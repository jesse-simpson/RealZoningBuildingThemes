using System;
using System.Collections.Generic;
using Colossal.Collections;
using Colossal.Mathematics;
using Colossal.Serialization.Entities;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Game.Zones;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace RealZoningBuildingThemes.Zones
{
    /// <summary>
    /// Makes every save safe to load without the mod.
    ///
    /// A save stores which zone each cell has. Load a city whose cells use one of our zones without
    /// the mod, and those cells come back unzoned, so the game condemns every building on them. To
    /// avoid that, every save is written in the layout "Prepare for uninstall" uses, without
    /// changing the city being played:
    ///
    /// 1. Just before the game writes a save (manual or autosave), this system rewrites our zones'
    ///    cells to game zones: each building's own home zone under it, the zone's base zone on
    ///    empty cells (and on the block's free lots). A level-up caught at the moment it switches
    ///    to a design from another game zone (our zones allow that) is paused in the saved copy,
    ///    since it would finish on the wrong game zone and be condemned. What it changed, and what
    ///    to put back, is kept as a record.
    /// 2. The game writes the save: its cells hold game zones only. The record is written too, as
    ///    this system's own save data (see <see cref="Serialize"/>). Without the mod, the game
    ///    skips data it has no reader for, so such a save loads as an ordinary city.
    /// 3. Right after, <see cref="SaveRestoreSystem"/> puts every change back exactly, so play
    ///    carries on as if nothing happened.
    /// 4. On load with the mod, <see cref="LoadRestoreSystem"/> reads the record and puts our zones
    ///    and the paused level-ups back before the city starts.
    ///
    /// The save step runs while the simulation is stopped (the game's own serialize phase), so a
    /// save never catches the city half-converted. Steps 1 and 3 run as Burst jobs (the buildings
    /// in parallel), since saves, autosaves included, should not hitch any longer than they must.
    /// </summary>
    public partial class SaveSwapSystem : GameSystemBase, IDefaultSerializable, ISerializable
    {
        /// <summary>Format of the saved record; bumped if the layout ever changes.</summary>
        private const int kFormat = 1;

        /// <summary>One of our zones: the game zone its free cells fall back to, and the name keys.</summary>
        public struct OurZone
        {
            public ZoneType Fallback;
            public ushort OurKey;
            public ushort FallbackKey;
        }

        /// <summary>A cell of ours under a building: the building's home zone goes there.</summary>
        public struct Hit
        {
            public Entity Block;
            public ushort Index;
            public ZoneType Home;
            public ushort HomeKey;
        }

        /// <summary>A cell or free lot of ours, rewritten in the saved copy.</summary>
        public struct Edit
        {
            public ushort Index;          // cell index in the block, or lot index
            public ZoneType Original;     // our zone (this session's index)
            public ZoneType Fallback;     // the game zone written in its place
            public ushort OriginalKey;    // our zone's prefab name, as a key into the name table
            public ushort FallbackKey;    // the game zone's prefab name, likewise
        }

        /// <summary>A block's edits: cells first, then free lots, in one run of the edit list.</summary>
        public struct Group
        {
            public Entity Block;
            public int Start;
            public int CellCount;
            public int LotCount;
        }

        public class PausedLevelUp
        {
            public Entity Building;
            public UnderConstruction Construction;
            public string Current;        // the building's prefab name
            public string Target;         // the design it was levelling up to
        }

        /// <summary>A block's record as read from a save (zone indices are not known yet).</summary>
        public class LoadedBlock
        {
            public Entity Block;
            public readonly List<(ushort index, ushort ours, ushort fallback)> Cells = new List<(ushort, ushort, ushort)>();
            public readonly List<(ushort index, ushort ours, ushort fallback)> Lots = new List<(ushort, ushort, ushort)>();
        }

        public class LoadedLevelUp
        {
            public Entity Building;
            public string Current;
            public string Target;
            public byte Progress;
            public byte Speed;
        }

        private ZoneSetupSystem m_ZoneSetup;
        private PrefabSystem m_PrefabSystem;
        private Game.Zones.SearchSystem m_ZoneSearch;
        private EntityQuery m_BuildingQuery;
        private EntityQuery m_BlockQuery;
        private EntityQuery m_ZonePrefabQuery;

        // The current save's changes (filled in step 1, written in step 2, undone in step 3).
        private readonly List<string> m_Names = new List<string>();
        private NativeList<Edit> m_Edits;
        private NativeList<Group> m_Groups;
        private readonly List<PausedLevelUp> m_LevelUps = new List<PausedLevelUp>();

        // The record read from the save being loaded (used once by LoadRestoreSystem).
        private readonly List<string> m_LoadedNames = new List<string>();
        private readonly List<LoadedBlock> m_LoadedBlocks = new List<LoadedBlock>();
        private readonly List<LoadedLevelUp> m_LoadedLevelUps = new List<LoadedLevelUp>();

        public IReadOnlyList<string> LoadedNames => m_LoadedNames;

        public IReadOnlyList<LoadedBlock> LoadedBlocks => m_LoadedBlocks;

        public IReadOnlyList<LoadedLevelUp> LoadedLevelUps => m_LoadedLevelUps;

        /// <summary>Set by step 1, cleared by step 3: there is something to put back.</summary>
        public bool HasPending { get; private set; }

        public void ClearPending()
        {
            m_Names.Clear();
            m_Edits.Clear();
            m_Groups.Clear();
            m_LevelUps.Clear();
            HasPending = false;
        }

        public void ClearLoaded()
        {
            m_LoadedNames.Clear();
            m_LoadedBlocks.Clear();
            m_LoadedLevelUps.Clear();
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ZoneSetup = World.GetOrCreateSystemManaged<ZoneSetupSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_ZoneSearch = World.GetOrCreateSystemManaged<Game.Zones.SearchSystem>();
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
            m_ZonePrefabQuery = GetEntityQuery(ComponentType.ReadOnly<ZoneData>(), ComponentType.ReadOnly<PrefabData>());
            m_Edits = new NativeList<Edit>(Allocator.Persistent);
            m_Groups = new NativeList<Group>(Allocator.Persistent);
        }

        protected override void OnDestroy()
        {
            if (m_Edits.IsCreated)
            {
                m_Edits.Dispose();
            }

            if (m_Groups.IsCreated)
            {
                m_Groups.Dispose();
            }

            base.OnDestroy();
        }

        /// <summary>Step 1: runs just before the game starts writing a save.</summary>
        protected override void OnUpdate()
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            ClearPending();
            try
            {
                MakeSaveSafe();
            }
            catch (Exception e)
            {
                // Everything changed so far is in the record, so step 3 still puts it back; the save
                // is then only partly converted, which is no worse than before this feature.
                Mod.log.Error(e, "SaveSafe: could not finish converting this save to game zones; the city itself is unchanged.");
            }

            if (HasPending)
            {
                int cells = 0;
                int lots = 0;
                for (int i = 0; i < m_Groups.Length; i++)
                {
                    cells += m_Groups[i].CellCount;
                    lots += m_Groups[i].LotCount;
                }

                Mod.log.Info($"SaveSafe: saving with game zones in place of custom zones: {cells:N0} cells in {m_Groups.Length:N0} blocks, {lots:N0} free lots, {m_LevelUps.Count:N0} level-ups to another game zone's design paused in the saved copy; {timer.Elapsed.TotalMilliseconds:0.#} ms.");
            }
        }

        private void MakeSaveSafe()
        {
            // Every zone's name gets a key (an index into the saved name table), and each of our
            // zones its fallback. Zones are few (about a hundred), so this is quick.
            var zoneEntities = m_ZonePrefabQuery.ToEntityArray(Allocator.Temp);
            int maxIndex = 0;
            for (int i = 0; i < zoneEntities.Length; i++)
            {
                maxIndex = math.max(maxIndex, EntityManager.GetComponentData<ZoneData>(zoneEntities[i]).m_ZoneType.m_Index);
            }

            var keys = new NativeArray<ushort>(maxIndex + 1, Allocator.TempJob);
            var nameKeys = new Dictionary<string, ushort>();
            ushort KeyOf(string name)
            {
                name = name ?? string.Empty;
                if (!nameKeys.TryGetValue(name, out ushort key))
                {
                    key = (ushort)m_Names.Count;
                    m_Names.Add(name);
                    nameKeys[name] = key;
                }

                return key;
            }

            KeyOf(string.Empty); // key 0: no zone
            for (int i = 0; i < zoneEntities.Length; i++)
            {
                ushort index = EntityManager.GetComponentData<ZoneData>(zoneEntities[i]).m_ZoneType.m_Index;
                keys[index] = KeyOf(m_PrefabSystem.GetPrefabName(zoneEntities[i]));
            }

            zoneEntities.Dispose();

            var ours = new NativeParallelHashMap<ushort, OurZone>(m_ZoneSetup.Zones.Count + 1, Allocator.TempJob);
            try
            {
                foreach (KeyValuePair<string, RzbtZonePrefab> kv in m_ZoneSetup.Zones)
                {
                    if (!m_PrefabSystem.TryGetEntity(kv.Value, out Entity zoneEntity) || !EntityManager.HasComponent<ZoneData>(zoneEntity))
                    {
                        continue;
                    }

                    ZoneType zoneType = EntityManager.GetComponentData<ZoneData>(zoneEntity).m_ZoneType;
                    if (zoneType.Equals(ZoneType.None) || zoneType.m_Index >= keys.Length)
                    {
                        continue;
                    }

                    ZoneType fallback = ZoneType.None;
                    if (m_ZoneSetup.BaseOf.TryGetValue(kv.Key, out ZonePrefab baseZone) && m_PrefabSystem.TryGetEntity(baseZone, out Entity baseEntity) &&
                        EntityManager.HasComponent<ZoneData>(baseEntity))
                    {
                        fallback = EntityManager.GetComponentData<ZoneData>(baseEntity).m_ZoneType;
                    }

                    ours.TryAdd(zoneType.m_Index, new OurZone
                    {
                        Fallback = fallback,
                        OurKey = keys[zoneType.m_Index],
                        FallbackKey = fallback.Equals(ZoneType.None) || fallback.m_Index >= keys.Length ? (ushort)0 : keys[fallback.m_Index],
                    });
                }

                if (ours.Count() == 0)
                {
                    return;
                }

                RunJobs(ours, keys);
            }
            finally
            {
                ours.Dispose();
                keys.Dispose();
            }
        }

        private void RunJobs(NativeParallelHashMap<ushort, OurZone> ours, NativeArray<ushort> keys)
        {
            // The game is saving: nothing else runs now. Finish any job still touching the city's
            // data, so ours read and write it alone.
            EntityManager.CompleteAllTrackedJobs();

            var hits = new NativeQueue<Hit>(Allocator.TempJob);
            var levelUps = new NativeQueue<Entity>(Allocator.TempJob);
            NativeArray<Entity> blocks = m_BlockQuery.ToEntityArray(Allocator.TempJob);
            try
            {
                NativeQuadTree<Entity, Bounds2> tree = m_ZoneSearch.GetSearchTree(true, out JobHandle treeDeps);

                // 1. Every building finds our zones' cells under its lot (in parallel, read only).
                var find = new FindLotCellsJob
                {
                    m_EntityType = GetEntityTypeHandle(),
                    m_PrefabRefType = GetComponentTypeHandle<PrefabRef>(true),
                    m_TransformType = GetComponentTypeHandle<Transform>(true),
                    m_UnderConstructionData = GetComponentLookup<UnderConstruction>(true),
                    m_BuildingData = GetComponentLookup<BuildingData>(true),
                    m_SpawnableData = GetComponentLookup<SpawnableBuildingData>(true),
                    m_ZoneData = GetComponentLookup<ZoneData>(true),
                    m_PrefabData = GetComponentLookup<PrefabData>(true),
                    m_BlockData = GetComponentLookup<Block>(true),
                    m_Cells = GetBufferLookup<Cell>(true),
                    m_Ours = ours,
                    m_Keys = keys,
                    m_SearchTree = tree,
                    m_Hits = hits.AsParallelWriter(),
                    m_LevelUps = levelUps.AsParallelWriter(),
                };
                JobHandle findHandle = find.ScheduleParallel(m_BuildingQuery, JobHandle.CombineDependencies(Dependency, treeDeps));
                m_ZoneSearch.AddSearchTreeReader(findHandle);

                // 2. Every block writes game zones into our cells and free lots, recording each change.
                var apply = new ApplyFallbackJob
                {
                    m_Blocks = blocks,
                    m_Cells = GetBufferLookup<Cell>(false),
                    m_Lots = GetBufferLookup<VacantLot>(false),
                    m_Ours = ours,
                    m_Hits = hits,
                    m_Edits = m_Edits,
                    m_Groups = m_Groups,
                };
                JobHandle applyHandle = apply.Schedule(findHandle);
                applyHandle.Complete();

                // 3. Level-ups caught switching to another game zone's design: pause them in the
                //    saved copy (few, if any: the game swaps the design on the next simulation tick).
                while (levelUps.TryDequeue(out Entity building))
                {
                    if (!EntityManager.HasComponent<UnderConstruction>(building))
                    {
                        continue;
                    }

                    UnderConstruction construction = EntityManager.GetComponentData<UnderConstruction>(building);
                    m_LevelUps.Add(new PausedLevelUp
                    {
                        Building = building,
                        Construction = construction,
                        Current = m_PrefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(building).m_Prefab),
                        Target = m_PrefabSystem.GetPrefabName(construction.m_NewPrefab),
                    });
                    EntityManager.RemoveComponent<UnderConstruction>(building);
                }

                HasPending = m_Edits.Length > 0 || m_LevelUps.Count > 0;
            }
            finally
            {
                hits.Dispose();
                levelUps.Dispose();
                blocks.Dispose();
            }
        }

        /// <summary>Step 3 (called by <see cref="SaveRestoreSystem"/>): puts every change back exactly.</summary>
        /// <remarks>The lookups come from the calling system's own update, so its job dependencies apply.</remarks>
        public void RestoreAfterSave(BufferLookup<Cell> cellLookup, BufferLookup<VacantLot> lotLookup, out int cells, out int lots, out int levelUps, out int missing)
        {
            cells = 0;
            lots = 0;
            levelUps = 0;
            missing = 0;
            var counts = new NativeArray<int>(3, Allocator.TempJob);
            try
            {
                var restore = new RestoreJob
                {
                    m_Edits = m_Edits,
                    m_Groups = m_Groups,
                    m_Cells = cellLookup,
                    m_Lots = lotLookup,
                    m_Counts = counts,
                };
                restore.Run();
                cells = counts[0];
                lots = counts[1];
                missing = counts[2];
            }
            finally
            {
                counts.Dispose();
            }

            foreach (PausedLevelUp p in m_LevelUps)
            {
                if (EntityManager.Exists(p.Building) && !EntityManager.HasComponent<Deleted>(p.Building) &&
                    !EntityManager.HasComponent<UnderConstruction>(p.Building))
                {
                    EntityManager.AddComponentData(p.Building, p.Construction);
                    levelUps++;
                }
                else
                {
                    missing++;
                }
            }
        }

        // ---- Jobs ----

        /// <summary>For each grown building, the cells of our zones under its lot, with its home zone.</summary>
        [BurstCompile]
        private struct FindLotCellsJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle m_EntityType;
            [ReadOnly] public ComponentTypeHandle<PrefabRef> m_PrefabRefType;
            [ReadOnly] public ComponentTypeHandle<Transform> m_TransformType;
            [ReadOnly] public ComponentLookup<UnderConstruction> m_UnderConstructionData;
            [ReadOnly] public ComponentLookup<BuildingData> m_BuildingData;
            [ReadOnly] public ComponentLookup<SpawnableBuildingData> m_SpawnableData;
            [ReadOnly] public ComponentLookup<ZoneData> m_ZoneData;
            [ReadOnly] public ComponentLookup<PrefabData> m_PrefabData;
            [ReadOnly] public ComponentLookup<Block> m_BlockData;
            [ReadOnly] public BufferLookup<Cell> m_Cells;
            [ReadOnly] public NativeParallelHashMap<ushort, OurZone> m_Ours;
            [ReadOnly] public NativeArray<ushort> m_Keys;
            [ReadOnly] public NativeQuadTree<Entity, Bounds2> m_SearchTree;
            public NativeQueue<Hit>.ParallelWriter m_Hits;
            public NativeQueue<Entity>.ParallelWriter m_LevelUps;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(m_EntityType);
                NativeArray<PrefabRef> prefabRefs = chunk.GetNativeArray(ref m_PrefabRefType);
                NativeArray<Transform> transforms = chunk.GetNativeArray(ref m_TransformType);
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity prefab = prefabRefs[i].m_Prefab;
                    if (!m_BuildingData.TryGetComponent(prefab, out BuildingData buildingData) ||
                        !m_SpawnableData.TryGetComponent(prefab, out SpawnableBuildingData spawnable))
                    {
                        continue;
                    }

                    Entity homePrefab = spawnable.m_ZonePrefab;
                    if (homePrefab == Entity.Null || !m_ZoneData.TryGetComponent(homePrefab, out ZoneData homeData) ||
                        !m_PrefabData.HasComponent(homePrefab) || !m_PrefabData.IsComponentEnabled(homePrefab))
                    {
                        continue; // no usable home zone: its cells get the zone's base zone
                    }

                    ZoneType home = homeData.m_ZoneType;
                    if (home.Equals(ZoneType.None) || home.m_Index >= m_Keys.Length || m_Ours.ContainsKey(home.m_Index))
                    {
                        continue;
                    }

                    var iterator = new LotIterator
                    {
                        m_BlockData = m_BlockData,
                        m_Cells = m_Cells,
                        m_Ours = m_Ours,
                        m_Hits = m_Hits,
                        m_Home = home,
                        m_HomeKey = m_Keys[home.m_Index],
                    };
                    iterator.Setup(transforms[i], buildingData.m_LotSize);
                    m_SearchTree.Iterate(ref iterator);
                    if (iterator.m_Count == 0)
                    {
                        continue; // not on our zones
                    }

                    // A level-up switching to a design from another game zone, caught mid-switch.
                    if (m_UnderConstructionData.TryGetComponent(entities[i], out UnderConstruction construction))
                    {
                        Entity next = construction.m_NewPrefab;
                        if (next != Entity.Null && next != prefab && m_SpawnableData.TryGetComponent(next, out SpawnableBuildingData nextSpawnable) &&
                            nextSpawnable.m_ZonePrefab != homePrefab)
                        {
                            m_LevelUps.Enqueue(entities[i]);
                        }
                    }
                }
            }

            private struct LotIterator : INativeQuadTreeIterator<Entity, Bounds2>, IUnsafeQuadTreeIterator<Entity, Bounds2>
            {
                public ComponentLookup<Block> m_BlockData;
                public BufferLookup<Cell> m_Cells;
                public NativeParallelHashMap<ushort, OurZone> m_Ours;
                public NativeQueue<Hit>.ParallelWriter m_Hits;
                public ZoneType m_Home;
                public ushort m_HomeKey;
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
                    if (!MathUtils.Intersect(bounds, m_Bounds) || !m_BlockData.TryGetComponent(blockEntity, out Block block) ||
                        !m_Cells.TryGetBuffer(blockEntity, out DynamicBuffer<Cell> cells))
                    {
                        return;
                    }

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
                                if (index < cells.Length && m_Ours.ContainsKey(cells[index].m_Zone.m_Index))
                                {
                                    m_Hits.Enqueue(new Hit { Block = blockEntity, Index = (ushort)index, Home = m_Home, HomeKey = m_HomeKey });
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

        /// <summary>Writes game zones into our cells and free lots, block by block, recording each change.</summary>
        [BurstCompile]
        private struct ApplyFallbackJob : IJob
        {
            [ReadOnly] public NativeArray<Entity> m_Blocks;
            public BufferLookup<Cell> m_Cells;
            public BufferLookup<VacantLot> m_Lots;
            [ReadOnly] public NativeParallelHashMap<ushort, OurZone> m_Ours;
            public NativeQueue<Hit> m_Hits;
            public NativeList<Edit> m_Edits;
            public NativeList<Group> m_Groups;

            public void Execute()
            {
                // The buildings' hits, by block.
                var hitsByBlock = new NativeParallelMultiHashMap<Entity, Hit>(math.max(16, m_Hits.Count), Allocator.Temp);
                while (m_Hits.TryDequeue(out Hit hit))
                {
                    hitsByBlock.Add(hit.Block, hit);
                }

                var home = new NativeList<int>(64, Allocator.Temp);
                var homeHits = new NativeList<Hit>(16, Allocator.Temp);
                for (int b = 0; b < m_Blocks.Length; b++)
                {
                    Entity blockEntity = m_Blocks[b];
                    if (!m_Cells.TryGetBuffer(blockEntity, out DynamicBuffer<Cell> cells))
                    {
                        continue;
                    }

                    bool any = false;
                    for (int c = 0; c < cells.Length && !any; c++)
                    {
                        any = m_Ours.ContainsKey(cells[c].m_Zone.m_Index);
                    }

                    bool hasLots = m_Lots.TryGetBuffer(blockEntity, out DynamicBuffer<VacantLot> lots);
                    bool anyLot = false;
                    if (hasLots)
                    {
                        for (int l = 0; l < lots.Length && !anyLot; l++)
                        {
                            anyLot = m_Ours.ContainsKey(lots[l].m_Type.m_Index);
                        }
                    }

                    if (!any && !anyLot)
                    {
                        continue;
                    }

                    var group = new Group { Block = blockEntity, Start = m_Edits.Length };

                    if (any)
                    {
                        // Which cells lie under a building (first building wins where lots overlap).
                        home.Clear();
                        home.Resize(cells.Length, NativeArrayOptions.ClearMemory);
                        homeHits.Clear();
                        if (hitsByBlock.TryGetFirstValue(blockEntity, out Hit hit, out NativeParallelMultiHashMapIterator<Entity> it))
                        {
                            do
                            {
                                if (hit.Index < home.Length && home[hit.Index] == 0)
                                {
                                    homeHits.Add(hit);
                                    home[hit.Index] = homeHits.Length; // 1-based
                                }
                            }
                            while (hitsByBlock.TryGetNextValue(out hit, ref it));
                        }

                        for (int c = 0; c < cells.Length; c++)
                        {
                            Cell cell = cells[c];
                            if (!m_Ours.TryGetValue(cell.m_Zone.m_Index, out OurZone zone))
                            {
                                continue;
                            }

                            ZoneType fallback = zone.Fallback;
                            ushort fallbackKey = zone.FallbackKey;
                            if (home[c] != 0)
                            {
                                Hit h = homeHits[home[c] - 1];
                                fallback = h.Home;
                                fallbackKey = h.HomeKey;
                            }

                            m_Edits.Add(new Edit { Index = (ushort)c, Original = cell.m_Zone, Fallback = fallback, OriginalKey = zone.OurKey, FallbackKey = fallbackKey });
                            cell.m_Zone = fallback;
                            cells[c] = cell;
                            group.CellCount++;
                        }
                    }

                    if (anyLot)
                    {
                        for (int l = 0; l < lots.Length; l++)
                        {
                            VacantLot lot = lots[l];
                            if (!m_Ours.TryGetValue(lot.m_Type.m_Index, out OurZone zone))
                            {
                                continue;
                            }

                            m_Edits.Add(new Edit { Index = (ushort)l, Original = lot.m_Type, Fallback = zone.Fallback, OriginalKey = zone.OurKey, FallbackKey = zone.FallbackKey });
                            lot.m_Type = zone.Fallback;
                            lots[l] = lot;
                            group.LotCount++;
                        }
                    }

                    m_Groups.Add(group);
                }

                home.Dispose();
                homeHits.Dispose();
                hitsByBlock.Dispose();
            }
        }

        /// <summary>Puts every recorded cell and free lot back to our zone.</summary>
        [BurstCompile]
        private struct RestoreJob : IJob
        {
            [ReadOnly] public NativeList<Edit> m_Edits;
            [ReadOnly] public NativeList<Group> m_Groups;
            public BufferLookup<Cell> m_Cells;
            public BufferLookup<VacantLot> m_Lots;
            public NativeArray<int> m_Counts; // cells, lots, blocks missing

            public void Execute()
            {
                for (int g = 0; g < m_Groups.Length; g++)
                {
                    Group group = m_Groups[g];
                    if (!m_Cells.TryGetBuffer(group.Block, out DynamicBuffer<Cell> cells))
                    {
                        m_Counts[2]++;
                        continue;
                    }

                    for (int i = 0; i < group.CellCount; i++)
                    {
                        Edit e = m_Edits[group.Start + i];
                        if (e.Index < cells.Length)
                        {
                            Cell cell = cells[e.Index];
                            cell.m_Zone = e.Original;
                            cells[e.Index] = cell;
                            m_Counts[0]++;
                        }
                    }

                    if (group.LotCount > 0 && m_Lots.TryGetBuffer(group.Block, out DynamicBuffer<VacantLot> lots))
                    {
                        for (int i = 0; i < group.LotCount; i++)
                        {
                            Edit e = m_Edits[group.Start + group.CellCount + i];
                            if (e.Index < lots.Length)
                            {
                                VacantLot lot = lots[e.Index];
                                lot.m_Type = e.Original;
                                lots[e.Index] = lot;
                                m_Counts[1]++;
                            }
                        }
                    }
                }
            }
        }

        // ---- Save data: the record of this save's changes ----

        public void SetDefaults(Context context)
        {
            // A save made before this feature (or a new city): nothing to put back.
            ClearLoaded();
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kFormat);
            writer.Write(m_Names.Count);
            foreach (string name in m_Names)
            {
                writer.Write(name);
            }

            writer.Write(m_Groups.Length);
            for (int g = 0; g < m_Groups.Length; g++)
            {
                Group group = m_Groups[g];
                writer.Write(group.Block);
                WriteEdits(writer, group.Start, group.CellCount);
                WriteEdits(writer, group.Start + group.CellCount, group.LotCount);
            }

            writer.Write(m_LevelUps.Count);
            foreach (PausedLevelUp p in m_LevelUps)
            {
                writer.Write(p.Building);
                writer.Write(p.Current ?? string.Empty);
                writer.Write(p.Target ?? string.Empty);
                writer.Write((int)p.Construction.m_Progress);
                writer.Write((int)p.Construction.m_Speed);
            }
        }

        private void WriteEdits<TWriter>(TWriter writer, int start, int count) where TWriter : IWriter
        {
            writer.Write(count);
            for (int i = start; i < start + count; i++)
            {
                Edit e = m_Edits[i];
                writer.Write(e.Index);
                writer.Write(e.OriginalKey);
                writer.Write(e.FallbackKey);
            }
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            ClearLoaded();
            reader.Read(out int format);
            if (format != kFormat)
            {
                // A newer layout than this version understands; it was written by a newer version
                // of the mod. Reading on would misread it; the city then keeps its game zones.
                Mod.log.Warn($"SaveSafe: this save's custom-zone record has format {format}, which this version cannot read; the city loads with game zones.");
                return;
            }

            reader.Read(out int names);
            for (int i = 0; i < names; i++)
            {
                reader.Read(out string name);
                m_LoadedNames.Add(name);
            }

            reader.Read(out int blocks);
            for (int i = 0; i < blocks; i++)
            {
                var b = new LoadedBlock();
                reader.Read(out b.Block);
                ReadEdits(reader, b.Cells);
                ReadEdits(reader, b.Lots);
                m_LoadedBlocks.Add(b);
            }

            reader.Read(out int levelUps);
            for (int i = 0; i < levelUps; i++)
            {
                var p = new LoadedLevelUp();
                reader.Read(out p.Building);
                reader.Read(out p.Current);
                reader.Read(out p.Target);
                reader.Read(out int progress);
                reader.Read(out int speed);
                p.Progress = (byte)math.clamp(progress, 0, 255);
                p.Speed = (byte)math.clamp(speed, 0, 255);
                m_LoadedLevelUps.Add(p);
            }
        }

        private static void ReadEdits<TReader>(TReader reader, List<(ushort, ushort, ushort)> into) where TReader : IReader
        {
            reader.Read(out int count);
            for (int i = 0; i < count; i++)
            {
                reader.Read(out ushort index);
                reader.Read(out ushort ours);
                reader.Read(out ushort fallback);
                into.Add((index, ours, fallback));
            }
        }
    }
}
