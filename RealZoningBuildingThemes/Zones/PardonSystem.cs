using System.Collections.Generic;
using Colossal.Collections;
using Colossal.Mathematics;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Notifications;
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
    /// The pardon pass, now a safety net: <see cref="RzbtZoneCheckSystem"/> stops our buildings
    /// being condemned in the first place, so this should find nothing (its log says "kept 0").
    /// Original purpose: The game's zone check (or Plop the Growables' replacement) condemns a
    /// grown building whose lot cells are not of its home zone, which is always true for a
    /// building grown in one of our zones. The condemnation is applied when ModificationEndBarrier
    /// plays back; this runs right after that barrier, in the same frame, so no simulation system
    /// (move-outs, demolition) ever sees it, and once more in GameSimulation just before the
    /// game's CondemnedBuildingSystem as a safety net. It lifts the condemnation when every cell
    /// under the lot is one of our zones that allows the building, and the lot still faces a
    /// road, as the game requires.
    ///
    /// A building that is not allowed (removed from the zone, or the cells rezoned to something
    /// else) stays condemned, so the game demolishes it as usual. Plop the Growables' plopped
    /// buildings are never condemned by its zone check, so they never reach this pass.
    /// </summary>
    public partial class PardonSystem : GameSystemBase
    {
        private EntityQuery m_CondemnedQuery;
        private EntityQuery m_BuildingConfigQuery;
        private AllowListSystem m_AllowList;
        private Game.Zones.SearchSystem m_ZoneSearch;
        private IconCommandSystem m_IconCommandSystem;

        // Distinct buildings per log window (a building can be re-condemned many times while
        // neighbours grow, because every new building makes the zone check re-test its area).
        private readonly HashSet<Entity> m_Pardoned = new HashSet<Entity>();
        private readonly HashSet<Entity> m_Kept = new HashSet<Entity>();
        private int m_PardonEvents;
        private System.DateTime m_LastLog = System.DateTime.MinValue;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_CondemnedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Building>(),
                    ComponentType.ReadOnly<Condemned>(),
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
            RequireForUpdate(m_CondemnedQuery);
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
                Perf.Stop("Pardon pass", perfStart);
            }
        }

        // TEMPORARY (perf-check): the original OnUpdate, timed by the wrapper above.
        private void OnUpdateTimed()
        {
            var entities = m_CondemnedQuery.ToEntityArray(Allocator.Temp);
            try
            {
                NativeQuadTree<Entity, Bounds2> tree = default;
                bool haveTree = false;
                IconCommandBuffer icons = default;
                bool haveIcons = false;
                Entity condemnedIcon = Entity.Null;

                for (int i = 0; i < entities.Length; i++)
                {
                    Entity building = entities[i];
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(building).m_Prefab;
                    List<ushort> zones = m_AllowList.GetAllowingZones(prefab);
                    if (zones == null || !EntityManager.HasComponent<BuildingData>(prefab))
                    {
                        continue; // not on any of our allow-lists: leave it to the game
                    }

                    if (!haveTree)
                    {
                        tree = m_ZoneSearch.GetSearchTree(true, out JobHandle deps);
                        deps.Complete();
                        haveTree = true;
                    }

                    var lot = EntityManager.GetComponentData<BuildingData>(prefab).m_LotSize;
                    var transform = EntityManager.GetComponentData<Transform>(building);
                    if (!ZoneLotTest.LotIsAllowed(EntityManager, tree, transform, lot, ZoneType.None, zones))
                    {
                        m_Kept.Add(building);
                        continue;
                    }

                    if (!haveIcons)
                    {
                        icons = m_IconCommandSystem.CreateCommandBuffer();
                        condemnedIcon = m_BuildingConfigQuery.GetSingleton<BuildingConfigurationData>().m_CondemnedNotification;
                        haveIcons = true;
                    }

                    EntityManager.RemoveComponent<Condemned>(building);
                    icons.Remove(building, condemnedIcon);
                    m_Pardoned.Add(building);
                    m_PardonEvents++;
                }

                if (haveTree)
                {
                    m_ZoneSearch.AddSearchTreeReader(default(JobHandle));
                }
            }
            finally
            {
                entities.Dispose();
            }

            // Log a summary now and then (about every 30 seconds of real time).
            if ((m_Pardoned.Count > 0 || m_Kept.Count > 0) && (System.DateTime.UtcNow - m_LastLog).TotalSeconds >= 30)
            {
                Mod.log.Info($"Pardon: kept {m_Pardoned.Count} allowed buildings ({m_PardonEvents} condemnations lifted); left {m_Kept.Count} condemned (cells not in a zone that allows them).");
                m_LastLog = System.DateTime.UtcNow;
                m_Pardoned.Clear();
                m_Kept.Clear();
                m_PardonEvents = 0;
            }
        }
    }
}
