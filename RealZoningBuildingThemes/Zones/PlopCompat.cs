using System;
using System.Reflection;
using Unity.Entities;

namespace RealZoningBuildingThemes.Zones
{
    /// <summary>
    /// Plop the Growables marks buildings the player placed by hand with its own PloppedBuilding
    /// component. They are the player's choice, so zone rules (removal, level-up swaps) leave them
    /// alone. The component is found by name, so the mod neither needs nor references Plop the
    /// Growables; without it every check answers "not plopped".
    /// </summary>
    public static class PlopCompat
    {
        private static bool s_Resolved;
        private static ComponentType s_Plopped;
        private static bool s_HavePlopped;

        public static bool IsPlopped(EntityManager em, Entity building)
        {
            Resolve();
            return s_HavePlopped && em.HasComponent(building, s_Plopped);
        }

        private static void Resolve()
        {
            if (s_Resolved)
            {
                return;
            }

            s_Resolved = true;
            try
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.GetName().Name.IndexOf("Plop", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    Type[] types;
                    try
                    {
                        types = assembly.GetTypes();
                    }
                    catch (ReflectionTypeLoadException e)
                    {
                        types = e.Types;
                    }

                    foreach (Type type in types)
                    {
                        if (type != null && type.Name == "PloppedBuilding" && typeof(IComponentData).IsAssignableFrom(type))
                        {
                            s_Plopped = ComponentType.ReadOnly(type);
                            s_HavePlopped = true;
                            Mod.log.Info($"PlopCompat: found {type.FullName} in {assembly.GetName().Name}; plopped buildings are exempt from zone rules.");
                            return;
                        }
                    }
                }

                Mod.log.Info("PlopCompat: Plop the Growables not loaded.");
            }
            catch (Exception e)
            {
                Mod.log.Warn($"PlopCompat: could not look for Plop the Growables ({e.Message}); plopped buildings are treated like grown ones.");
            }
        }
    }
}
