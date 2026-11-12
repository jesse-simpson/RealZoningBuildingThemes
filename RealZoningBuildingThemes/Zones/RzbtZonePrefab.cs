using System.Collections.Generic;
using Game.Prefabs;
using Unity.Entities;

namespace RealZoningBuildingThemes.Zones
{
    /// <summary>Tag on the entity of every zone this mod creates.</summary>
    public struct RzbtZone : IComponentData
    {
    }

    /// <summary>
    /// The building prefabs allowed in one of our zones (every level). Kept on the zone entity
    /// by AllowListSystem and read by the spawner's Burst job.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct RzbtAllowedBuilding : IBufferElementData
    {
        public Entity m_Building;
    }

    /// <summary>
    /// A game ZonePrefab whose entity also carries <see cref="RzbtZone"/>, so our systems can
    /// find our zones. Created with ScriptableObject.CreateInstance so it goes through the
    /// game's full prefab initialisation.
    /// </summary>
    public class RzbtZonePrefab : ZonePrefab
    {
        public override void GetPrefabComponents(HashSet<ComponentType> components)
        {
            base.GetPrefabComponents(components);
            components.Add(ComponentType.ReadWrite<RzbtZone>());
            components.Add(ComponentType.ReadWrite<RzbtAllowedBuilding>());
        }
    }
}
