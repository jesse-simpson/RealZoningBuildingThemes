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
    ///    empty cells (and on the block's free lots). A level-up in progress to a design from
    ///    another game zone (our zones allow that) is paused in the saved copy, since it would
    ///    finish on the wrong game zone and be condemned. What it changed, and what to put back,
    ///    is kept as a record.
    /// 2. The game writes the save: its cells hold game zones only. The record is written too, as
    ///    this system's own save data (see <see cref="Serialize"/>). Without the mod, the game
    ///    skips data it has no reader for, so such a save loads as an ordinary city.
    /// 3. Right after, <see cref="SaveRestoreSystem"/> puts every change back exactly, so play
    ///    carries on as if nothing happened.
    /// 4. On load with the mod, <see cref="LoadRestoreSystem"/> reads the record and puts our zones
    ///    and the paused level-ups back before the city starts.
    ///
    /// The save step runs while the simulation is stopped (the game's own serialize phase), so a
    /// save never catches the city half-converted.
    /// </summary>
    public partial class SaveSwapSystem : GameSystemBase, IDefaultSerializable, ISerializable
    {
        /// <summary>Format of the saved record; bumped if the layout ever changes.</summary>
        private const int kFormat = 1;

        /// <summary>A cell or free lot of ours, rewritten in the saved copy.</summary>
        public struct Edit
        {
            public ushort Index;          // cell index in the block, or lot index
            public ZoneType Original;     // our zone (this session's index)
            public ZoneType Fallback;     // the game zone written in its place
            public ushort OriginalKey;    // our zone's prefab name, as a key into the name table
            public ushort FallbackKey;    // the game zone's prefab name, likewise
        }

        public class BlockEdits
        {
            public Entity Block;
            public readonly List<Edit> Cells = new List<Edit>();
            public readonly List<Edit> Lots = new List<Edit>();
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
        private readonly Dictionary<string, ushort> m_NameKeys = new Dictionary<string, ushort>();
        private readonly Dictionary<Entity, BlockEdits> m_Blocks = new Dictionary<Entity, BlockEdits>();
        private readonly List<PausedLevelUp> m_LevelUps = new List<PausedLevelUp>();

        // The record read from the save being loaded (used once by LoadRestoreSystem).
        private readonly List<string> m_LoadedNames = new List<string>();
        private readonly List<LoadedBlock> m_LoadedBlocks = new List<LoadedBlock>();
        private readonly List<LoadedLevelUp> m_LoadedLevelUps = new List<LoadedLevelUp>();

        public IReadOnlyCollection<BlockEdits> PendingBlocks => m_Blocks.Values;

        public IReadOnlyList<PausedLevelUp> PendingLevelUps => m_LevelUps;

        public IReadOnlyList<string> LoadedNames => m_LoadedNames;

        public IReadOnlyList<LoadedBlock> LoadedBlocks => m_LoadedBlocks;

        public IReadOnlyList<LoadedLevelUp> LoadedLevelUps => m_LoadedLevelUps;

        /// <summary>Set by step 1, cleared by step 3: there is something to put back.</summary>
        public bool HasPending { get; private set; }

        public void ClearPending()
        {
            m_Names.Clear();
            m_NameKeys.Clear();
            m_Blocks.Clear();
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
                foreach (BlockEdits b in m_Blocks.Values)
                {
                    cells += b.Cells.Count;
                    lots += b.Lots.Count;
                }

                Mod.log.Info($"SaveSafe: saving with game zones in place of custom zones: {cells:N0} cells in {m_Blocks.Count:N0} blocks, {lots:N0} free lots, {m_LevelUps.Count:N0} level-ups to another game zone's design paused in the saved copy; {timer.Elapsed.TotalMilliseconds:0.#} ms.");
            }
        }

        private void MakeSaveSafe()
        {
            // Our zone types, each with the game zone its free cells fall back to.
            var ours = new Dictionary<ushort, (ZoneType ours, string name, ZoneType fallback, string fallbackName)>();
            foreach (KeyValuePair<string, RzbtZonePrefab> kv in m_ZoneSetup.Zones)
            {
                if (!m_PrefabSystem.TryGetEntity(kv.Value, out Entity zoneEntity) || !EntityManager.HasComponent<ZoneData>(zoneEntity))
                {
                    continue;
                }

                ZoneType zoneType = EntityManager.GetComponentData<ZoneData>(zoneEntity).m_ZoneType;
                if (zoneType.Equals(ZoneType.None))
                {
                    continue;
                }

                ZoneType fallback = ZoneType.None;
                string fallbackName = string.Empty;
                if (m_ZoneSetup.BaseOf.TryGetValue(kv.Key, out ZonePrefab baseZone) && m_PrefabSystem.TryGetEntity(baseZone, out Entity baseEntity) &&
                    EntityManager.HasComponent<ZoneData>(baseEntity))
                {
                    fallback = EntityManager.GetComponentData<ZoneData>(baseEntity).m_ZoneType;
                    fallbackName = m_PrefabSystem.GetPrefabName(baseEntity);
                }

                ours[zoneType.m_Index] = (zoneType, kv.Key, fallback, fallbackName);
            }

            if (ours.Count == 0)
            {
                return;
            }

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
                    if (homePrefab == Entity.Null || !EntityManager.HasComponent<ZoneData>(homePrefab) || !EntityManager.IsComponentEnabled<PrefabData>(homePrefab))
                    {
                        continue; // no usable home zone: its cells get the zone's base zone below
                    }

                    ZoneType home = EntityManager.GetComponentData<ZoneData>(homePrefab).m_ZoneType;
                    if (home.Equals(ZoneType.None) || ours.ContainsKey(home.m_Index))
                    {
                        continue;
                    }

                    var writer = new LotWriter
                    {
                        m_System = this,
                        m_EntityManager = EntityManager,
                        m_Home = home,
                        m_HomeKey = Key(m_PrefabSystem.GetPrefabName(homePrefab)),
                        m_Ours = ours,
                    };
                    writer.Setup(EntityManager.GetComponentData<Transform>(building), EntityManager.GetComponentData<BuildingData>(prefab).m_LotSize);
                    tree.Iterate(ref writer);
                    if (writer.m_Count == 0)
                    {
                        continue; // not on our zones
                    }

                    // A level-up in progress to a design from another game zone would finish on
                    // cells of the wrong game zone if the mod were missing. Pause it in the saved
                    // copy; it is put back right after the save, and on load with the mod.
                    if (EntityManager.HasComponent<UnderConstruction>(building))
                    {
                        UnderConstruction construction = EntityManager.GetComponentData<UnderConstruction>(building);
                        Entity next = construction.m_NewPrefab;
                        if (next != Entity.Null && next != prefab && EntityManager.HasComponent<SpawnableBuildingData>(next) &&
                            EntityManager.GetComponentData<SpawnableBuildingData>(next).m_ZonePrefab != homePrefab)
                        {
                            m_LevelUps.Add(new PausedLevelUp
                            {
                                Building = building,
                                Construction = construction,
                                Current = m_PrefabSystem.GetPrefabName(prefab),
                                Target = m_PrefabSystem.GetPrefabName(next),
                            });
                            HasPending = true;
                            EntityManager.RemoveComponent<UnderConstruction>(building);
                        }
                    }
                }
            }
            finally
            {
                buildings.Dispose();
                m_ZoneSearch.AddSearchTreeReader(default);
            }

            // 2. Every other cell of our zones, and every free lot, takes the zone's base zone.
            var blocks = m_BlockQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < blocks.Length; i++)
                {
                    Entity block = blocks[i];
                    DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(block);
                    for (int c = 0; c < cells.Length; c++)
                    {
                        Cell cell = cells[c];
                        if (ours.TryGetValue(cell.m_Zone.m_Index, out var zone))
                        {
                            Edits(block).Cells.Add(new Edit
                            {
                                Index = (ushort)c,
                                Original = zone.ours,
                                Fallback = zone.fallback,
                                OriginalKey = Key(zone.name),
                                FallbackKey = Key(zone.fallbackName),
                            });
                            cell.m_Zone = zone.fallback;
                            cells[c] = cell;
                            HasPending = true;
                        }
                    }

                    if (!EntityManager.HasBuffer<VacantLot>(block))
                    {
                        continue;
                    }

                    DynamicBuffer<VacantLot> lots = EntityManager.GetBuffer<VacantLot>(block);
                    for (int l = 0; l < lots.Length; l++)
                    {
                        VacantLot lot = lots[l];
                        if (ours.TryGetValue(lot.m_Type.m_Index, out var zone))
                        {
                            Edits(block).Lots.Add(new Edit
                            {
                                Index = (ushort)l,
                                Original = zone.ours,
                                Fallback = zone.fallback,
                                OriginalKey = Key(zone.name),
                                FallbackKey = Key(zone.fallbackName),
                            });
                            lot.m_Type = zone.fallback;
                            lots[l] = lot;
                            HasPending = true;
                        }
                    }
                }
            }
            finally
            {
                blocks.Dispose();
            }
        }

        private BlockEdits Edits(Entity block)
        {
            if (!m_Blocks.TryGetValue(block, out BlockEdits edits))
            {
                edits = new BlockEdits { Block = block };
                m_Blocks[block] = edits;
            }

            return edits;
        }

        private ushort Key(string name)
        {
            name = name ?? string.Empty;
            if (!m_NameKeys.TryGetValue(name, out ushort key))
            {
                key = (ushort)m_Names.Count;
                m_Names.Add(name);
                m_NameKeys[name] = key;
            }

            return key;
        }

        /// <summary>Writes our zones' cells under a building's lot to its home zone, recording each change.</summary>
        private struct LotWriter : INativeQuadTreeIterator<Entity, Bounds2>, IUnsafeQuadTreeIterator<Entity, Bounds2>
        {
            public SaveSwapSystem m_System;
            public EntityManager m_EntityManager;
            public ZoneType m_Home;
            public ushort m_HomeKey;
            public Dictionary<ushort, (ZoneType ours, string name, ZoneType fallback, string fallbackName)> m_Ours;
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
                if (!MathUtils.Intersect(bounds, m_Bounds) || !m_EntityManager.HasComponent<Block>(blockEntity) || !m_EntityManager.HasBuffer<Cell>(blockEntity) ||
                    m_EntityManager.HasComponent<Temp>(blockEntity) || m_EntityManager.HasComponent<Deleted>(blockEntity))
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
                            if (m_Ours.TryGetValue(cell.m_Zone.m_Index, out var zone))
                            {
                                m_System.Edits(blockEntity).Cells.Add(new Edit
                                {
                                    Index = (ushort)index,
                                    Original = zone.ours,
                                    Fallback = m_Home,
                                    OriginalKey = m_System.Key(zone.name),
                                    FallbackKey = m_HomeKey,
                                });
                                m_System.HasPending = true;
                                cell.m_Zone = m_Home;
                                cells[index] = cell;
                                m_Count++;
                            }
                        }

                        position -= m_Right;
                    }

                    rowStart -= m_Forward;
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

            writer.Write(m_Blocks.Count);
            foreach (BlockEdits b in m_Blocks.Values)
            {
                writer.Write(b.Block);
                WriteEdits(writer, b.Cells);
                WriteEdits(writer, b.Lots);
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

        private static void WriteEdits<TWriter>(TWriter writer, List<Edit> edits) where TWriter : IWriter
        {
            writer.Write(edits.Count);
            foreach (Edit e in edits)
            {
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
