using Game;
using Game.Common;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;

namespace RealZoningBuildingThemes.Zones
{
    /// <summary>
    /// Finishes each of our zone entities right after the game's ZoneSystem has indexed it.
    /// </summary>
    public partial class ZoneInitSystem : GameSystemBase
    {
        private EntityQuery m_CreatedQuery;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_CreatedQuery = GetEntityQuery(ComponentType.ReadOnly<Created>(), ComponentType.ReadOnly<RzbtZone>());
            RequireForUpdate(m_CreatedQuery);
        }

        protected override void OnUpdate()
        {
            var entities = m_CreatedQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];

                    if (EntityManager.HasComponent<ZoneData>(entity))
                    {
                        // Allow corner lots and narrow lots, so any building chosen for the zone can fit.
                        var zoneData = EntityManager.GetComponentData<ZoneData>(entity);
                        zoneData.m_ZoneFlags |= ZoneFlags.SupportLeftCorner | ZoneFlags.SupportRightCorner | ZoneFlags.SupportNarrow;
                        EntityManager.SetComponentData(entity, zoneData);
                        Mod.log.Info($"ZoneInit: '{World.GetOrCreateSystemManaged<PrefabSystem>().GetPrefabName(entity)}' has zone type index {zoneData.m_ZoneType.m_Index}.");
                    }

                    // The game only writes a prefab into a save if it carries Locked (enabled or not)
                    // or is referenced by saved entities. Zone cells store a zone number, and loading
                    // maps saved numbers back through the saved zone prefabs, so without this the
                    // painted cells would be lost on reload. Disabled means unlocked.
                    if (!EntityManager.HasComponent<Locked>(entity))
                    {
                        EntityManager.AddComponent<Locked>(entity);
                    }

                    EntityManager.SetComponentEnabled<Locked>(entity, false);
                }
            }
            finally
            {
                entities.Dispose();
            }
        }
    }
}
