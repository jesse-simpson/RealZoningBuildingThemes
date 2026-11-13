// Approach adapted from Custom Zones' LevelUpControlSystem.
// Copyright (c) 2026 dvovo. Licensed under the MIT License (LICENSES/MIT-CustomZones.txt).
// Changed for Real Zoning & Building Themes: next levels chosen from our zones' allow lists,
// same-design preference, missed level-ups started by this system, and guards for deleted buildings.

using System.Collections.Generic;
using Colossal.Collections;
using Colossal.Mathematics;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Notifications;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Transform = Game.Objects.Transform;

namespace RealZoningBuildingThemes.Zones
{
    /// <summary>
    /// Keeps level-ups inside the zone's allow-list.
    ///
    /// How the game levels a building up (BuildingUpkeepSystem): its condition grows each upkeep
    /// tick; at the levelling cost it asks for construction materials (a ResourceNeeding buffer
    /// and the "levelling" icon); once they are delivered its condition goes back to 0 and it
    /// picks, at random, any building of its home zone one level up with the same lot size, road
    /// access and goods, at least as many homes, and no taller than the cells allow. It then adds
    /// UnderConstruction with that prefab, and BuildingConstructionSystem swaps the prefab. A
    /// top-level building goes through the same cycle and simply finds nothing to become.
    ///
    /// For buildings on one of our zones, this system steps in at three points:
    /// 1. Materials: when a building asks for materials and the zone allows no next level that
    ///    fits, the request is cancelled before any truck is sent and the condition goes back to
    ///    0, exactly where a delivered level-up would leave it. The building then behaves like a
    ///    top-level building: it saves up again and is checked again next time, so no materials
    ///    are wasted and nothing loops faster than the game's own level-up pace. If the rules
    ///    later allow a next level, that next check lets it through.
    /// 2. The swap: the game's choice is replaced by one the zone allows (the building's own
    ///    design at the next level when the zone prefers the same design, otherwise the game's
    ///    choice if allowed, otherwise another allowed building that fits by the same rules).
    ///    With none (rules changed while materials were on their way), the level-up is cancelled.
    /// 3. Missed level-ups: when the materials arrive but the game found nothing in the home zone
    ///    to become, while our zone has something, this system starts the level-up itself.
    ///
    /// Approach adapted from LevelUpControlSystem in Custom Zones by dvovo (MIT licence).
    /// </summary>
    public partial class LevelUpSwapSystem : GameSystemBase
    {
        // LeftAccess | RightAccess: the flags the game matches when it levels up.
        private const Game.Prefabs.BuildingFlags kAccessFlags = Game.Prefabs.BuildingFlags.LeftAccess | Game.Prefabs.BuildingFlags.RightAccess;

        // Frames to wait after materials arrive before deciding the game started no level-up
        // (its choice is applied through a command buffer, possibly a frame or two later).
        private const int kMissedLevelUpDelay = 8;

        private EntityQuery m_UnderConstructionQuery;
        private EntityQuery m_MaterialsQuery;
        private EntityQuery m_BuildingConfigQuery;
        private AllowListSystem m_AllowList;
        private Game.Zones.SearchSystem m_ZoneSearch;
        private IconCommandSystem m_IconCommandSystem;
        private ZoneBuiltRequirementSystem m_ZoneBuiltRequirementSystem;
        private Unity.Mathematics.Random m_Random;
        private int m_Frame;

        // Buildings whose materials request was looked at (until the request ends).
        private readonly HashSet<Entity> m_MaterialsChecked = new HashSet<Entity>();

        // Buildings on our zones gathering materials for a level-up the zone allows, with their
        // prefab then and the frame the materials arrived (-1 while still gathering).
        private readonly Dictionary<Entity, (Entity prefab, int arrivedFrame)> m_Awaiting = new Dictionary<Entity, (Entity, int)>();

        // Level-ups already looked at, with the prefab they were left (or set) to; they are not
        // looked at again unless something changes that prefab. Pruned now and then.
        private readonly Dictionary<Entity, Entity> m_Approved = new Dictionary<Entity, Entity>();

        // Reused list, so the per-update checks allocate nothing.
        private readonly List<(Entity building, UnderConstruction construction, Entity current)> m_Pending = new List<(Entity, UnderConstruction, Entity)>();

