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
            long perfStart = Perf.Start();
            try
            {
                OnUpdateTimed();
            }
            finally
            {
                Perf.Stop("Level-up swaps", perfStart);
            }
        }

        // TEMPORARY (perf-check): the original OnUpdate, timed by the wrapper above.
        private void OnUpdateTimed()
        {
            m_Frame++;
            bool haveMaterials = !m_MaterialsQuery.IsEmpty;
            bool haveConstruction = !m_UnderConstructionQuery.IsEmpty;
            if (!haveMaterials && m_MaterialsChecked.Count > 0)
            {
                m_MaterialsChecked.Clear();
            }

            if (haveMaterials || haveConstruction || m_Awaiting.Count > 0)
            {
                var tree = m_ZoneSearch.GetSearchTree(true, out JobHandle deps);
                deps.Complete();
                try
                {
                    if (haveMaterials)
                    {
                        CheckMaterialRequests(tree);
                    }

                    if (m_Awaiting.Count > 0)
                    {
                        CheckMissedLevelUps(tree);
                    }

                    if (haveConstruction)
                    {
                        CheckLevelUps(tree);
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
                }
                finally
                {
                    m_ZoneSearch.AddSearchTreeReader(default(JobHandle));
                }
            }

            if (m_BlockedCount > 0 && (System.DateTime.UtcNow - m_LastBlockedLog).TotalSeconds >= 60)
            {
                Mod.log.Info($"LevelUp: {m_BlockedCount} buildings kept their level because their zone allows no next level that fits (e.g. {m_BlockedSample}); no materials were bought for them.");
                m_BlockedCount = 0;
                m_LastBlockedLog = System.DateTime.UtcNow;
            }
        }

        /// <summary>Point 1: cancel material requests for level-ups the zone does not allow.</summary>
        private void CheckMaterialRequests(NativeQuadTree<Entity, Bounds2> tree)
        {
            var entities = m_MaterialsQuery.ToEntityArray(Allocator.Temp);
            try
            {
                if (m_MaterialsChecked.Count > 0)
                {
                    var present = new HashSet<Entity>();
                    for (int i = 0; i < entities.Length; i++)
                    {
                        present.Add(entities[i]);
                    }

                    m_MaterialsChecked.RemoveWhere(e => !present.Contains(e));
                }

                for (int i = 0; i < entities.Length; i++)
                {
                    Entity building = entities[i];
                    if (!m_MaterialsChecked.Add(building))
                    {
                        continue;
                    }

                    Entity current = EntityManager.GetComponentData<PrefabRef>(building).m_Prefab;
                    if (!TryGetContext(tree, building, current, out BuildingInfo from, out AllowListSystem.ZoneRules zone, out float maxHeight))
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
        private void CheckMissedLevelUps(NativeQuadTree<Entity, Bounds2> tree)
        {
            var done = new List<Entity>();
            var arrived = new List<Entity>();
            foreach (var kv in m_Awaiting)
            {
                Entity building = kv.Key;
                // A bulldozed building can linger for a frame as an entity without its components
                // (seen 2026-11-11: no PrefabRef), so check before reading anything.
                if (!EntityManager.Exists(building) || EntityManager.HasComponent<Deleted>(building) ||
                    !EntityManager.HasComponent<PrefabRef>(building) || !EntityManager.HasComponent<Game.Objects.Transform>(building) ||
                    EntityManager.HasComponent<UnderConstruction>(building) ||
                    EntityManager.GetComponentData<PrefabRef>(building).m_Prefab != kv.Value.prefab)
                {
                    done.Add(building); // gone, or the game's level-up is under way (handled by CheckLevelUps)
                    continue;
                }

                if (EntityManager.HasBuffer<ResourceNeeding>(building))
                {
                    continue; // still gathering materials
                }

                if (kv.Value.arrivedFrame < 0)
                {
                    arrived.Add(building);
                }
                else if (m_Frame - kv.Value.arrivedFrame >= kMissedLevelUpDelay)
                {
                    done.Add(building);
                    StartLevelUp(tree, building, kv.Value.prefab);
                }
            }

            foreach (Entity building in arrived)
            {
                m_Awaiting[building] = (m_Awaiting[building].prefab, m_Frame);
            }

            foreach (Entity building in done)
            {
                m_Awaiting.Remove(building);
            }
        }

        private void StartLevelUp(NativeQuadTree<Entity, Bounds2> tree, Entity building, Entity current)
        {
            if (EntityManager.HasComponent<Abandoned>(building) || EntityManager.HasComponent<Destroyed>(building) || EntityManager.HasComponent<Condemned>(building) ||
                !TryGetContext(tree, building, current, out BuildingInfo from, out AllowListSystem.ZoneRules zone, out float maxHeight))
            {
                return;
            }

            Entity target = ChooseNext(current, from, Entity.Null, zone, maxHeight);
            if (target == Entity.Null)
            {
                return;
            }

            EntityManager.AddComponentData(building, new UnderConstruction { m_NewPrefab = target, m_Progress = byte.MaxValue });
            if (TryGetConfig(out BuildingConfigurationData config))
            {
                m_IconCommandSystem.CreateCommandBuffer().Add(building, config.m_LevelUpNotification, IconPriority.Info, IconClusterLayer.Transaction);
            }

            QueueBuiltLevel(current, from.Level, from.Level + 1);
            Mod.log.Info($"LevelUp: '{from.Name}' in '{zone.Name}' becomes '{m_AllowList.GetInfo(target)?.Name}' (the game found nothing in its home zone, so the zone's choice was started).");
        }

        /// <summary>Point 2: replace or cancel the game's level-up choice.</summary>
        private void CheckLevelUps(NativeQuadTree<Entity, Bounds2> tree)
        {
            // Level-ups only: their new prefab differs from the current one (new buildings
            // under construction have none).
            var pending = new List<(Entity building, UnderConstruction construction, Entity current)>();
            var entities = m_UnderConstructionQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity building = entities[i];
                    var construction = EntityManager.GetComponentData<UnderConstruction>(building);
                    Entity current = EntityManager.GetComponentData<PrefabRef>(building).m_Prefab;
                    if (construction.m_NewPrefab != Entity.Null && construction.m_NewPrefab != current)
                    {
                        pending.Add((building, construction, current));
                    }
                }
            }
            finally
            {
                entities.Dispose();
            }

            foreach (var (building, construction, current) in pending)
            {
                m_Awaiting.Remove(building);
                if (TryGetContext(tree, building, current, out BuildingInfo from, out AllowListSystem.ZoneRules zone, out float maxHeight))
                {
                    Apply(building, construction, current, from, zone, maxHeight);
                }
            }
        }

        /// <summary>The building's index entry and the one of our zones it stands on; false if not on ours or plopped.</summary>
        private bool TryGetContext(NativeQuadTree<Entity, Bounds2> tree, Entity building, Entity current, out BuildingInfo from, out AllowListSystem.ZoneRules zone, out float maxHeight)
        {
            zone = null;
            maxHeight = float.PositiveInfinity;
            from = m_AllowList.GetInfo(current);
            if (from == null || !EntityManager.HasComponent<BuildingData>(current) || PlopCompat.IsPlopped(EntityManager, building))
            {
                return false;
            }

            var lot = EntityManager.GetComponentData<BuildingData>(current).m_LotSize;
            var transform = EntityManager.GetComponentData<Transform>(building);
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
                Cancel(building, current, from, gameInfo);
                Mod.log.Info($"LevelUp: '{from.Name}' in '{zone.Name}' keeps level {from.Level}; the zone allows no level {from.Level + 1} building that fits (the game chose '{gameInfo?.Name}').");
                return;
            }

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
