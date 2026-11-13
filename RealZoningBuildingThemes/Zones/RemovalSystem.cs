using System.Collections.Generic;
using Colossal.Serialization.Entities;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Notifications;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Transform = Game.Objects.Transform;

namespace RealZoningBuildingThemes.Zones
{
    /// <summary>
    /// The removal pass. When the rules change (the zone file is reloaded, which stands in for
    /// the panel's Apply button for now) and once after each city loads, every grown building is
    /// checked against the zones under its lot. A building standing on one of our zones that no
    /// longer allows it is condemned, with the game's own condemned icon, so the game demolishes
    /// it and the spawner grows an allowed building in its place (decision: removal replaces
    /// grandfathering).
    ///
    /// The pardon pass never lifts these condemnations, because it only pardons buildings whose
    /// every lot cell allows them. Plop the Growables' plopped buildings and signature buildings
    /// are left alone. The check is spread over several frames to avoid a stutter in big cities.
    /// </summary>
    public partial class RemovalSystem : GameSystemBase
    {
        private const int kBatchSize = 4000;

        private EntityQuery m_BuildingQuery;
        private EntityQuery m_BuildingConfigQuery;
        private AllowListSystem m_AllowList;
        private Game.Zones.SearchSystem m_ZoneSearch;
        private IconCommandSystem m_IconCommandSystem;

        private int m_CheckedVersion = -1;
        private readonly List<Entity> m_Pending = new List<Entity>();
        private int m_Next;
        private int m_OnOurZones;
        private readonly Dictionary<string, int> m_RemovedByZone = new Dictionary<string, int>();
        private readonly List<string> m_Examples = new List<string>();
        private System.Diagnostics.Stopwatch m_Timer;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_BuildingQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Building>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Condemned>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });
            m_BuildingConfigQuery = GetEntityQuery(ComponentType.ReadOnly<BuildingConfigurationData>());
            m_AllowList = World.GetOrCreateSystemManaged<AllowListSystem>();
            m_ZoneSearch = World.GetOrCreateSystemManaged<Game.Zones.SearchSystem>();
            m_IconCommandSystem = World.GetOrCreateSystemManaged<IconCommandSystem>();
        }

        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);

            // A city saved under other rules: check it against the current ones.
            m_CheckedVersion = -1;
            m_Pending.Clear();
            m_Next = 0;
        }

        protected override void OnUpdate()
        {
            if (m_Next >= m_Pending.Count)
            {
                if (m_AllowList.RulesVersion == 0 || m_AllowList.RulesVersion == m_CheckedVersion)
                {
                    return;
                }

                Start();
            }

            int end = System.Math.Min(m_Next + kBatchSize, m_Pending.Count);
            var tree = m_ZoneSearch.GetSearchTree(true, out JobHandle deps);
            deps.Complete();
            try
            {
                IconCommandBuffer icons = default;
                bool haveIcons = false;
                Entity condemnedIcon = Entity.Null;

                for (; m_Next < end; m_Next++)
                {
                    Entity building = m_Pending[m_Next];
                    if (!TryFindDisallowingZone(tree, building, out AllowListSystem.ZoneRules zone))
                    {
                        continue;
                    }

                    if (!haveIcons)
                    {
                        icons = m_IconCommandSystem.CreateCommandBuffer();
                        condemnedIcon = m_BuildingConfigQuery.GetSingleton<BuildingConfigurationData>().m_CondemnedNotification;
                        haveIcons = true;
                    }

                    // As the game's zone check condemns: no icon on abandoned or destroyed buildings.
                    EntityManager.AddComponent<Condemned>(building);
                    if (!EntityManager.HasComponent<Destroyed>(building) && !EntityManager.HasComponent<Abandoned>(building))
                    {
                        icons.Add(building, condemnedIcon, IconPriority.FatalProblem);
                    }

                    m_RemovedByZone.TryGetValue(zone.Name, out int n);
                    m_RemovedByZone[zone.Name] = n + 1;
                    if (m_Examples.Count < 15)
                    {
                        Entity prefab = EntityManager.GetComponentData<PrefabRef>(building).m_Prefab;
                        BuildingInfo info = m_AllowList.GetInfo(prefab);
                        m_Examples.Add(info != null
                            ? $"'{info.Name}' in '{zone.Name}': {BuildingIndex.Explain(zone.Definition, info)}"
                            : $"'{World.GetOrCreateSystemManaged<PrefabSystem>().GetPrefabName(prefab)}' in '{zone.Name}': not in the building index");
                    }
                }
            }
            finally
            {
                m_ZoneSearch.AddSearchTreeReader(default(JobHandle));
            }

            if (m_Next >= m_Pending.Count)
            {
                Finish();
            }
        }

        private void Start()
        {
            m_CheckedVersion = m_AllowList.RulesVersion;
            m_Pending.Clear();
            m_Next = 0;
            m_OnOurZones = 0;
            m_RemovedByZone.Clear();
            m_Examples.Clear();
            m_Timer = System.Diagnostics.Stopwatch.StartNew();

            var entities = m_BuildingQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    m_Pending.Add(entities[i]);
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void Finish()
        {
            int removed = 0;
            var parts = new List<string>();
            foreach (var kv in m_RemovedByZone)
            {
                removed += kv.Value;
                parts.Add($"{kv.Key}: {kv.Value}");
            }

            string detail = parts.Count > 0 ? $" ({string.Join(", ", parts)})" : string.Empty;
            Mod.log.Info($"Removal: checked {m_Pending.Count} buildings in {m_Timer.ElapsedMilliseconds} ms; {m_OnOurZones} stand on our zones; condemned {removed} the rules no longer allow{detail}.");
            foreach (string example in m_Examples)
            {
                Mod.log.Info($"Removal:   {example}");
            }
            m_Pending.Clear();
            m_Next = 0;
        }

        /// <summary>True when the building stands on one of our zones that does not allow it.</summary>
        private bool TryFindDisallowingZone(Colossal.Collections.NativeQuadTree<Entity, Colossal.Mathematics.Bounds2> tree, Entity building, out AllowListSystem.ZoneRules zone)
        {
            zone = null;
            // The check is spread over frames, so a building may have been bulldozed since it was listed.
            if (!EntityManager.Exists(building) || EntityManager.HasComponent<Deleted>(building) || EntityManager.HasComponent<Condemned>(building) ||
                !EntityManager.HasComponent<PrefabRef>(building) || !EntityManager.HasComponent<Transform>(building))
            {
                return false;
            }

            Entity prefab = EntityManager.GetComponentData<PrefabRef>(building).m_Prefab;
            if (!EntityManager.HasComponent<SpawnableBuildingData>(prefab) ||
                !EntityManager.HasComponent<BuildingData>(prefab) ||
                EntityManager.HasComponent<SignatureBuildingData>(prefab) ||
                PlopCompat.IsPlopped(EntityManager, building))
            {
                return false;
            }

            var lot = EntityManager.GetComponentData<BuildingData>(prefab).m_LotSize;
            var transform = EntityManager.GetComponentData<Transform>(building);
            bool onOurs = false;
            using (LotCells cells = LotCells.Read(EntityManager, tree, transform, lot))
            {
                for (int i = 0; i < cells.Zones.Length; i++)
                {
                    if (cells.Zones[i] == 0 || !m_AllowList.TryGetZone(cells.Zones[i], out var rules))
                    {
                        continue;
                    }

                    onOurs = true;
                    if (!rules.AllowedSet.Contains(prefab))
                    {
                        zone = rules;
                        break;
                    }
                }
            }

            if (onOurs)
            {
                m_OnOurZones++;
            }

            return zone != null;
        }
    }
}
