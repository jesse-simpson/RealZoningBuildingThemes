using System.Collections.Generic;
using System.Linq;
using Game;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;

namespace RealZoningBuildingThemes.Zones
{
    /// <summary>
    /// Resolves which building prefabs each of our zones allows: every growable is indexed once
    /// (<see cref="BuildingIndex"/>), then each zone's rules from the zone library are applied. The
    /// result goes into the zone's <see cref="RzbtAllowedBuilding"/> buffer (read by the spawner)
    /// and a lookup from building prefab to allowing zone type indices (read by the pardon pass).
    /// Rebuilds when buildings or zones are added, or when the zone file is reloaded.
    /// </summary>
    public partial class AllowListSystem : GameSystemBase
    {
        /// <summary>One of our zones as the rules last resolved it.</summary>
        public class ZoneRules
        {
            public ushort Index;
            public string PrefabName;

            /// <summary>Null when the zone was removed from zones.json (it then allows nothing until restart).</summary>
            public ZoneDefinition Definition;

            public string Name => Definition != null ? Definition.Name : PrefabName;

            public readonly List<BuildingInfo> Allowed = new List<BuildingInfo>();
            public readonly HashSet<Entity> AllowedSet = new HashSet<Entity>();
        }

        private static bool s_RebuildRequested;
        private int m_Updates;

        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_BuildingQuery;
        private EntityQuery m_OurZonesQuery;
        private int m_LastBuildingCount = -1;
        private int m_LastZoneCount = -1;

        private readonly List<BuildingInfo> m_Index = new List<BuildingInfo>();
        private readonly Dictionary<Entity, List<ushort>> m_ZonesByBuilding = new Dictionary<Entity, List<ushort>>();
        private readonly Dictionary<Entity, BuildingInfo> m_InfoByEntity = new Dictionary<Entity, BuildingInfo>();
        private readonly Dictionary<ushort, ZoneRules> m_RulesByIndex = new Dictionary<ushort, ZoneRules>();

        /// <summary>Every growable, as last indexed (empty until a city has loaded).</summary>
        public IReadOnlyList<BuildingInfo> Index => m_Index;

        /// <summary>Goes up each time the building index is rebuilt; the zone panel watches it.</summary>
        public int IndexVersion { get; private set; }

        /// <summary>Goes up each time the rules are applied; the removal pass watches it.</summary>
        public int RulesVersion { get; private set; }

        /// <summary>Called after the zone file is reloaded.</summary>
        public static void RequestRebuild()
        {
            s_RebuildRequested = true;
        }

        /// <summary>Every building prefab on any of our allow-lists, with the zone type indices that allow it; the zone check copies it for its job.</summary>
        public IReadOnlyDictionary<Entity, List<ushort>> ZonesByBuilding => m_ZonesByBuilding;

        /// <summary>Zone type indices of our zones that allow this building prefab, or null.</summary>
        public List<ushort> GetAllowingZones(Entity buildingPrefab)
        {
            return m_ZonesByBuilding.TryGetValue(buildingPrefab, out var zones) ? zones : null;
        }

        /// <summary>The index entry of a growable prefab, or null.</summary>
        public BuildingInfo GetInfo(Entity buildingPrefab)
        {
            return m_InfoByEntity.TryGetValue(buildingPrefab, out var info) ? info : null;
        }

        /// <summary>Our zones as the rules last resolved them (empty until a city has loaded).</summary>
        public IEnumerable<ZoneRules> AllZones => m_RulesByIndex.Values;

        /// <summary>Our zone with this zone type index, or false for a game zone.</summary>
        public bool TryGetZone(ushort zoneIndex, out ZoneRules rules)
        {
            return m_RulesByIndex.TryGetValue(zoneIndex, out rules);
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_BuildingQuery = GetEntityQuery(
                ComponentType.ReadOnly<PrefabData>(),
                ComponentType.ReadOnly<BuildingData>(),
                ComponentType.ReadOnly<SpawnableBuildingData>());
            m_OurZonesQuery = GetEntityQuery(
                ComponentType.ReadOnly<RzbtZone>(),
                ComponentType.ReadOnly<ZoneData>(),
                ComponentType.ReadWrite<RzbtAllowedBuilding>());
        }

        protected override void OnUpdate()
        {
            long perfStart = Perf.Start();
            try
            {
                OnUpdateTimed();
            }
            finally
            {
                Perf.Stop("Allow-lists", perfStart);
            }
        }

        // TEMPORARY (perf-check): the original OnUpdate, timed by the wrapper above.
        private void OnUpdateTimed()
        {
            // Counting every building prefab is the costly part, and new prefabs (assets, zones made
            // in the panel) only appear now and then: look every 16 updates, at once on request.
            if (!s_RebuildRequested && (++m_Updates & 15) != 0)
            {
                return;
            }

            int buildings = m_BuildingQuery.CalculateEntityCount();
            int zones = m_OurZonesQuery.CalculateEntityCount();
            bool buildingsChanged = buildings != m_LastBuildingCount;
            if (!buildingsChanged && zones == m_LastZoneCount && !s_RebuildRequested)
            {
                return;
            }

            m_LastBuildingCount = buildings;
            m_LastZoneCount = zones;
            s_RebuildRequested = false;

            if (buildingsChanged)
            {
                RebuildIndex();
            }

            ApplyRules();
        }

