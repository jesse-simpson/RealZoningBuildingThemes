using System;
using System.Collections.Generic;
using Colossal.Collections;
using Colossal.Mathematics;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace RealZoningBuildingThemes.Zones
{
    /// <summary>
    /// Turns our zones' cells in the current city back into the game's own zones, so the city no
    /// longer depends on them: for retiring a zone, and before uninstalling the mod.
    ///
    /// Why it is needed: a save stores which zone prefab each cell had. Load a city without one
    /// of our zones (mod removed, or the zone deleted) and its cells come back unzoned; the zone
    /// check then condemns every building on them, and the game demolishes them.
    ///
    /// How: every cell of the chosen zones under a building becomes that building's own home zone
    /// (its game zone, such as "FR Residential Medium Row"), so the game's zone check accepts the
    /// building and nothing is demolished. Every other cell of those zones becomes the zone's base
    /// game zone (the one it was created from). The blocks are then marked updated so the game
    /// refreshes lots and growth.
    /// </summary>
    public partial class RezoneSystem : GameSystemBase
    {
        private ZoneSetupSystem m_ZoneSetup;
        private PrefabSystem m_PrefabSystem;
        private Game.Zones.SearchSystem m_ZoneSearch;
        private EntityQuery m_BuildingQuery;
        private EntityQuery m_BlockQuery;
        private EntityQuery m_BuildingConfigQuery;
        private Game.Notifications.IconCommandSystem m_IconCommandSystem;

        private readonly List<Request> m_Requests = new List<Request>();

        /// <summary>
        /// The last rezoning, for the panel, as JSON {key, vars, label:{key, vars}} so the panel
        /// shows it in the player's language; changes with <see cref="NoticeVersion"/>.
        /// </summary>
        public string Notice { get; private set; } = string.Empty;

        public int NoticeVersion { get; private set; }

        private class Request
        {
            public List<string> PrefabNames;
            public string Label;      // English, for the log
            public string LabelKey;   // translation key of the label, for the panel
            public Dictionary<string, object> LabelVars;
            public Action OnDone;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ZoneSetup = World.GetOrCreateSystemManaged<ZoneSetupSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_ZoneSearch = World.GetOrCreateSystemManaged<Game.Zones.SearchSystem>();
            m_IconCommandSystem = World.GetOrCreateSystemManaged<Game.Notifications.IconCommandSystem>();
            m_BuildingConfigQuery = GetEntityQuery(ComponentType.ReadOnly<BuildingConfigurationData>());
            m_BuildingQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Building>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Transform>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            m_BlockQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Block>(), ComponentType.ReadWrite<Cell>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
        }

        /// <summary>Queues a rezoning of the given zones (prefab names) in the current city; runs on the next frame.</summary>
        public void RequestRezone(IEnumerable<string> prefabNames, string label, string labelKey, Dictionary<string, object> labelVars = null, Action onDone = null)
        {
            m_Requests.Add(new Request { PrefabNames = new List<string>(prefabNames), Label = label, LabelKey = labelKey, LabelVars = labelVars, OnDone = onDone });
        }

        protected override void OnUpdate()
        {
            while (m_Requests.Count > 0)
            {
                Request request = m_Requests[0];
                m_Requests.RemoveAt(0);
                try
                {
                    Rezone(request);
                }
                catch (Exception e)
                {
                    Mod.log.Error(e, $"Rezone: failed for {request.Label}; nothing further was changed.");
                    SetNotice("Notice.Failed", null, request);
                    continue;
                }

                request.OnDone?.Invoke();
            }
        }

        private void SetNotice(string key, Dictionary<string, object> vars, Request request)
        {
            Notice = Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                key,
                vars = vars ?? new Dictionary<string, object>(),
                label = new { key = request.LabelKey, vars = request.LabelVars ?? new Dictionary<string, object>() },
            });
            NoticeVersion++;
        }

        private void Rezone(Request request)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();

            // Our zone types to clear, each with the game zone its free cells fall back to.
            var fallback = new Dictionary<ushort, ZoneType>();
            foreach (string prefabName in request.PrefabNames)
            {
                if (!m_ZoneSetup.Zones.TryGetValue(prefabName, out RzbtZonePrefab zone) || !m_PrefabSystem.TryGetEntity(zone, out Entity zoneEntity) ||
                    !EntityManager.HasComponent<ZoneData>(zoneEntity))
                {
                    continue;
                }

                ZoneType ours = EntityManager.GetComponentData<ZoneData>(zoneEntity).m_ZoneType;
                ZoneType target = ZoneType.None;
                if (m_ZoneSetup.BaseOf.TryGetValue(prefabName, out ZonePrefab baseZone) && m_PrefabSystem.TryGetEntity(baseZone, out Entity baseEntity) &&
                    EntityManager.HasComponent<ZoneData>(baseEntity))
                {
                    target = EntityManager.GetComponentData<ZoneData>(baseEntity).m_ZoneType;
                }

                if (!ours.Equals(ZoneType.None))
                {
                    fallback[ours.m_Index] = target;
                }
            }

            if (fallback.Count == 0)
            {
                SetNotice("Notice.Nothing", null, request);
                return;
            }

            var changedBlocks = new HashSet<Entity>();
            int underBuildings = 0;
            int cancelledLevelUps = 0;

            // 1. Cells under buildings take the building's own home zone.
            NativeQuadTree<Entity, Bounds2> tree = m_ZoneSearch.GetSearchTree(true, out JobHandle deps);
            deps.Complete();
            var buildings = m_BuildingQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < buildings.Length; i++)
                {
                    Entity building = buildings[i];
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(building).m_Prefab;
                    if (!EntityManager.HasComponent<BuildingData>(prefab) || !EntityManager.HasComponent<SpawnableBuildingData>(prefab))
                    {
                        continue;
                    }

                    Entity homePrefab = EntityManager.GetComponentData<SpawnableBuildingData>(prefab).m_ZonePrefab;

                    // A level-up in progress to a design from another game zone (our zones allow
                    // that) would finish on cells of the wrong game zone and be condemned: cancel
                    // it; the game levels the building up again later, within its own zone.
                    if (EntityManager.HasComponent<Game.Objects.UnderConstruction>(building))
                    {
                        Entity next = EntityManager.GetComponentData<Game.Objects.UnderConstruction>(building).m_NewPrefab;
                        if (next != Entity.Null && next != prefab && EntityManager.HasComponent<SpawnableBuildingData>(next) &&
                            EntityManager.GetComponentData<SpawnableBuildingData>(next).m_ZonePrefab != homePrefab)
                        {
                            EntityManager.RemoveComponent<Game.Objects.UnderConstruction>(building);
                            if (m_BuildingConfigQuery.TryGetSingleton(out BuildingConfigurationData config))
                            {
                                m_IconCommandSystem.CreateCommandBuffer().Remove(building, config.m_LevelUpNotification);
                            }

                            cancelledLevelUps++;
                        }
                    }
                    if (homePrefab == Entity.Null || !EntityManager.HasComponent<ZoneData>(homePrefab) || !EntityManager.IsComponentEnabled<PrefabData>(homePrefab))
                    {
                        continue; // no usable home zone: its cells get the fallback below
                    }

                    ZoneType home = EntityManager.GetComponentData<ZoneData>(homePrefab).m_ZoneType;
                    if (home.Equals(ZoneType.None) || fallback.ContainsKey(home.m_Index))
                    {
                        continue;
                    }

                    var writer = new LotWriter
                    {
                        m_EntityManager = EntityManager,
                        m_Home = home,
                        m_Ours = fallback,
                        m_Changed = changedBlocks,
                    };
                    writer.Setup(EntityManager.GetComponentData<Transform>(building), EntityManager.GetComponentData<BuildingData>(prefab).m_LotSize);
                    tree.Iterate(ref writer);
                    underBuildings += writer.m_Count;
                }
            }
            finally
            {
                buildings.Dispose();
                m_ZoneSearch.AddSearchTreeReader(default);
            }

            // 2. Every other cell of those zones takes the zone's base game zone.
            int freeCells = 0;
            var blocks = m_BlockQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < blocks.Length; i++)
                {
                    DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(blocks[i]);
                    for (int c = 0; c < cells.Length; c++)
                    {
                        Cell cell = cells[c];
                        if (fallback.TryGetValue(cell.m_Zone.m_Index, out ZoneType target))
                        {
                            cell.m_Zone = target;
                            cells[c] = cell;
                            changedBlocks.Add(blocks[i]);
                            freeCells++;
                        }
                    }
                }
            }
            finally
            {
                blocks.Dispose();
            }

            foreach (Entity block in changedBlocks)
            {
                // As the game's zoning tool does after painting: the block is updated (lots and
                // growth) and its batches are redrawn (zone colours).
                if (!EntityManager.HasComponent<Updated>(block))
                {
                    EntityManager.AddComponent<Updated>(block);
                }

                if (!EntityManager.HasComponent<BatchesUpdated>(block))
                {
                    EntityManager.AddComponent<BatchesUpdated>(block);
                }
            }

            string text = underBuildings + freeCells == 0
                ? $"{request.Label}: no cells of these zones in this city."
                : $"{request.Label}: rezoned {underBuildings + freeCells:N0} cells to game zones ({underBuildings:N0} under buildings, which keep their own zone).";
            if (underBuildings + freeCells == 0)
            {
                SetNotice("Notice.NoCells", null, request);
            }
            else
            {
                SetNotice("Notice.Rezoned", new Dictionary<string, object> { ["cells"] = underBuildings + freeCells, ["under"] = underBuildings }, request);
            }
            Mod.log.Info($"Rezone: {text} {changedBlocks.Count} blocks, {timer.ElapsedMilliseconds} ms; {cancelledLevelUps} level-ups to another game zone's design cancelled.");
        }

        /// <summary>Writes a building's home zone into our zones' cells under its lot (the game's lot geometry, as in <see cref="ZoneLotTest"/>).</summary>
        private struct LotWriter : INativeQuadTreeIterator<Entity, Bounds2>, IUnsafeQuadTreeIterator<Entity, Bounds2>
        {
            public EntityManager m_EntityManager;
            public ZoneType m_Home;
            public Dictionary<ushort, ZoneType> m_Ours;
            public HashSet<Entity> m_Changed;
            public int m_Count;

            private Bounds2 m_Bounds;
            private int2 m_LotSize;
            private float2 m_StartPosition;
            private float2 m_Right;
            private float2 m_Forward;

            public void Setup(Transform transform, int2 lotSize)
            {
                m_Right = math.rotate(transform.m_Rotation, new float3(8f, 0f, 0f)).xz;
                m_Forward = math.rotate(transform.m_Rotation, new float3(0f, 0f, 8f)).xz;
                float2 halfRight = m_Right * (lotSize.x * 0.5f - 0.5f);
                float2 halfForward = m_Forward * (lotSize.y * 0.5f - 0.5f);
                float2 extent = math.abs(halfForward) + math.abs(halfRight);
                m_Bounds = new Bounds2(transform.m_Position.xz - extent, transform.m_Position.xz + extent);
                m_LotSize = lotSize;
                m_StartPosition = transform.m_Position.xz + halfForward + halfRight;
            }

            public bool Intersect(Bounds2 bounds)
            {
                return MathUtils.Intersect(bounds, m_Bounds);
            }

            public void Iterate(Bounds2 bounds, Entity blockEntity)
            {
                if (!MathUtils.Intersect(bounds, m_Bounds) || !m_EntityManager.HasComponent<Block>(blockEntity) || !m_EntityManager.HasBuffer<Cell>(blockEntity))
                {
                    return;
                }

                Block block = m_EntityManager.GetComponentData<Block>(blockEntity);
                DynamicBuffer<Cell> cells = m_EntityManager.GetBuffer<Cell>(blockEntity);
                float2 rowStart = m_StartPosition;
                for (int y = 0; y < m_LotSize.y; y++)
                {
                    float2 position = rowStart;
                    for (int x = 0; x < m_LotSize.x; x++)
                    {
                        int2 cellIndex = ZoneUtils.GetCellIndex(block, position);
                        if (math.all((cellIndex >= 0) & (cellIndex < block.m_Size)))
                        {
                            int index = cellIndex.y * block.m_Size.x + cellIndex.x;
                            Cell cell = cells[index];
                            if (m_Ours.ContainsKey(cell.m_Zone.m_Index))
                            {
                                cell.m_Zone = m_Home;
                                cells[index] = cell;
                                m_Changed.Add(blockEntity);
                                m_Count++;
                            }
                        }

                        position -= m_Right;
                    }

                    rowStart -= m_Forward;
                }
            }
        }
    }
}