        // The game's zone search tree, fetched only when a decision needs it and at most once
        // per update: fetching it waits for the game's zone jobs, which is the costly part.
        private NativeQuadTree<Entity, Bounds2> m_Tree;
        private bool m_HaveTree;

        // The waiting list and the remembered buildings can hold thousands of buildings, so each
        // update looks at only a slice of them, in rotation: the cost per update stays small and
        // flat however big the city. A missed level-up may start a little later (well under a
        // second of game time in a large city); level-ups take minutes anyway.
        private const int kMissedPerUpdate = 48;
        private const int kMaterialsInterval = 4;
        private const int kPrunePerUpdate = 64;
        private readonly List<Entity> m_AwaitScan = new List<Entity>();
        private int m_AwaitScanPos;
        private readonly List<Entity> m_MaterialsScan = new List<Entity>();
        private int m_MaterialsScanPos;
        private readonly List<Entity> m_ApprovedScan = new List<Entity>();
        private int m_ApprovedScanPos;

        // Blocked level-ups, summarised in the log about once a minute.
        private int m_BlockedCount;
        private string m_BlockedSample;
        private System.DateTime m_LastBlockedLog = System.DateTime.MinValue;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_UnderConstructionQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Building>(),
                    ComponentType.ReadWrite<UnderConstruction>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });

            // Only chunks whose construction state changed (new level-ups) are looked at.
            m_UnderConstructionQuery.SetChangedVersionFilter(ComponentType.ReadWrite<UnderConstruction>());

            m_MaterialsQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Building>(),
                    ComponentType.ReadOnly<ResourceNeeding>(),
                    ComponentType.ReadOnly<BuildingCondition>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });

            m_BuildingConfigQuery = GetEntityQuery(ComponentType.ReadOnly<BuildingConfigurationData>());
            m_AllowList = World.GetOrCreateSystemManaged<AllowListSystem>();
            m_ZoneSearch = World.GetOrCreateSystemManaged<Game.Zones.SearchSystem>();
            m_IconCommandSystem = World.GetOrCreateSystemManaged<IconCommandSystem>();
            m_ZoneBuiltRequirementSystem = World.GetOrCreateSystemManaged<ZoneBuiltRequirementSystem>();
            m_Random = new Unity.Mathematics.Random(0x52A7B1u);
        }

        private System.DateTime m_LastErrorLog = System.DateTime.MinValue;

        protected override void OnUpdate()
        {
            m_Frame++;
            if (m_MaterialsQuery.IsEmptyIgnoreFilter && m_MaterialsChecked.Count > 0)
            {
                m_MaterialsChecked.Clear();
            }

            // New materials requests are looked for every few updates by listing the buildings
            // that have one and skipping those already looked at. A change filter here made every
            // update wait for the game's delivery jobs to finish writing the requests.
            bool haveMaterials = m_Frame % kMaterialsInterval == 0 && !m_MaterialsQuery.IsEmptyIgnoreFilter;
            bool haveConstruction = !m_UnderConstructionQuery.IsEmpty;  // changed chunks only
            bool checkMissed = m_Awaiting.Count > 0;

            PruneSlice();

            if (haveMaterials || haveConstruction || checkMissed)
            {
                m_HaveTree = false;
                try
                {
                    if (haveMaterials)
                    {
                        CheckMaterialRequests();
                    }

                    if (checkMissed)
                    {
                        CheckMissedLevelUps();
                    }

                    if (haveConstruction)
                    {
                        CheckLevelUps();
                    }
                }
                catch (System.Exception e)
                {
                    // One bad entity must not stop level-ups for the whole city: log it (once a
                    // minute at most), drop the waiting list and carry on next frame.
                    if ((System.DateTime.UtcNow - m_LastErrorLog).TotalSeconds >= 60)
                    {
                        Mod.log.Warn($"LevelUp: skipped a frame after an error ({e.GetType().Name}: {e.Message}).\n{e.StackTrace}");
                        m_LastErrorLog = System.DateTime.UtcNow;
                    }

                    m_Awaiting.Clear();
                    m_AwaitScan.Clear();
                    m_AwaitScanPos = 0;
                }
                finally
                {
                    if (m_HaveTree)
                    {
                        m_ZoneSearch.AddSearchTreeReader(default(JobHandle));
                        m_HaveTree = false;
                    }
                }
            }

            if (m_BlockedCount > 0 && (System.DateTime.UtcNow - m_LastBlockedLog).TotalSeconds >= 60)
            {
                Mod.log.Info($"LevelUp: {m_BlockedCount} buildings kept their level because their zone allows no next level that fits (e.g. {m_BlockedSample}); no materials were bought for them.");
                m_BlockedCount = 0;
                m_LastBlockedLog = System.DateTime.UtcNow;
            }
        }

        /// <summary>The game's zone search tree, fetched on first use in an update.</summary>
        private NativeQuadTree<Entity, Bounds2> Tree
        {
            get
            {
                if (!m_HaveTree)
                {
                    m_Tree = m_ZoneSearch.GetSearchTree(true, out JobHandle deps);
                    deps.Complete();
                    m_HaveTree = true;
                }

                return m_Tree;
            }
        }

        /// <summary>Forgets, a slice at a time, buildings whose materials request or level-up has ended.</summary>
        private void PruneSlice()
        {
            if (m_MaterialsScanPos >= m_MaterialsScan.Count)
            {
                m_MaterialsScan.Clear();
                m_MaterialsScan.AddRange(m_MaterialsChecked);
                m_MaterialsScanPos = 0;
            }

            for (int n = 0; n < kPrunePerUpdate && m_MaterialsScanPos < m_MaterialsScan.Count; n++, m_MaterialsScanPos++)
            {
                Entity building = m_MaterialsScan[m_MaterialsScanPos];
                if (!EntityManager.Exists(building) || !EntityManager.HasBuffer<ResourceNeeding>(building))
                {
                    m_MaterialsChecked.Remove(building);
                }
            }

            if (m_ApprovedScanPos >= m_ApprovedScan.Count)
            {
                m_ApprovedScan.Clear();
                m_ApprovedScan.AddRange(m_Approved.Keys);
                m_ApprovedScanPos = 0;
            }

            for (int n = 0; n < kPrunePerUpdate && m_ApprovedScanPos < m_ApprovedScan.Count; n++, m_ApprovedScanPos++)
            {
                Entity building = m_ApprovedScan[m_ApprovedScanPos];
                if (!EntityManager.Exists(building) || !EntityManager.HasComponent<UnderConstruction>(building))
                {
                    m_Approved.Remove(building);
                }
            }
        }

        /// <summary>Point 1: cancel material requests for level-ups the zone does not allow.</summary>
        private void CheckMaterialRequests()
        {
            var entities = m_MaterialsQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity building = entities[i];
                    if (!m_MaterialsChecked.Add(building))
                    {
                        continue;
                    }

                    Entity current = EntityManager.GetComponentData<PrefabRef>(building).m_Prefab;
                    if (!TryGetContext(building, current, out BuildingInfo from, out AllowListSystem.ZoneRules zone, out float maxHeight))
                    {
                        continue;
                    }

                    if (ChooseNext(current, from, Entity.Null, zone, maxHeight) != Entity.Null)
                    {
                        m_Awaiting[building] = (current, -1);
                        continue;
                    }

                    // As at a top-level building: no materials, condition back to 0, saving up again.
                    EntityManager.RemoveComponent<ResourceNeeding>(building);
                    EntityManager.SetComponentData(building, new BuildingCondition { m_Condition = 0 });
                    if (TryGetConfig(out BuildingConfigurationData config))
                    {
                        m_IconCommandSystem.CreateCommandBuffer().Remove(building, config.m_LevelingBuildingNotificationPrefab);
                    }

                    m_MaterialsChecked.Remove(building);
                    m_Awaiting.Remove(building);
                    m_BlockedCount++;
                    m_BlockedSample = $"'{from.Name}' in '{zone.Name}'";
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        /// <summary>Point 3: materials arrived but the game started no level-up; start the zone's own.</summary>
        private void CheckMissedLevelUps()
        {
            if (m_AwaitScanPos >= m_AwaitScan.Count)
            {
                m_AwaitScan.Clear();
                m_AwaitScan.AddRange(m_Awaiting.Keys);
                m_AwaitScanPos = 0;
            }

            for (int n = 0; n < kMissedPerUpdate && m_AwaitScanPos < m_AwaitScan.Count; n++, m_AwaitScanPos++)
            {
                Entity building = m_AwaitScan[m_AwaitScanPos];
                if (!m_Awaiting.TryGetValue(building, out var entry))
                {
                    continue; // already settled since this round began
                }

                // A bulldozed building can linger for a frame as an entity without its components
                // (seen 2026-11-11: no PrefabRef), so check before reading anything.
                if (!EntityManager.Exists(building) || EntityManager.HasComponent<Deleted>(building) ||
                    !EntityManager.HasComponent<PrefabRef>(building) || !EntityManager.HasComponent<Game.Objects.Transform>(building) ||
                    EntityManager.HasComponent<UnderConstruction>(building) ||
                    EntityManager.GetComponentData<PrefabRef>(building).m_Prefab != entry.prefab)
                {
                    m_Awaiting.Remove(building); // gone, or the game's level-up is under way (handled by CheckLevelUps)
                    continue;
                }

                if (EntityManager.HasBuffer<ResourceNeeding>(building))
                {
                    continue; // still gathering materials
                }

                if (entry.arrivedFrame < 0)
                {
                    m_Awaiting[building] = (entry.prefab, m_Frame); // materials arrived: give the game its turn first
                }
                else if (m_Frame - entry.arrivedFrame >= kMissedLevelUpDelay)
                {
                    m_Awaiting.Remove(building);
                    StartLevelUp(building, entry.prefab);
                }
            }
        }

        private void StartLevelUp(Entity building, Entity current)
        {
            if (EntityManager.HasComponent<Abandoned>(building) || EntityManager.HasComponent<Destroyed>(building) || EntityManager.HasComponent<Condemned>(building) ||
                !TryGetContext(building, current, out BuildingInfo from, out AllowListSystem.ZoneRules zone, out float maxHeight))
            {
                return;
            }

            Entity target = ChooseNext(current, from, Entity.Null, zone, maxHeight);
            if (target == Entity.Null)
            {
                return;
            }

            EntityManager.AddComponentData(building, new UnderConstruction { m_NewPrefab = target, m_Progress = byte.MaxValue });
            m_Approved[building] = target;
            if (TryGetConfig(out BuildingConfigurationData config))
            {
                m_IconCommandSystem.CreateCommandBuffer().Add(building, config.m_LevelUpNotification, IconPriority.Info, IconClusterLayer.Transaction);
            }

            QueueBuiltLevel(current, from.Level, from.Level + 1);
            Mod.log.Info($"LevelUp: '{from.Name}' in '{zone.Name}' becomes '{m_AllowList.GetInfo(target)?.Name}' (the game found nothing in its home zone, so the zone's choice was started).");
        }

        /// <summary>Point 2: replace or cancel the game's level-up choice.</summary>
        private void CheckLevelUps()
        {
            var pending = m_Pending;
            pending.Clear();

            // Read the changed chunks in bulk (three copies) rather than building by building:
            // construction progress marks every building under construction as changed on every
            // update, and almost all of them are new buildings, not level-ups.
            var entities = m_UnderConstructionQuery.ToEntityArray(Allocator.Temp);
            var constructions = m_UnderConstructionQuery.ToComponentDataArray<UnderConstruction>(Allocator.Temp);
            var prefabs = m_UnderConstructionQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    UnderConstruction construction = constructions[i];
                    Entity current = prefabs[i].m_Prefab;

                    // Level-ups only: their new prefab differs from the current one (new buildings
                    // under construction have none). One already looked at is skipped unless its
                    // target prefab changed since.
                    if (construction.m_NewPrefab == Entity.Null || construction.m_NewPrefab == current)
                    {
                        continue;
                    }

                    Entity building = entities[i];
                    if (m_Approved.TryGetValue(building, out Entity approved) && approved == construction.m_NewPrefab)
                    {
                        continue;
                    }

                    pending.Add((building, construction, current));
                }
            }
            finally
            {
                entities.Dispose();
                constructions.Dispose();
                prefabs.Dispose();
            }

            foreach (var (building, construction, current) in pending)
            {
                m_Awaiting.Remove(building);
                if (TryGetContext(building, current, out BuildingInfo from, out AllowListSystem.ZoneRules zone, out float maxHeight))
                {
                    Apply(building, construction, current, from, zone, maxHeight);
                }
                else
                {
                    m_Approved[building] = construction.m_NewPrefab; // not on our zones (or plopped): the game's level-up stands
                }
            }
        }

        /// <summary>The building's index entry and the one of our zones it stands on; false if not on ours or plopped.</summary>
        private bool TryGetContext(Entity building, Entity current, out BuildingInfo from, out AllowListSystem.ZoneRules zone, out float maxHeight)
        {
            zone = null;
            maxHeight = float.PositiveInfinity;
            // A building Placemaker reshaped stands on a copy of its design, which is not in the
            // index: its level-ups are left to the game and Placemaker.
            from = m_AllowList.GetInfo(current);
            if (from == null || !EntityManager.HasComponent<BuildingData>(current) || PlopCompat.IsPlopped(EntityManager, building))
            {
                return false;
            }

            var lot = EntityManager.GetComponentData<BuildingData>(current).m_LotSize;
            var transform = EntityManager.GetComponentData<Transform>(building);
            NativeQuadTree<Entity, Bounds2> tree = Tree;
            using (LotCells cells = LotCells.Read(EntityManager, tree, transform, lot))
            {
                zone = OurZoneOf(cells);
                maxHeight = cells.MaxHeight;
            }

            return zone != null;
        }

        /// <summary>The one of our zones covering most of the lot, or null.</summary>
        private AllowListSystem.ZoneRules OurZoneOf(LotCells cells)
        {
            AllowListSystem.ZoneRules best = null;
            int bestCount = 0;
            for (int i = 0; i < cells.Zones.Length; i++)
            {
                ushort index = cells.Zones[i];
                if (index == 0 || (best != null && best.Index == index) || !m_AllowList.TryGetZone(index, out var rules))
                {
                    continue;
                }

                int count = 0;
                for (int j = 0; j < cells.Zones.Length; j++)
                {
                    if (cells.Zones[j] == index)
                    {
                        count++;
                    }
                }

                if (count > bestCount)
                {
                    best = rules;
                    bestCount = count;
                }
            }

            return best;
        }

        private void Apply(Entity building, UnderConstruction construction, Entity current, BuildingInfo from, AllowListSystem.ZoneRules zone, float maxHeight)
        {
            Entity gameChoice = construction.m_NewPrefab;
            BuildingInfo gameInfo = m_AllowList.GetInfo(gameChoice);
            Entity target = ChooseNext(current, from, gameChoice, zone, maxHeight);

            if (target == Entity.Null)
            {
                m_Approved.Remove(building);
                Cancel(building, current, from, gameInfo);
                Mod.log.Info($"LevelUp: '{from.Name}' in '{zone.Name}' keeps level {from.Level}; the zone allows no level {from.Level + 1} building that fits (the game chose '{gameInfo?.Name}').");
                return;
            }

            m_Approved[building] = target;
            if (target == gameChoice)
            {
                return; // the game's choice is allowed and preferred
            }

            construction.m_NewPrefab = target;
            EntityManager.SetComponentData(building, construction);
            Mod.log.Info($"LevelUp: '{from.Name}' in '{zone.Name}' becomes '{m_AllowList.GetInfo(target)?.Name}' instead of '{gameInfo?.Name}'.");
        }

        /// <summary>
        /// The building one level up that the zone allows: with "prefer same design", the
        /// building's own design; then the game's own choice; then any other, at random. All must
        /// fit as the game's choice does. Entity.Null when there is none.
        /// </summary>
        private Entity ChooseNext(Entity current, BuildingInfo from, Entity gameChoice, AllowListSystem.ZoneRules zone, float maxHeight)
        {
            if (zone.Definition == null || from.Level >= 5)
            {
                return Entity.Null;
            }

            bool sameDesign = zone.Definition.PreferSameDesign;
            var candidates = new List<Entity>();
            var own = new List<Entity>();
            bool gameChoiceOk = false;
            foreach (BuildingInfo b in zone.Allowed)
            {
                if (b.Level != from.Level + 1 || !Fits(current, b.Entity, maxHeight))
                {
                    continue;
                }

                candidates.Add(b.Entity);
                if (b.Family == from.Family)
                {
                    own.Add(b.Entity);
                }

                if (b.Entity == gameChoice)
                {
                    gameChoiceOk = true;
                }
            }

            if (sameDesign && own.Count > 0)
            {
                return own.Contains(gameChoice) ? gameChoice : own[m_Random.NextInt(own.Count)];
            }

            if (gameChoiceOk)
            {
                return gameChoice;
            }

            return candidates.Count == 0 ? Entity.Null : candidates[m_Random.NextInt(candidates.Count)];
        }

        /// <summary>The game's level-up test (BuildingUpkeepSystem.SelectSpawnableBuilding), zone type aside.</summary>
        private bool Fits(Entity current, Entity next, float maxHeight)
        {
            if (!EntityManager.HasComponent<BuildingData>(next) || !EntityManager.HasComponent<ObjectGeometryData>(next))
            {
                return false;
            }

            var a = EntityManager.GetComponentData<BuildingData>(current);
            var b = EntityManager.GetComponentData<BuildingData>(next);
            if (!a.m_LotSize.Equals(b.m_LotSize) || (a.m_Flags & kAccessFlags) != (b.m_Flags & kAccessFlags))
            {
                return false;
            }

            if (EntityManager.GetComponentData<ObjectGeometryData>(next).m_Size.y > maxHeight)
            {
                return false;
            }

            bool hasA = EntityManager.HasComponent<BuildingPropertyData>(current);
            bool hasB = EntityManager.HasComponent<BuildingPropertyData>(next);
            if (hasA != hasB)
            {
                return false;
            }

            if (!hasA)
            {
                return true;
            }

            var pa = EntityManager.GetComponentData<BuildingPropertyData>(current);
            var pb = EntityManager.GetComponentData<BuildingPropertyData>(next);
            return pa.m_ResidentialProperties <= pb.m_ResidentialProperties &&
                pa.m_AllowedManufactured == pb.m_AllowedManufactured &&
                pa.m_AllowedInput == pb.m_AllowedInput &&
                pa.m_AllowedSold == pb.m_AllowedSold &&
                pa.m_AllowedStored == pb.m_AllowedStored;
        }

        /// <summary>
        /// Undoes the game's level-up: the building keeps its prefab, the "level up" icon goes,
        /// and the zone statistics the game moved up one level (used for unlocks) move back.
        /// </summary>
        private void Cancel(Entity building, Entity current, BuildingInfo from, BuildingInfo gameInfo)
        {
            EntityManager.RemoveComponent<UnderConstruction>(building);
            if (TryGetConfig(out BuildingConfigurationData config))
            {
                m_IconCommandSystem.CreateCommandBuffer().Remove(building, config.m_LevelUpNotification);
            }

            if (gameInfo != null)
            {
                QueueBuiltLevel(current, gameInfo.Level, from.Level);
            }
        }

        /// <summary>Moves one building between levels in the game's zone statistics (what unlocks count), as its own level-up does.</summary>
        private void QueueBuiltLevel(Entity prefab, int fromLevel, int toLevel)
        {
            if (fromLevel == toLevel || !EntityManager.HasComponent<SpawnableBuildingData>(prefab) || !EntityManager.HasComponent<BuildingData>(prefab))
            {
                return;
            }

            try
            {
                var spawnable = EntityManager.GetComponentData<SpawnableBuildingData>(prefab);
                var lot = EntityManager.GetComponentData<BuildingData>(prefab).m_LotSize;
                var queue = m_ZoneBuiltRequirementSystem.GetZoneBuiltLevelQueue(out JobHandle dependency);
                dependency.Complete();
                queue.Enqueue(new ZoneBuiltLevelUpdate
                {
                    m_Zone = spawnable.m_ZonePrefab,
                    m_FromLevel = fromLevel,
                    m_ToLevel = toLevel,
                    m_Squares = lot.x * lot.y,
                });
            }
            catch (System.Exception e)
            {
                Mod.log.Warn($"LevelUp: could not update the zone statistics ({e.Message}).");
            }
        }

        private bool TryGetConfig(out BuildingConfigurationData config)
        {
            config = default;
            if (m_BuildingConfigQuery.IsEmptyIgnoreFilter)
            {
                return false;
            }

            config = m_BuildingConfigQuery.GetSingleton<BuildingConfigurationData>();
            return true;
        }
    }
}
