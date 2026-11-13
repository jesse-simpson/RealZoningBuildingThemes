using System;
using System.Reflection;
using Unity.Entities;

namespace RealZoningBuildingThemes.Zones
{
    /// <summary>
    /// Placemaker's Outgrow can reshape grown buildings (row houses that join and bend to their
    /// land). It does so by swapping the building to a copy of its design, a new building prefab
    /// named "Placemaker Shape ...", with the same home zone. Our allow-lists name designs, not
    /// copies, so without this a reshaped building looked like one the zone does not allow, and
    /// was condemned. Here each copy is traced back to the design it was made from, through
    /// Placemaker's own ShapeSystem.BaseOf, so it is allowed wherever that design is. Copies are
    /// never grown or chosen for level-ups as designs of their own, and a reshaped building's own
    /// level-ups are left to the game and Placemaker.
    ///
    /// Found by name at run time, so the mod neither needs nor references Placemaker; without it
    /// every prefab is its own base.
    /// </summary>
    public static class PlacemakerCompat
    {
        private const string kTypeName = "Placemaker.Game.Shapes.ShapeSystem";

        private static bool s_TypeSearched;
        private static Type s_Type;
        private static MethodInfo s_BaseOfMethod;
        private static Func<Entity, Entity> s_BaseOf;
        private static bool s_Logged;
        private static bool s_LoggedOdd;

        /// <summary>The design a Placemaker shape copy was made from; the prefab itself otherwise.</summary>
        public static Entity BaseOf(World world, Entity prefab)
        {
            Func<Entity, Entity> baseOf = Resolve(world);
            if (baseOf == null)
            {
                return prefab;
            }

            Entity result;
            try
            {
                result = baseOf(prefab);
            }
            catch (Exception)
            {
                return prefab;
            }

            if (result == Entity.Null || result == prefab)
            {
                return prefab;
            }

            // Placemaker is in beta and may change: only trust an answer that makes sense, a
            // growable design with the copy's own game zone and lot size. Anything else is ignored,
            // and the prefab is treated as itself (as in versions before this compatibility).
            if (!LooksLikeBase(world.EntityManager, prefab, result))
            {
                if (!s_LoggedOdd)
                {
                    s_LoggedOdd = true;
                    Mod.log.Warn("PlacemakerCompat: Placemaker named a design for one of its copies that does not match it (game zone or lot size); such answers are ignored.");
                }

                return prefab;
            }

            return result;
        }

        private static bool LooksLikeBase(EntityManager em, Entity copy, Entity design)
        {
            if (!em.Exists(design) ||
                !em.HasComponent<Game.Prefabs.BuildingData>(copy) || !em.HasComponent<Game.Prefabs.BuildingData>(design) ||
                !em.HasComponent<Game.Prefabs.SpawnableBuildingData>(copy) || !em.HasComponent<Game.Prefabs.SpawnableBuildingData>(design))
            {
                return false;
            }

            if (!em.GetComponentData<Game.Prefabs.BuildingData>(copy).m_LotSize.Equals(em.GetComponentData<Game.Prefabs.BuildingData>(design).m_LotSize))
            {
                return false;
            }

            return em.GetComponentData<Game.Prefabs.SpawnableBuildingData>(copy).m_ZonePrefab ==
                em.GetComponentData<Game.Prefabs.SpawnableBuildingData>(design).m_ZonePrefab;
        }

        /// <summary>True when Placemaker's shapes are loaded (its system exists in this world).</summary>
        public static bool Available(World world)
        {
            return Resolve(world) != null;
        }

        private static Func<Entity, Entity> Resolve(World world)
        {
            if (s_BaseOf != null)
            {
                return s_BaseOf;
            }

            if (!s_TypeSearched)
            {
                s_TypeSearched = true;
                try
                {
                    foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (assembly.GetName().Name.IndexOf("Placemaker", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        Type type = assembly.GetType(kTypeName, false);
                        if (type != null)
                        {
                            s_Type = type;
                            s_BaseOfMethod = type.GetMethod("BaseOf", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Entity) }, null);
                            break;
                        }
                    }
                }
                catch (Exception e)
                {
                    Mod.log.Warn($"PlacemakerCompat: could not look for Placemaker ({e.Message}); its reshaped buildings may be treated as designs the zone does not allow.");
                }

                if (s_Type == null || s_BaseOfMethod == null || s_BaseOfMethod.ReturnType != typeof(Entity))
                {
                    s_Type = null;
                    return null;
                }
            }

            if (s_Type == null || world == null)
            {
                return null;
            }

            // The system may be created after us: look for it each time until it exists.
            try
            {
                if (world.GetExistingSystemManaged(s_Type) is object system)
                {
                    s_BaseOf = (Func<Entity, Entity>)Delegate.CreateDelegate(typeof(Func<Entity, Entity>), system, s_BaseOfMethod);
                    if (!s_Logged)
                    {
                        s_Logged = true;
                        Mod.log.Info("PlacemakerCompat: found Placemaker's shapes; buildings it reshapes count as the design they were made from.");
                    }
                }
            }
            catch (Exception e)
            {
                s_Type = null;
                Mod.log.Warn($"PlacemakerCompat: could not use Placemaker's shapes ({e.Message}); its reshaped buildings may be treated as designs the zone does not allow.");
            }

            return s_BaseOf;
        }
    }
}
