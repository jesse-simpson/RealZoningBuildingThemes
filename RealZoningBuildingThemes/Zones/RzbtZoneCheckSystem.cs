// The building search and the condemn/lift logic follow the game's ZoneCheckSystem and Plop the
// Growables' SelectiveZoneCheckSystem.
// Plop the Growables: Copyright (c) 2024-26 algernon (K. Algernon A. Sheppard). Licensed under the
// Apache License, Version 2.0 (LICENSES/Apache-2.0.txt; see also NOTICE.txt).
// Changed for Real Zoning & Building Themes: runs on the main thread in place of the game's or
// Plop the Growables' check; a lot also passes when every cell is one of our zones that allows the
// building (ZoneLotTest); honours Plop the Growables' despawn setting by switching itself off;
// skips plopped buildings; logs examples of what it condemns.

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
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
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
    /// place and, like it, never touches plopped buildings. Buildings created this frame are left
    /// to the next check (a fresh plop is tagged by Plop the Growables only in this phase).
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
        }

        protected override void OnDestroy()
        {
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
                if (!TakeOver())
                {
                    return;
                }

                if (m_ZoneUpdateCollect.isUpdated && !m_BuildingConfigQuery.IsEmptyIgnoreFilter)
                {
                    Check();
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

        private bool m_SearchedPlop;

        private void FindPlopCheck()
        {
            if (m_SearchedPlop)
            {
                return;
            }

            m_SearchedPlop = true;
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
                    m_PlopDespawnSetting = type.GetProperty("SpawnedZoneDespawn", BindingFlags.Public | BindingFlags.Instance);
                    Mod.log.Info($"ZoneCheck: found Plop the Growables' zone check ({(m_PlopCheck != null ? "running" : "not running")}; despawn setting {(m_PlopDespawnSetting != null ? "found" : "not found")}).");
                    return;
                }
            }
        }

        private void Check()
        {
            NativeList<Bounds2> updated = m_ZoneUpdateCollect.GetUpdatedBounds(true, out JobHandle boundsDeps);
            NativeQuadTree<Entity, QuadTreeBoundsXZ> objects = m_ObjectSearch.GetStaticSearchTree(true, out JobHandle objectDeps);
            boundsDeps.Complete();
            objectDeps.Complete();

            var buildings = new HashSet<Entity>();
            var finder = new BuildingFinder { m_EntityManager = EntityManager, m_Results = buildings };
            for (int i = 0; i < updated.Length; i++)
            {
                finder.m_Bounds = updated[i];
                objects.Iterate(ref finder);
            }

            m_ZoneUpdateCollect.AddBoundsReader(default);
            m_ObjectSearch.AddStaticSearchTreeReader(default);
            if (buildings.Count == 0)
            {
                return;
            }

            NativeQuadTree<Entity, Bounds2> zoneTree = m_ZoneSearch.GetSearchTree(true, out JobHandle zoneDeps);
            zoneDeps.Complete();

            bool editor = m_ToolSystem.actionMode.IsEditor();
            var config = m_BuildingConfigQuery.GetSingleton<BuildingConfigurationData>();
            IconCommandBuffer icons = default;
            bool haveIcons = false;
            var condemn = new List<Entity>();
            var lift = new List<Entity>();

            foreach (Entity building in buildings)
            {
                if (EntityManager.HasComponent<Created>(building) || EntityManager.HasComponent<Deleted>(building) ||
                    EntityManager.HasComponent<Temp>(building) || PlopCompat.IsPlopped(EntityManager, building))
                {
                    continue;
                }

                m_Checked++;
                bool valid = editor || IsValid(zoneTree, building);
                bool condemned = EntityManager.HasComponent<Condemned>(building);
                if (valid && condemned)
                {
                    lift.Add(building);
                }
                else if (!valid && !condemned)
                {
                    condemn.Add(building);
                }
            }

            m_ZoneSearch.AddSearchTreeReader(default);

            foreach (Entity building in lift)
            {
                if (!haveIcons)
                {
                    icons = m_IconCommandSystem.CreateCommandBuffer();
                    haveIcons = true;
                }

                EntityManager.RemoveComponent<Condemned>(building);
                icons.Remove(building, config.m_CondemnedNotification);
                m_Lifted++;
            }

            foreach (Entity building in condemn)
            {
                if (!haveIcons)
                {
                    icons = m_IconCommandSystem.CreateCommandBuffer();
                    haveIcons = true;
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

        private readonly List<string> m_Examples = new List<string>();

        /// <summary>For the log: the building, its home zone, a level-up in progress, and the zones under its lot.</summary>
        private string Describe(Entity building)
        {
            var prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(building).m_Prefab;
            Entity home = EntityManager.GetComponentData<SpawnableBuildingData>(prefab).m_ZonePrefab;
            string text = $"'{prefabs.GetPrefabName(prefab)}' (home zone '{(home != Entity.Null ? prefabs.GetPrefabName(home) : "none")}'";
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

        /// <summary>The game's two tests: a placeholder parent of the same zone, or the zoning under the lot.</summary>
        private bool IsValid(NativeQuadTree<Entity, Bounds2> zoneTree, Entity building)
        {
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(building).m_Prefab;
            var spawnable = EntityManager.GetComponentData<SpawnableBuildingData>(prefab);

            if (EntityManager.HasComponent<Attached>(building))
            {
                Entity parent = EntityManager.GetComponentData<Attached>(building).m_Parent;
                if (EntityManager.HasComponent<PrefabRef>(parent))
                {
                    Entity parentPrefab = EntityManager.GetComponentData<PrefabRef>(parent).m_Prefab;
                    if (EntityManager.HasComponent<PlaceholderBuildingData>(parentPrefab) &&
                        EntityManager.GetComponentData<PlaceholderBuildingData>(parentPrefab).m_ZonePrefab == spawnable.m_ZonePrefab)
                    {
                        return true;
                    }
                }
            }

            // The home zone counts unless its prefab is switched off (the game then condemns);
            // our zones that allow the building always count.
            ZoneType home = ZoneType.None;
            if (EntityManager.HasComponent<ZoneData>(spawnable.m_ZonePrefab))
            {
                home = EntityManager.GetComponentData<ZoneData>(spawnable.m_ZonePrefab).m_ZoneType;
                if (!home.Equals(ZoneType.None) && !EntityManager.IsComponentEnabled<PrefabData>(spawnable.m_ZonePrefab))
                {
                    home = ZoneType.None;
                }
            }

            List<ushort> zones = m_AllowList.GetAllowingZones(prefab);
            var lot = EntityManager.GetComponentData<BuildingData>(prefab).m_LotSize;
            var transform = EntityManager.GetComponentData<Transform>(building);
            return ZoneLotTest.LotIsAllowed(EntityManager, zoneTree, transform, lot, home, zones);
        }

        /// <summary>Grown (spawnable, non-signature) buildings whose bounds meet an updated zone area.</summary>
        private struct BuildingFinder : INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>, IUnsafeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
        {
            public Bounds2 m_Bounds;
            public EntityManager m_EntityManager;
            public HashSet<Entity> m_Results;

            public bool Intersect(QuadTreeBoundsXZ bounds)
            {
                return MathUtils.Intersect(bounds.m_Bounds.xz, m_Bounds);
            }

            public void Iterate(QuadTreeBoundsXZ bounds, Entity entity)
            {
                if (!MathUtils.Intersect(bounds.m_Bounds.xz, m_Bounds) || !m_EntityManager.HasComponent<Building>(entity) || !m_EntityManager.HasComponent<PrefabRef>(entity))
                {
                    return;
                }

                Entity prefab = m_EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                if (m_EntityManager.HasComponent<SpawnableBuildingData>(prefab) && !m_EntityManager.HasComponent<SignatureBuildingData>(prefab) &&
                    m_EntityManager.HasComponent<BuildingData>(prefab) && m_EntityManager.HasComponent<Transform>(entity))
                {
                    m_Results.Add(entity);
                }
            }
        }
    }
}
