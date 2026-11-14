// The building search and the condemn/lift logic follow the game's ZoneCheckSystem and Plop the
// Growables' SelectiveZoneCheckSystem.
// Plop the Growables: Copyright (c) 2024-26 algernon (K. Algernon A. Sheppard). Licensed under the
// Apache License, Version 2.0 (LICENSES/Apache-2.0.txt; see also NOTICE.txt).
// Changed for Real Zoning & Building Themes: runs in place of the game's or Plop the Growables'
// check; a cell also passes when it is one of our zones that allows the building; the jobs only
// report changes, which are applied on the main thread the next update (skipping plopped
// buildings and logging examples); honours Plop the Growables' despawn setting by switching
// itself off.

using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Collections;
using Colossal.Mathematics;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Notifications;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Game.Zones;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace RealZoningBuildingThemes.Zones
{
    /// <summary>
    /// The city's zone check: condemns a grown building when the zoning under its lot no longer
    /// fits it. It replaces the game's check, which condemns every building grown in one of our
    /// zones (their cells are never their home zone), so the pardon pass had to lift thousands of
    /// condemnations a minute. Here a cell passes when it is the building's home zone, as in the
    /// game, or one of our zones that allows the building.
    ///
    /// With Plop the Growables, its own check (SelectiveZoneCheckSystem) has already replaced the
    /// game's. Its "despawn spawned buildings when zoning changes" setting is followed: when it is
    /// off nothing is checked, as Plop the Growables intends; when it is on, this check runs in its
    /// place and, like it, never touches plopped buildings.
    ///
    /// How it runs, as the game's check does: when zoning changed somewhere, parallel jobs find the
    /// grown buildings in the changed areas and test each lot cell by cell. The jobs only report
    /// buildings whose state should change (condemn or lift); those are applied on the main thread
    /// at the start of the next update, once the jobs have long finished. Applying a frame later
    /// also lets Plop the Growables tag a fresh plop first, so plops are never condemned.
    /// If anything throws, the replaced check is switched back on and this one turns itself off.
    /// </summary>
    public partial class RzbtZoneCheckSystem : GameSystemBase
    {
        private Game.Zones.UpdateCollectSystem m_ZoneUpdateCollect;
        private Game.Zones.SearchSystem m_ZoneSearch;
        private Game.Objects.SearchSystem m_ObjectSearch;
        private ToolSystem m_ToolSystem;
        private IconCommandSystem m_IconCommandSystem;
        private AllowListSystem m_AllowList;
        private EntityQuery m_BuildingConfigQuery;

        private ZoneCheckSystem m_GameCheck;
        private ComponentSystemBase m_PlopCheck;
        private PropertyInfo m_PlopDespawnSetting;
        private ComponentSystemBase m_Replaced;
        private bool m_Failed;

        // The jobs' copy of the allow-lists: building prefab -> zone type indices of our zones that
        // allow it. Rebuilt when the rules change, never while a check is running.
        private NativeParallelMultiHashMap<Entity, ushort> m_Allowed;
        private int m_AllowedVersion = -1;
        private int m_VariantCursor;

        // The check scheduled last update; its results are applied at the start of the next one.
        private JobHandle m_Handle;
        private NativeList<Entity> m_Buildings;
        private NativeQueue<Result> m_Results;
        private bool m_Pending;

        private bool m_LoggedWaiting;
        private int m_Checked;
        private int m_Condemned;
        private int m_Lifted;
        private DateTime m_LastLog = DateTime.MinValue;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ZoneUpdateCollect = World.GetOrCreateSystemManaged<Game.Zones.UpdateCollectSystem>();
            m_ZoneSearch = World.GetOrCreateSystemManaged<Game.Zones.SearchSystem>();
            m_ObjectSearch = World.GetOrCreateSystemManaged<Game.Objects.SearchSystem>();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_IconCommandSystem = World.GetOrCreateSystemManaged<IconCommandSystem>();
            m_AllowList = World.GetOrCreateSystemManaged<AllowListSystem>();
            m_GameCheck = World.GetOrCreateSystemManaged<ZoneCheckSystem>();
            m_BuildingConfigQuery = GetEntityQuery(ComponentType.ReadOnly<BuildingConfigurationData>());
            m_Allowed = new NativeParallelMultiHashMap<Entity, ushort>(1024, Allocator.Persistent);
        }

        protected override void OnDestroy()
        {
            DisposePending();
            if (m_Allowed.IsCreated)
            {
                m_Allowed.Dispose();
            }

            Restore();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            if (m_Failed)
            {
                return;
            }

            try
            {
                if (m_Pending)
                {
                    ApplyResults();
                }

                if (!TakeOver())
                {
                    return;
                }

                // Until the allow-lists exist (the first city of a session, until the simulation has
                // run a few frames; it may be paused for a while after loading), every building on
                // our zones would look unallowed and be condemned. Wait for them: the game's own
                // check stays off meanwhile, so nothing is condemned for want of a list.
                if (m_AllowList.RulesVersion == 0)
                {
                    if (!m_LoggedWaiting && m_ZoneUpdateCollect.isUpdated)
                    {
                        m_LoggedWaiting = true;
                        Mod.log.Info("ZoneCheck: waiting for the zones' allow-lists before checking any building (they are built once the simulation runs).");
                    }
                }
                else if (m_ZoneUpdateCollect.isUpdated && !m_BuildingConfigQuery.IsEmptyIgnoreFilter)
                {
                    ScheduleCheck();
                }

                if (m_Checked > 0 && (DateTime.UtcNow - m_LastLog).TotalSeconds >= 60)
                {
                    Mod.log.Info($"ZoneCheck: checked {m_Checked} buildings in the last minute; condemned {m_Condemned} whose zoning no longer fits, lifted {m_Lifted}.");
                    foreach (string example in m_Examples)
                    {
                        Mod.log.Info($"ZoneCheck:   condemned {example}");
                    }

                    m_Examples.Clear();
                    m_LastLog = DateTime.UtcNow;
                    m_Checked = m_Condemned = m_Lifted = 0;
                }
            }
            catch (Exception e)
            {
                m_Failed = true;
                DisposePending();
                Restore();
                Mod.log.Error($"ZoneCheck: failed ({e}); the replaced zone check is back on and the pardon pass covers our zones.");
            }
        }

        /// <summary>Decides which check to replace this frame. False when no check should run.</summary>
        private bool TakeOver()
        {
            FindPlopCheck();
            ComponentSystemBase target = m_GameCheck;
            if (m_PlopCheck != null)
            {
                bool despawn = m_PlopDespawnSetting == null || (bool)m_PlopDespawnSetting.GetValue(m_PlopCheck);
                if (!despawn)
                {
                    // Plop the Growables is set never to condemn spawned buildings: nothing to do.
                    Restore();
                    return false;
                }

                target = m_PlopCheck;
            }

            if (m_Replaced != target)
            {
                Restore();
                m_Replaced = target;
                Mod.log.Info($"ZoneCheck: replacing {target.GetType().FullName}.");
            }

            target.Enabled = false; // kept off every frame, in case anything turns it back on
            return true;
        }

        private void Restore()
        {
            if (m_Replaced != null)
            {
                // The game's check stays off when Plop the Growables is installed (it turned it off).
                if (m_Replaced != m_GameCheck || m_PlopCheck == null)
                {
                    m_Replaced.Enabled = true;
                }

                m_Replaced = null;
            }
        }

        // Updates until the next look for Plop the Growables' check. It is looked for again until
        // found, and at once when a city finishes loading: this system can first run before other
        // mods have created their systems (seen 2026-11-13: it ran 17 s after start, found
        // nothing, took over the game's check, and Plop the Growables' check then ran alongside
        // it, condemning buildings on our zones that the pardon pass had to lift).
        private int m_PlopSearchWait;
        private const int kPlopSearchInterval = 300;

        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            m_PlopSearchWait = 0;
        }

        private void FindPlopCheck()
        {
            if (m_PlopCheck != null || m_PlopSearchWait-- > 0)
            {
                return;
            }

            m_PlopSearchWait = kPlopSearchInterval;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name.IndexOf("Plop", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                Type type = null;
                try
                {
                    foreach (Type t in assembly.GetTypes())
                    {
                        if (t.Name == "SelectiveZoneCheckSystem" && typeof(ComponentSystemBase).IsAssignableFrom(t))
                        {
                            type = t;
                            break;
                        }
                    }
                }
                catch (ReflectionTypeLoadException)
                {
                    continue;
                }

                if (type != null)
                {
                    m_PlopCheck = World.GetExistingSystemManaged(type);
                    if (m_PlopCheck != null)
                    {
                        m_PlopDespawnSetting = type.GetProperty("SpawnedZoneDespawn", BindingFlags.Public | BindingFlags.Instance);
                        Mod.log.Info($"ZoneCheck: found Plop the Growables' zone check (despawn setting {(m_PlopDespawnSetting != null ? "found" : "not found")}).");
                    }

                    return; // not created yet: looked for again later
                }
            }
        }

        /// <summary>Copies the allow-lists into the jobs' map (only when no check is running).</summary>
        private void RebuildAllowed()
        {
            m_Allowed.Clear();
            foreach (var kv in m_AllowList.ZonesByBuilding)
            {
                foreach (ushort zone in kv.Value)
                {
                    m_Allowed.Add(kv.Key, zone);
                }
            }

            m_AllowedVersion = m_AllowList.RulesVersion;
            m_VariantCursor = m_AllowList.AddedVariants.Count;
        }

        /// <summary>Adds the Placemaker copies the allow-lists learned since the map was built (only when no check is running).</summary>
        private void AddNewVariants()
        {
            IReadOnlyList<Entity> added = m_AllowList.AddedVariants;
            if (m_VariantCursor > added.Count)
            {
                RebuildAllowed();
                return;
            }

            for (; m_VariantCursor < added.Count; m_VariantCursor++)
            {
                Entity variant = added[m_VariantCursor];
                if (m_Allowed.ContainsKey(variant) || !m_AllowList.ZonesByBuilding.TryGetValue(variant, out List<ushort> zones))
                {
                    continue;
                }

                foreach (ushort zone in zones)
                {
                    m_Allowed.Add(variant, zone);
                }
            }
        }

        /// <summary>Schedules the jobs that find and test the buildings in the areas whose zoning changed.</summary>
        private void ScheduleCheck()
        {
            if (m_AllowList.RulesVersion != m_AllowedVersion)
            {
                RebuildAllowed();
            }
            else
            {
                AddNewVariants();
            }

            NativeList<Bounds2> updated = m_ZoneUpdateCollect.GetUpdatedBounds(true, out JobHandle boundsDeps);
            NativeQuadTree<Entity, QuadTreeBoundsXZ> objects = m_ObjectSearch.GetStaticSearchTree(true, out JobHandle objectDeps);
            NativeQuadTree<Entity, Bounds2> zoneTree = m_ZoneSearch.GetSearchTree(true, out JobHandle zoneDeps);

            var queue = new NativeQueue<Entity>(Allocator.TempJob);
            m_Buildings = new NativeList<Entity>(Allocator.TempJob);
            m_Results = new NativeQueue<Result>(Allocator.TempJob);

            var find = new FindBuildingsJob
            {
                m_Bounds = updated.AsDeferredJobArray(),
                m_SearchTree = objects,
                m_BuildingData = GetComponentLookup<Building>(true),
                m_PrefabRefData = GetComponentLookup<PrefabRef>(true),
                m_SpawnableData = GetComponentLookup<SpawnableBuildingData>(true),
                m_SignatureData = GetComponentLookup<SignatureBuildingData>(true),
                m_Queue = queue.AsParallelWriter(),
            };

            var collect = new CollectJob { m_Queue = queue, m_List = m_Buildings };

            var check = new CheckJob
            {
                m_Buildings = m_Buildings.AsDeferredJobArray(),
                m_SearchTree = zoneTree,
                m_Allowed = m_Allowed,
                m_CondemnedData = GetComponentLookup<Condemned>(true),
                m_DeletedData = GetComponentLookup<Deleted>(true),
                m_TempData = GetComponentLookup<Temp>(true),
                m_BlockData = GetComponentLookup<Block>(true),
                m_ValidAreaData = GetComponentLookup<ValidArea>(true),
                m_TransformData = GetComponentLookup<Transform>(true),
                m_AttachedData = GetComponentLookup<Attached>(true),
                m_PrefabRefData = GetComponentLookup<PrefabRef>(true),
                m_PrefabData = GetComponentLookup<PrefabData>(true),
                m_BuildingData = GetComponentLookup<BuildingData>(true),
                m_SpawnableData = GetComponentLookup<SpawnableBuildingData>(true),
                m_PlaceholderData = GetComponentLookup<PlaceholderBuildingData>(true),
                m_ZoneData = GetComponentLookup<ZoneData>(true),
                m_Cells = GetBufferLookup<Cell>(true),
                m_EditorMode = m_ToolSystem.actionMode.IsEditor(),
                m_Results = m_Results.AsParallelWriter(),
            };

            JobHandle findHandle = find.Schedule(updated, 1, JobHandle.CombineDependencies(Dependency, boundsDeps, objectDeps));
            JobHandle collectHandle = collect.Schedule(findHandle);
            JobHandle checkHandle = check.Schedule(m_Buildings, 1, JobHandle.CombineDependencies(collectHandle, zoneDeps));
            queue.Dispose(collectHandle);

            m_ZoneUpdateCollect.AddBoundsReader(findHandle);
            m_ObjectSearch.AddStaticSearchTreeReader(findHandle);
            m_ZoneSearch.AddSearchTreeReader(checkHandle);
            Dependency = checkHandle;
            m_Handle = checkHandle;
            m_Pending = true;
        }

        /// <summary>Applies the last check's results: condemns or lifts, skipping plopped and removed buildings.</summary>
        private void ApplyResults()
        {
            m_Handle.Complete();
            m_Pending = false;
            try
            {
                m_Checked += m_Buildings.Length;
                if (m_Results.Count == 0)
                {
                    return;
                }

                var config = m_BuildingConfigQuery.GetSingleton<BuildingConfigurationData>();
                IconCommandBuffer icons = m_IconCommandSystem.CreateCommandBuffer();
                NativeQuadTree<Entity, Bounds2> zoneTree = default;
                bool haveTree = false;
                while (m_Results.TryDequeue(out Result result))
                {
                    Entity building = result.m_Building;
                    if (!EntityManager.Exists(building) || EntityManager.HasComponent<Deleted>(building) ||
                        EntityManager.HasComponent<Temp>(building) || PlopCompat.IsPlopped(EntityManager, building))
                    {
                        continue;
                    }

                    bool condemned = EntityManager.HasComponent<Condemned>(building);
                    if (result.m_Valid && condemned)
                    {
                        EntityManager.RemoveComponent<Condemned>(building);
                        icons.Remove(building, config.m_CondemnedNotification);
                        m_Lifted++;
                    }
                    else if (!result.m_Valid && !condemned)
                    {
                        // A Placemaker copy the job did not know yet: test it again as its design.
                        if (FitsAsCopy(building, ref zoneTree, ref haveTree))
                        {
                            continue;
                        }

                        if (m_Examples.Count < 10)
                        {
                            m_Examples.Add(Describe(building));
                        }

                        EntityManager.AddComponent<Condemned>(building);
                        if (!EntityManager.HasComponent<Destroyed>(building) && !EntityManager.HasComponent<Abandoned>(building))
                        {
                            icons.Add(building, config.m_CondemnedNotification, IconPriority.FatalProblem);
                        }

                        m_Condemned++;
                    }
                }

                if (haveTree)
                {
                    m_ZoneSearch.AddSearchTreeReader(default(JobHandle));
                }
            }
            finally
            {
                m_Buildings.Dispose();
                m_Results.Dispose();
            }
        }

        /// <summary>
        /// True when the building's prefab is a Placemaker copy whose design is allowed where it
        /// stands. Placemaker makes copies at any moment (mostly as disabled prefab entities no query
        /// lists), so the job can meet one before the allow-lists know it; it is learned here.
        /// </summary>
        private bool FitsAsCopy(Entity building, ref NativeQuadTree<Entity, Bounds2> tree, ref bool haveTree)
        {
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(building).m_Prefab;
            if (m_Allowed.ContainsKey(prefab) || !m_AllowList.EnsureVariant(prefab) ||
                !EntityManager.HasComponent<BuildingData>(prefab) || !EntityManager.HasComponent<SpawnableBuildingData>(prefab))
            {
                return false;
            }

            ZoneType home = ZoneType.None;
            Entity homePrefab = EntityManager.GetComponentData<SpawnableBuildingData>(prefab).m_ZonePrefab;
            if (homePrefab != Entity.Null && EntityManager.HasComponent<ZoneData>(homePrefab) && EntityManager.IsComponentEnabled<PrefabData>(homePrefab))
            {
                home = EntityManager.GetComponentData<ZoneData>(homePrefab).m_ZoneType;
            }

            if (!haveTree)
            {
                tree = m_ZoneSearch.GetSearchTree(true, out JobHandle deps);
                deps.Complete();
                haveTree = true;
            }

            return ZoneLotTest.LotIsAllowed(EntityManager, tree, EntityManager.GetComponentData<Transform>(building),
                EntityManager.GetComponentData<BuildingData>(prefab).m_LotSize, home, m_AllowList.GetAllowingZones(prefab));
        }

        private void DisposePending()
        {
            if (m_Pending)
            {
                m_Handle.Complete();
                m_Buildings.Dispose();
                m_Results.Dispose();
                m_Pending = false;
            }
        }

        private readonly List<string> m_Examples = new List<string>();

        /// <summary>For the log: the building, its home zone, a level-up in progress, and the zones under its lot.</summary>
        private string Describe(Entity building)
        {
            var prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(building).m_Prefab;
            Entity home = EntityManager.GetComponentData<SpawnableBuildingData>(prefab).m_ZonePrefab;
            string text = $"'{prefabs.GetPrefabName(prefab)}' (home zone '{(home != Entity.Null ? prefabs.GetPrefabName(home) : "none")}'";
            if (m_AllowList.BaseOf(prefab) != prefab)
            {
                text += ", a Placemaker copy";
            }
            if (EntityManager.HasComponent<Game.Objects.UnderConstruction>(building))
            {
                Entity next = EntityManager.GetComponentData<Game.Objects.UnderConstruction>(building).m_NewPrefab;
                text += next != Entity.Null ? $", levelling up to '{prefabs.GetPrefabName(next)}'" : ", under construction";
            }

            try
            {
                NativeQuadTree<Entity, Bounds2> tree = m_ZoneSearch.GetSearchTree(true, out JobHandle deps);
                deps.Complete();
                var lot = EntityManager.GetComponentData<BuildingData>(prefab).m_LotSize;
                using (LotCells cells = LotCells.Read(EntityManager, tree, EntityManager.GetComponentData<Transform>(building), lot))
                {
                    var zones = new SortedSet<ushort>();
                    for (int i = 0; i < cells.Zones.Length; i++)
                    {
                        zones.Add(cells.Zones[i]);
                    }

                    text += $", cell zone types {string.Join("/", zones)}";
                }

                m_ZoneSearch.AddSearchTreeReader(default);
            }
            catch (Exception)
            {
                // diagnostics only
            }

            return text + ")";
        }

        /// <summary>A building whose state should change: lift its condemnation (valid) or condemn it.</summary>
        private struct Result
        {
            public Entity m_Building;
            public bool m_Valid;
        }

        /// <summary>Grown (spawnable, non-signature) buildings whose bounds meet a changed zone area, as the game finds them.</summary>
        [BurstCompile]
        private struct FindBuildingsJob : IJobParallelForDefer
        {
            [ReadOnly] public NativeArray<Bounds2> m_Bounds;
            [ReadOnly] public NativeQuadTree<Entity, QuadTreeBoundsXZ> m_SearchTree;
            [ReadOnly] public ComponentLookup<Building> m_BuildingData;
            [ReadOnly] public ComponentLookup<PrefabRef> m_PrefabRefData;
            [ReadOnly] public ComponentLookup<SpawnableBuildingData> m_SpawnableData;
            [ReadOnly] public ComponentLookup<SignatureBuildingData> m_SignatureData;
            public NativeQueue<Entity>.ParallelWriter m_Queue;

            public void Execute(int index)
            {
                var iterator = new Iterator
                {
                    m_Bounds = m_Bounds[index],
                    m_BuildingData = m_BuildingData,
                    m_PrefabRefData = m_PrefabRefData,
                    m_SpawnableData = m_SpawnableData,
                    m_SignatureData = m_SignatureData,
                    m_Queue = m_Queue,
                };
                m_SearchTree.Iterate(ref iterator);
            }

            private struct Iterator : INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>, IUnsafeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
            {
                public Bounds2 m_Bounds;
                public ComponentLookup<Building> m_BuildingData;
                public ComponentLookup<PrefabRef> m_PrefabRefData;
                public ComponentLookup<SpawnableBuildingData> m_SpawnableData;
                public ComponentLookup<SignatureBuildingData> m_SignatureData;
                public NativeQueue<Entity>.ParallelWriter m_Queue;

                public bool Intersect(QuadTreeBoundsXZ bounds)
                {
                    return MathUtils.Intersect(bounds.m_Bounds.xz, m_Bounds);
                }

                public void Iterate(QuadTreeBoundsXZ bounds, Entity entity)
                {
                    if (!MathUtils.Intersect(bounds.m_Bounds.xz, m_Bounds) || !m_BuildingData.HasComponent(entity) || !m_PrefabRefData.HasComponent(entity))
                    {
                        return;
                    }

                    Entity prefab = m_PrefabRefData[entity].m_Prefab;
                    if (m_SpawnableData.HasComponent(prefab) && !m_SignatureData.HasComponent(prefab))
                    {
                        m_Queue.Enqueue(entity);
                    }
                }
            }
        }

        /// <summary>Sorts the found buildings and drops duplicates (a building can meet several changed areas).</summary>
        [BurstCompile]
        private struct CollectJob : IJob
        {
            public NativeQueue<Entity> m_Queue;
            public NativeList<Entity> m_List;

            public void Execute()
            {
                int count = m_Queue.Count;
                if (count == 0)
                {
                    return;
                }

                m_List.ResizeUninitialized(count);
                for (int i = 0; i < count; i++)
                {
                    m_List[i] = m_Queue.Dequeue();
                }

                m_List.Sort(new EntityComparer());
                Entity last = Entity.Null;
                int write = 0;
                for (int read = 0; read < m_List.Length; read++)
                {
                    Entity entity = m_List[read];
                    if (entity != last)
                    {
                        m_List[write++] = entity;
                        last = entity;
                    }
                }

                if (write < m_List.Length)
                {
                    m_List.RemoveRangeSwapBack(write, m_List.Length - write);
                }
            }

            private struct EntityComparer : IComparer<Entity>
            {
                public int Compare(Entity x, Entity y) => x.Index - y.Index;
            }
        }

        /// <summary>
        /// The game's two tests (a placeholder parent of the same zone, or the zoning under the lot),
        /// with one change: a cell passes when it is the building's home zone (unless that zone's
        /// prefab is switched off) or one of our zones that allows the building. Reports only
        /// buildings whose condemned state should change.
        /// </summary>
        [BurstCompile]
        private struct CheckJob : IJobParallelForDefer
        {
            [ReadOnly] public NativeArray<Entity> m_Buildings;
            [ReadOnly] public NativeQuadTree<Entity, Bounds2> m_SearchTree;
            [ReadOnly] public NativeParallelMultiHashMap<Entity, ushort> m_Allowed;
            [ReadOnly] public ComponentLookup<Condemned> m_CondemnedData;
            [ReadOnly] public ComponentLookup<Deleted> m_DeletedData;
            [ReadOnly] public ComponentLookup<Temp> m_TempData;
            [ReadOnly] public ComponentLookup<Block> m_BlockData;
            [ReadOnly] public ComponentLookup<ValidArea> m_ValidAreaData;
            [ReadOnly] public ComponentLookup<Transform> m_TransformData;
            [ReadOnly] public ComponentLookup<Attached> m_AttachedData;
            [ReadOnly] public ComponentLookup<PrefabRef> m_PrefabRefData;
            [ReadOnly] public ComponentLookup<PrefabData> m_PrefabData;
            [ReadOnly] public ComponentLookup<BuildingData> m_BuildingData;
            [ReadOnly] public ComponentLookup<SpawnableBuildingData> m_SpawnableData;
            [ReadOnly] public ComponentLookup<PlaceholderBuildingData> m_PlaceholderData;
            [ReadOnly] public ComponentLookup<ZoneData> m_ZoneData;
            [ReadOnly] public BufferLookup<Cell> m_Cells;
            public bool m_EditorMode;
            public NativeQueue<Result>.ParallelWriter m_Results;

            public void Execute(int index)
            {
                Entity building = m_Buildings[index];
                if (m_DeletedData.HasComponent(building) || m_TempData.HasComponent(building) ||
                    !m_TransformData.HasComponent(building) || !m_PrefabRefData.HasComponent(building))
                {
                    return;
                }

                Entity prefab = m_PrefabRefData[building].m_Prefab;
                if (!m_BuildingData.HasComponent(prefab) || !m_SpawnableData.HasComponent(prefab))
                {
                    return;
                }

                SpawnableBuildingData spawnable = m_SpawnableData[prefab];
                bool valid = m_EditorMode || ValidateAttachedParent(building, spawnable) || ValidateZoneBlocks(building, prefab, spawnable);
                if (valid == m_CondemnedData.HasComponent(building))
                {
                    m_Results.Enqueue(new Result { m_Building = building, m_Valid = valid });
                }
            }

            private bool ValidateAttachedParent(Entity building, SpawnableBuildingData spawnable)
            {
                if (m_AttachedData.HasComponent(building))
                {
                    Entity parent = m_AttachedData[building].m_Parent;
                    if (m_PrefabRefData.HasComponent(parent))
                    {
                        Entity parentPrefab = m_PrefabRefData[parent].m_Prefab;
                        if (m_PlaceholderData.HasComponent(parentPrefab) && m_PlaceholderData[parentPrefab].m_ZonePrefab == spawnable.m_ZonePrefab)
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            private bool ValidateZoneBlocks(Entity building, Entity prefab, SpawnableBuildingData spawnable)
            {
                // The home zone counts unless its prefab is switched off (the game then condemns);
                // our zones that allow the building always count.
                ZoneType home = ZoneType.None;
                if (m_ZoneData.HasComponent(spawnable.m_ZonePrefab))
                {
                    home = m_ZoneData[spawnable.m_ZonePrefab].m_ZoneType;
                    if (!home.Equals(ZoneType.None) && !m_PrefabData.IsComponentEnabled(spawnable.m_ZonePrefab))
                    {
                        home = ZoneType.None;
                    }
                }

                var zones = new FixedList128Bytes<ushort>();
                if (m_Allowed.TryGetFirstValue(prefab, out ushort zone, out NativeParallelMultiHashMapIterator<Entity> it))
                {
                    do
                    {
                        if (zones.Length < zones.Capacity)
                        {
                            zones.Add(zone);
                        }
                    }
                    while (m_Allowed.TryGetNextValue(out zone, ref it));
                }

                Transform transform = m_TransformData[building];
                int2 lotSize = m_BuildingData[prefab].m_LotSize;
                float2 right = math.rotate(transform.m_Rotation, new float3(8f, 0f, 0f)).xz;
                float2 forward = math.rotate(transform.m_Rotation, new float3(0f, 0f, 8f)).xz;
                float2 halfRight = right * (lotSize.x * 0.5f - 0.5f);
                float2 halfForward = forward * (lotSize.y * 0.5f - 0.5f);
                float2 extent = math.abs(halfForward) + math.abs(halfRight);

                var validated = new NativeArray<bool>(lotSize.x * lotSize.y, Allocator.Temp, NativeArrayOptions.ClearMemory);
                var iterator = new LotIterator
                {
                    m_Bounds = new Bounds2(transform.m_Position.xz - extent, transform.m_Position.xz + extent),
                    m_LotSize = lotSize,
                    m_StartPosition = transform.m_Position.xz + halfForward + halfRight,
                    m_Right = right,
                    m_Forward = forward,
                    m_Home = home,
                    m_HasHome = !home.Equals(ZoneType.None),
                    m_Zones = zones,
                    m_Validated = validated,
                    m_BlockData = m_BlockData,
                    m_ValidAreaData = m_ValidAreaData,
                    m_Cells = m_Cells,
                };
                m_SearchTree.Iterate(ref iterator);

                bool ok = (iterator.m_Directions & CellFlags.Roadside) != 0;
                for (int i = 0; i < validated.Length; i++)
                {
                    ok &= validated[i];
                }

                validated.Dispose();
                return ok;
            }
        }

        /// <summary>The game's lot iterator (ZoneCheckSystem), with our zones counted as described on CheckJob.</summary>
        private struct LotIterator : INativeQuadTreeIterator<Entity, Bounds2>, IUnsafeQuadTreeIterator<Entity, Bounds2>
        {
            public Bounds2 m_Bounds;
            public int2 m_LotSize;
            public float2 m_StartPosition;
            public float2 m_Right;
            public float2 m_Forward;
            public ZoneType m_Home;
            public bool m_HasHome;
            public FixedList128Bytes<ushort> m_Zones;
            public CellFlags m_Directions;
            public NativeArray<bool> m_Validated;
            public ComponentLookup<Block> m_BlockData;
            public ComponentLookup<ValidArea> m_ValidAreaData;
            public BufferLookup<Cell> m_Cells;

            public bool Intersect(Bounds2 bounds)
            {
                return MathUtils.Intersect(bounds, m_Bounds);
            }

            public void Iterate(Bounds2 bounds, Entity blockEntity)
            {
                if (!MathUtils.Intersect(bounds, m_Bounds) || !m_ValidAreaData.HasComponent(blockEntity) ||
                    !m_BlockData.HasComponent(blockEntity) || !m_Cells.HasBuffer(blockEntity))
                {
                    return;
                }

                ValidArea validArea = m_ValidAreaData[blockEntity];
                if (validArea.m_Area.y <= validArea.m_Area.x)
                {
                    return;
                }

                var target = new Block { m_Direction = m_Forward };
                Block block = m_BlockData[blockEntity];
                DynamicBuffer<Cell> cells = m_Cells[blockEntity];

                float2 rowStart = m_StartPosition;
                for (int y = 0; y < m_LotSize.y; y++)
                {
                    float2 position = rowStart;
                    for (int x = 0; x < m_LotSize.x; x++)
                    {
                        int2 cellIndex = ZoneUtils.GetCellIndex(block, position);
                        if (math.all((cellIndex >= validArea.m_Area.xz) & (cellIndex < validArea.m_Area.yw)))
                        {
                            Cell cell = cells[cellIndex.y * block.m_Size.x + cellIndex.x];
                            if ((cell.m_State & CellFlags.Visible) != 0 && ZoneOk(cell.m_Zone))
                            {
                                m_Validated[y * m_LotSize.x + x] = true;
                                if ((cell.m_State & (CellFlags.Roadside | CellFlags.RoadLeft | CellFlags.RoadRight | CellFlags.RoadBack)) != 0)
                                {
                                    CellFlags roadDirection = ZoneUtils.GetRoadDirection(target, block, cell.m_State);
                                    int4 mask = math.select(int4.zero, new int4(512, 4, 1024, 2048), new bool4(new int2(x, y) == 0, new int2(x, y) == m_LotSize - 1));
                                    m_Directions |= (CellFlags)((uint)roadDirection & (uint)(ushort)math.csum(mask));
                                }
                            }
                        }

                        position -= m_Right;
                    }

                    rowStart -= m_Forward;
                }
            }

            private bool ZoneOk(ZoneType zone)
            {
                if (m_HasHome && zone.Equals(m_Home))
                {
                    return true;
                }

                for (int i = 0; i < m_Zones.Length; i++)
                {
                    if (m_Zones[i] == zone.m_Index)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }
}