        private void RebuildIndex()
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            m_Index.Clear();
            m_InfoByEntity.Clear();
            var entities = m_BuildingQuery.ToEntityArray(Allocator.Temp);
            try
            {
                int failed = 0;
                int hubs = 0;
                var hubsBy = new Dictionary<string, int>();
                var hubExample = new Dictionary<string, string>();
                for (int i = 0; i < entities.Length; i++)
                {
                    BuildingInfo info;
                    try
                    {
                        info = BuildingIndex.Build(EntityManager, m_PrefabSystem, entities[i]);
                    }
                    catch (System.Exception e)
                    {
                        // A broken custom asset must not take the whole index down with it.
                        if (failed++ < 10)
                        {
                            Mod.log.Warn($"AllowList: skipped '{m_PrefabSystem.GetPrefabName(entities[i])}' ({e.GetType().Name}: {e.Message}).");
                        }

                        continue;
                    }

                    string hubReason = BuildingIndex.HubReason(info);
                    if (hubReason != null)
                    {
                        // Belongs next to an extraction area, not on ordinary zoning. Counted per
                        // reason and home zone for the log, so a wrong catch is easy to spot.
                        hubs++;
                        string key = $"{hubReason}: {(string.IsNullOrEmpty(info.HomeZone) ? "(none)" : info.HomeZone)}";
                        hubsBy[key] = hubsBy.TryGetValue(key, out int seen) ? seen + 1 : 1;
                        if (!hubExample.ContainsKey(key))
                        {
                            hubExample[key] = info.Name;
                        }

                        continue;
                    }

                    m_Index.Add(info);
                    m_InfoByEntity[info.Entity] = info;
                }

                if (failed > 10)
                {
                    Mod.log.Warn($"AllowList: skipped {failed} buildings in all.");
                }

                if (hubs > 0)
                {
                    Mod.log.Info($"AllowList: left out {hubs} specialized-industry hub buildings (they belong next to extraction areas):");
                    foreach (var kv in hubsBy.OrderByDescending(k => k.Value))
                    {
                        Mod.log.Info($"AllowList:   {kv.Value} by {kv.Key} (e.g. '{hubExample[kv.Key]}')");
                    }
                }
            }
            finally
            {
                entities.Dispose();
            }

            BuildingIndex.AssignZoneTypes(EntityManager, m_Index);
            BuildingIndex.GroupSmallSources(m_Index);
            IndexVersion++;
            int custom = 0;
            var sources = new HashSet<string>();
            foreach (var b in m_Index)
            {
                if (b.Origin == BuildingIndex.OriginCustom)
                {
                    custom++;
                    sources.Add(b.Source);
                }
            }

            Mod.log.Info($"AllowList: indexed {m_Index.Count} growables in {timer.ElapsedMilliseconds} ms ({custom} custom assets from {sources.Count} sources).");
        }

        private void ApplyRules()
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            m_ZonesByBuilding.Clear();
            m_RulesByIndex.Clear();

            var zoneEntities = m_OurZonesQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int z = 0; z < zoneEntities.Length; z++)
                {
                    Entity zoneEntity = zoneEntities[z];
                    string prefabName = m_PrefabSystem.GetPrefabName(zoneEntity);
                    ushort zoneIndex = EntityManager.GetComponentData<ZoneData>(zoneEntity).m_ZoneType.m_Index;
                    var allowed = EntityManager.GetBuffer<RzbtAllowedBuilding>(zoneEntity);
                    allowed.Clear();

                    // A zone removed from the file keeps its prefab until restart, but allows nothing.
                    ZoneDefinition definition = ZoneLibrary.FindByPrefabName(prefabName);
                    var rules = new ZoneRules { Index = zoneIndex, PrefabName = prefabName, Definition = definition };
                    m_RulesByIndex[zoneIndex] = rules;
                    if (definition == null)
                    {
                        Mod.log.Info($"AllowList: '{prefabName}' is no longer in zones.json; it allows nothing until restart.");
                        continue;
                    }

                    int levelOne = 0;
                    foreach (BuildingInfo b in m_Index)
                    {
                        if (!BuildingIndex.Matches(definition, b))
                        {
                            continue;
                        }

                        allowed.Add(new RzbtAllowedBuilding { m_Building = b.Entity });
                        rules.Allowed.Add(b);
                        rules.AllowedSet.Add(b.Entity);
                        if (b.Level == 1)
                        {
                            levelOne++;
                        }

                        if (!m_ZonesByBuilding.TryGetValue(b.Entity, out var list))
                        {
                            list = new List<ushort>(1);
                            m_ZonesByBuilding[b.Entity] = list;
                        }

                        list.Add(zoneIndex);
                    }

                    Mod.log.Info($"AllowList: '{definition.Name}' ({prefabName}, zone type {zoneIndex}) allows {allowed.Length} buildings, {levelOne} of them level 1 designs that can grow on empty lots.");
                }
            }
            finally
            {
                zoneEntities.Dispose();
            }

            RulesVersion++;
            Mod.log.Info($"AllowList: rules applied in {timer.ElapsedMilliseconds} ms.");
        }
    }
}
