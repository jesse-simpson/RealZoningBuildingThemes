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

        // Placemaker's reshaped copies of designs, with the design each was made from (see
        // PlacemakerCompat), and every building prefab seen by the last index (designs, copies,
        // hubs and broken ones alike), to tell new copies from new designs.
        private readonly Dictionary<Entity, Entity> m_VariantBase = new Dictionary<Entity, Entity>();
        private readonly HashSet<Entity> m_Known = new HashSet<Entity>();
        private readonly List<Entity> m_AddedVariants = new List<Entity>();

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

        /// <summary>Zone type indices of our zones that allow this building prefab, or null. A Placemaker copy met for the first time is learned here.</summary>
        public List<ushort> GetAllowingZones(Entity buildingPrefab)
        {
            if (m_ZonesByBuilding.TryGetValue(buildingPrefab, out var zones))
            {
                return zones;
            }

            return EnsureVariant(buildingPrefab) && m_ZonesByBuilding.TryGetValue(buildingPrefab, out zones) ? zones : null;
        }

        /// <summary>Placemaker copies added to the allow-lists since the rules were last applied, in order (the zone check adds them to its job's map).</summary>
        public IReadOnlyList<Entity> AddedVariants => m_AddedVariants;

        /// <summary>
        /// True when this building prefab is a Placemaker copy (now on the allow-lists of its design).
        /// Placemaker makes most of its copies as disabled prefab entities, which no prefab query
        /// lists, so a copy is learned when it is first met on a building, here.
        /// </summary>
        public bool EnsureVariant(Entity buildingPrefab)
        {
            if (m_VariantBase.ContainsKey(buildingPrefab))
            {
                return true;
            }

            if (RulesVersion == 0 || buildingPrefab == Entity.Null || m_InfoByEntity.ContainsKey(buildingPrefab))
            {
                return false;
            }

            Entity design = PlacemakerCompat.BaseOf(World, buildingPrefab);
            if (design == buildingPrefab)
            {
                return false;
            }

            m_Known.Add(buildingPrefab);
            m_VariantBase[buildingPrefab] = design;
            AllowVariant(buildingPrefab, design);
            return true;
        }

        /// <summary>The design a building prefab stands for: itself, or for a Placemaker reshaped copy, the design it was made from.</summary>
        public Entity BaseOf(Entity buildingPrefab)
        {
            return m_VariantBase.TryGetValue(buildingPrefab, out Entity design) ? design : buildingPrefab;
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

            bool rebuildRequested = s_RebuildRequested;
            int lastZones = m_LastZoneCount;
            m_LastBuildingCount = buildings;
            m_LastZoneCount = zones;
            s_RebuildRequested = false;

            // Placemaker makes a new copy of a design each time it reshapes one in a new way. Those
            // only need adding to the allow-lists of their design, not a whole new index.
            if (buildingsChanged && !rebuildRequested && zones == lastZones && TryAddVariantsOnly())
            {
                return;
            }

            if (buildingsChanged)
            {
                RebuildIndex();
            }

            ApplyRules();
        }

        /// <summary>
        /// When every building prefab new since the last index is a Placemaker copy, adds them to
        /// the allow-lists of their designs and returns true; otherwise false (a full rebuild is due).
        /// </summary>
        private bool TryAddVariantsOnly()
        {
            if (m_Known.Count == 0 || !PlacemakerCompat.Available(World))
            {
                return false;
            }

            var added = new List<(Entity variant, Entity design)>();
            var current = new HashSet<Entity>();
            var entities = m_BuildingQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity e = entities[i];
                    current.Add(e);
                    if (m_Known.Contains(e))
                    {
                        continue;
                    }

                    Entity design = PlacemakerCompat.BaseOf(World, e);
                    if (design == e)
                    {
                        return false; // a new design, not a copy
                    }

                    added.Add((e, design));
                }
            }
            finally
            {
                entities.Dispose();
            }

            // Placemaker also deletes copies no building uses any more. A design gone means a full
            // rebuild; a copy gone is simply forgotten. (Copies made as disabled entities are not
            // listed here at all; those are learned in EnsureVariant and kept until the next rebuild.)
            var removed = new List<Entity>();
            foreach (Entity e in m_Known)
            {
                if (!current.Contains(e) && !EntityManager.Exists(e))
                {
                    if (!m_VariantBase.ContainsKey(e))
                    {
                        return false;
                    }

                    removed.Add(e);
                }
            }

            foreach (Entity e in removed)
            {
                m_Known.Remove(e);
                m_VariantBase.Remove(e);
                if (m_ZonesByBuilding.TryGetValue(e, out List<ushort> zones))
                {
                    foreach (ushort zone in zones)
                    {
                        if (m_RulesByIndex.TryGetValue(zone, out ZoneRules rules))
                        {
                            rules.AllowedSet.Remove(e);
                        }
                    }

                    m_ZonesByBuilding.Remove(e);
                }
            }

            foreach (var (variant, design) in added)
            {
                m_Known.Add(variant);
                m_VariantBase[variant] = design;
                AllowVariant(variant, design);
            }

            return true;
        }

        /// <summary>A Placemaker copy is allowed wherever its design is (for the zone check, pardon and removal; never grown as such).</summary>
        private void AllowVariant(Entity variant, Entity design)
        {
            if (!m_ZonesByBuilding.TryGetValue(design, out List<ushort> zones))
            {
                return;
            }

            m_ZonesByBuilding[variant] = zones;
            m_AddedVariants.Add(variant);
            foreach (ushort zone in zones)
            {
                if (m_RulesByIndex.TryGetValue(zone, out ZoneRules rules))
                {
                    rules.AllowedSet.Add(variant);
                }
            }
        }

        private void RebuildIndex()
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            m_Index.Clear();
            m_InfoByEntity.Clear();
            var learned = new List<Entity>(m_VariantBase.Keys);
            m_VariantBase.Clear();
            m_Known.Clear();
            var entities = m_BuildingQuery.ToEntityArray(Allocator.Temp);
            try
            {
                int failed = 0;
                int hubs = 0;
                var hubsBy = new Dictionary<string, int>();
                var hubExample = new Dictionary<string, string>();
                for (int i = 0; i < entities.Length; i++)
                {
                    m_Known.Add(entities[i]);

                    // A Placemaker reshaped copy is not a design of its own: it counts as the
                    // design it was made from (allowed where that is), and is never grown as such.
                    Entity design = PlacemakerCompat.BaseOf(World, entities[i]);
                    if (design != entities[i])
                    {
                        m_VariantBase[entities[i]] = design;
                        continue;
                    }

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

            // Copies learned from buildings (disabled entities the query does not list) stay known
            // while Placemaker still has them.
            foreach (Entity e in learned)
            {
                if (!m_VariantBase.ContainsKey(e) && EntityManager.Exists(e))
                {
                    Entity design = PlacemakerCompat.BaseOf(World, e);
                    if (design != e)
                    {
                        m_Known.Add(e);
                        m_VariantBase[e] = design;
                    }
                }
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

            Mod.log.Info($"AllowList: indexed {m_Index.Count} growables in {timer.ElapsedMilliseconds} ms ({custom} custom assets from {sources.Count} sources){(m_VariantBase.Count > 0 ? $"; {m_VariantBase.Count} Placemaker reshaped copies count as their designs" : string.Empty)}.");
        }

        private void ApplyRules()
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            m_ZonesByBuilding.Clear();
            m_RulesByIndex.Clear();
            m_AddedVariants.Clear();

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

            foreach (KeyValuePair<Entity, Entity> kv in m_VariantBase)
            {
                AllowVariant(kv.Key, kv.Value);
            }

            RulesVersion++;
            Mod.log.Info($"AllowList: rules applied in {timer.ElapsedMilliseconds} ms.");
        }
    }
}
