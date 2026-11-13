using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game;
using Game.Prefabs;
using Game.SceneFlow;
using Unity.Entities;
using UnityEngine;

namespace RealZoningBuildingThemes.Zones
{
    /// <summary>
    /// Creates the "Real Zoning" tab of the zoning toolbar and the mod's zone prefabs.
    ///
    /// How the game indexes zones: PrefabSystem.AddPrefab creates the zone entity with Created,
    /// and ZoneSystem gives every Created zone whose prefab has an area type a ZoneType index on
    /// its next pass. Creating the zones in Mod.OnLoad means they are indexed together with the
    /// game's own zones, before any city loads. The approach follows Custom Zones by dvovo and
    /// ZoningByLaw (both MIT licensed); this is our own implementation.
    ///
    /// One zone per entry in the zone library (zones.json), named "RZBT_" + its id, with the
    /// library's colour (default: the icon's game zone colour) and the icon of its base zone or a
    /// chosen game zone.
    ///
    /// Zones made in the panel while a game runs are created the same way (AddPrefab is indexed by
    /// the next ZoneSystem pass), then listed in our toolbar tab and announced to the toolbar with
    /// an Unlock event, as Custom Zones by dvovo (MIT) does. Zone names go to the game's
    /// localization through a memory source, so renames show at once.
    /// </summary>
    public partial class ZoneSetupSystem : GameSystemBase
    {
        public const string Prefix = "RZBT_";
        public const string CategoryName = Prefix + "Category";
        private const string BaseCategoryName = "ZonesOffice";

        private PrefabSystem m_PrefabSystem;
        private UIAssetCategoryPrefab m_Category;
        private bool m_Installed;
        private List<ZonePrefab> m_GameZones;
        private int m_NextPriority = 0;

        private readonly List<ZoneDefinition> m_PendingCreate = new List<ZoneDefinition>();
        private readonly List<RzbtZonePrefab> m_PendingToolbar = new List<RzbtZonePrefab>();
        private int m_ToolbarCountdown = -1;
        private Entity m_UnlockEvent = Entity.Null;
        private EntityArchetype m_UnlockArchetype;

        /// <summary>The zones this mod created, by prefab name.</summary>
        public Dictionary<string, RzbtZonePrefab> Zones { get; } = new Dictionary<string, RzbtZonePrefab>();

        /// <summary>The game zone each of our zones was based on, by our prefab name (rezoning falls back to it).</summary>
        public Dictionary<string, ZonePrefab> BaseOf { get; } = new Dictionary<string, ZonePrefab>();

        /// <summary>Puts a restored zone back in the toolbar tab.</summary>
        public void RequestList(string prefabName)
        {
            if (Zones.TryGetValue(prefabName, out RzbtZonePrefab zone) && !m_PendingToolbar.Contains(zone))
            {
                m_PendingToolbar.Add(zone);
                m_ToolbarCountdown = Math.Max(m_ToolbarCountdown, 1);
            }
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_UnlockArchetype = EntityManager.CreateArchetype(ComponentType.ReadWrite<Game.Common.Event>(), ComponentType.ReadWrite<Unlock>());
        }

        /// <summary>Queues a zone made in the panel; it is created on the next PrefabUpdate pass.</summary>
        public void RequestCreate(ZoneDefinition definition)
        {
            m_PendingCreate.Add(definition);
        }

        protected override void OnUpdate()
        {
            // The unlock event only has to live one frame.
            if (m_UnlockEvent != Entity.Null)
            {
                if (EntityManager.Exists(m_UnlockEvent))
                {
                    EntityManager.DestroyEntity(m_UnlockEvent);
                }

                m_UnlockEvent = Entity.Null;
            }

            while (m_PendingCreate.Count > 0)
            {
                ZoneDefinition definition = m_PendingCreate[0];
                m_PendingCreate.RemoveAt(0);
                try
                {
                    RzbtZonePrefab zone = CreateFromDefinition(definition);
                    if (zone != null)
                    {
                        m_PendingToolbar.Add(zone);
                        m_ToolbarCountdown = 2; // let the game initialise the new UIObject first
                    }

                    PushLocale();
                }
                catch (Exception e)
                {
                    Mod.log.Error(e, $"ZoneSetup: could not create zone '{definition?.Id}'.");
                }
            }

            UnlistRetired();
            ReclaimZones();

            if (m_ToolbarCountdown > 0)
            {
                m_ToolbarCountdown--;
            }
            else if (m_ToolbarCountdown == 0)
            {
                m_ToolbarCountdown = -1;
                RefreshToolbar();
            }
        }

        /// <summary>
        /// Keeps retired zones out of the toolbar tab (the game lists every zone of the tab when it
        /// initialises them, and retiring happens during a game), and removes their toolbar data
        /// (UIObjectData) so other mods' zone pickers skip them too. Restoring adds it back
        /// (RefreshToolbar). Cheap: a few zones, one buffer.
        /// </summary>
        private void UnlistRetired()
        {
            if (m_Category == null || !m_PrefabSystem.TryGetEntity(m_Category, out Entity categoryEntity) || !EntityManager.HasBuffer<UIGroupElement>(categoryEntity))
            {
                return;
            }

            bool removed = false;
            foreach (ZoneDefinition definition in ZoneLibrary.Current.Zones)
            {
                if (!definition.Retired || !Zones.TryGetValue(definition.PrefabName, out RzbtZonePrefab zone) || !m_PrefabSystem.TryGetEntity(zone, out Entity zoneEntity))
                {
                    continue;
                }

                // Out of our tab, and out of any tab another mod (Zone Organizer) moved it to.
                bool hasUiData = EntityManager.HasComponent<UIObjectData>(zoneEntity);
                Entity otherGroup = hasUiData ? EntityManager.GetComponentData<UIObjectData>(zoneEntity).m_Group : Entity.Null;
                foreach (Entity group in new[] { categoryEntity, otherGroup })
                {
                    if (group == Entity.Null || !EntityManager.Exists(group) || !EntityManager.HasBuffer<UIGroupElement>(group))
                    {
                        continue;
                    }

                    var elements = EntityManager.GetBuffer<UIGroupElement>(group);
                    for (int i = elements.Length - 1; i >= 0; i--)
                    {
                        if (elements[i].m_Prefab == zoneEntity)
                        {
                            elements.RemoveAt(i);
                            removed = true;
                            Mod.log.Info($"ZoneSetup: zone '{zone.name}' is retired; taken out of the toolbar.");
                        }
                    }
                }

                // Then drop its toolbar data altogether, so zone pickers of other mods that list
                // every zone with toolbar data (Platter's parcel pre-zoning) no longer offer it. The
                // zone itself stays registered, so saves that use it still load and keep growing.
                // Only the milestone screen also reads this data, and it skips zones without it.
                if (hasUiData)
                {
                    EntityManager.RemoveComponent<UIObjectData>(zoneEntity);
                    removed = true;
                }
            }

            // Zones no longer in the file at all (deleted this session) leave the tab too.
            foreach (var pair in Zones)
            {
                if (ZoneLibrary.FindByPrefabName(pair.Key) != null || !m_PrefabSystem.TryGetEntity(pair.Value, out Entity zoneEntity))
                {
                    continue;
                }

                var elements = EntityManager.GetBuffer<UIGroupElement>(categoryEntity);
                for (int i = elements.Length - 1; i >= 0; i--)
                {
                    if (elements[i].m_Prefab == zoneEntity)
                    {
                        elements.RemoveAt(i);
                        removed = true;
                    }
                }
            }

            if (removed && m_UnlockEvent == Entity.Null)
            {
                m_UnlockEvent = EntityManager.CreateEntity(m_UnlockArchetype);
                EntityManager.SetComponentData(m_UnlockEvent, new Unlock(categoryEntity));
            }
        }

        private bool m_LoggedReclaim;

        /// <summary>
        /// Keeps our zones in our own toolbar tab. Zone Organizer, when the city loads, moves every
        /// zone into its own density tabs (it rewrites each zone's UI group and priority), which
        /// left our tab empty, so the game hid it. Any of our active zones found in another group is
        /// moved back, with its own priority. Cheap: a few zones, checked each frame.
        /// </summary>
        private void ReclaimZones()
        {
            if (m_Category == null || !m_PrefabSystem.TryGetEntity(m_Category, out Entity categoryEntity) || !EntityManager.HasBuffer<UIGroupElement>(categoryEntity))
            {
                return;
            }

            bool moved = false;
            foreach (ZoneDefinition definition in ZoneLibrary.Current.Zones)
            {
                if (definition.Retired || !Zones.TryGetValue(definition.PrefabName, out RzbtZonePrefab zone) ||
                    !m_PrefabSystem.TryGetEntity(zone, out Entity zoneEntity) || !EntityManager.HasComponent<UIObjectData>(zoneEntity))
                {
                    continue;
                }

                var data = EntityManager.GetComponentData<UIObjectData>(zoneEntity);
                if (data.m_Group == categoryEntity || data.m_Group == Entity.Null)
                {
                    continue; // ours, or not set up by the game yet
                }

                Entity other = data.m_Group;
                if (EntityManager.Exists(other) && EntityManager.HasBuffer<UIGroupElement>(other))
                {
                    var otherElements = EntityManager.GetBuffer<UIGroupElement>(other);
                    for (int i = otherElements.Length - 1; i >= 0; i--)
                    {
                        if (otherElements[i].m_Prefab == zoneEntity)
                        {
                            otherElements.RemoveAt(i);
                        }
                    }
                }

                data.m_Group = categoryEntity;
                data.m_Priority = zone.GetComponent<UIObject>()?.m_Priority ?? data.m_Priority;
                EntityManager.SetComponentData(zoneEntity, data);

                var elements = EntityManager.GetBuffer<UIGroupElement>(categoryEntity);
                bool listed = false;
                for (int i = 0; i < elements.Length && !listed; i++)
                {
                    listed = elements[i].m_Prefab == zoneEntity;
                }

                if (!listed)
                {
                    m_Category.AddElement(EntityManager, zoneEntity);
                }

                moved = true;
                if (!m_LoggedReclaim)
                {
                    Mod.log.Info($"ZoneSetup: another mod moved zone '{zone.name}' to '{m_PrefabSystem.GetPrefabName(other)}' (Zone Organizer does this); moved it back to our tab. Further moves are fixed silently.");
                }
            }

            if (moved)
            {
                m_LoggedReclaim = true;
                if (m_UnlockEvent == Entity.Null)
                {
                    m_UnlockEvent = EntityManager.CreateEntity(m_UnlockArchetype);
                    EntityManager.SetComponentData(m_UnlockEvent, new Unlock(categoryEntity));
                }
            }
        }

        /// <summary>Lists new zones in our tab and sends the toolbar an Unlock event so it redraws.</summary>
        private void RefreshToolbar()
        {
            if (m_Category == null || !m_PrefabSystem.TryGetEntity(m_Category, out Entity categoryEntity))
            {
                return;
            }

            foreach (RzbtZonePrefab zone in m_PendingToolbar)
            {
                if (!m_PrefabSystem.TryGetEntity(zone, out Entity zoneEntity))
                {
                    continue;
                }

                // A restored zone lost its toolbar data when it was retired; give it back.
                if (!EntityManager.HasComponent<UIObjectData>(zoneEntity))
                {
                    EntityManager.AddComponentData(zoneEntity, new UIObjectData
                    {
                        m_Group = categoryEntity,
                        m_Priority = zone.GetComponent<UIObject>()?.m_Priority ?? 0,
                    });
                }

                bool listed = false;
                if (EntityManager.HasBuffer<UIGroupElement>(categoryEntity))
                {
                    var elements = EntityManager.GetBuffer<UIGroupElement>(categoryEntity, true);
                    for (int i = 0; i < elements.Length && !listed; i++)
                    {
                        listed = elements[i].m_Prefab == zoneEntity;
                    }
                }

                if (!listed)
                {
                    m_Category.AddElement(EntityManager, zoneEntity);
                }

                Mod.log.Info($"ZoneSetup: zone '{zone.name}' added to the toolbar tab{(listed ? string.Empty : " (listed by the mod)")}.");
            }

            m_PendingToolbar.Clear();
            m_UnlockEvent = EntityManager.CreateEntity(m_UnlockArchetype);
            EntityManager.SetComponentData(m_UnlockEvent, new Unlock(categoryEntity));
        }

        /// <summary>Zone names and descriptions for the game's localization; call after names change.</summary>
        public void PushLocale()
        {
            try
            {
                var entries = new Dictionary<string, string>();
                foreach (ZoneDefinition zone in ZoneLibrary.Current.Zones)
                {
                    entries[$"Assets.NAME[{zone.PrefabName}]"] = zone.Name;
                    entries[$"Assets.DESCRIPTION[{zone.PrefabName}]"] = ModLocalization.Get("Zone.Description", "Real Zoning & Building Themes zone. Grows only the buildings its rules allow.");
                }

                var manager = GameManager.instance.localizationManager;
                manager.AddSource(manager.activeLocaleId, new Colossal.Localization.MemorySource(entries));
            }
            catch (Exception e)
            {
                Mod.log.Warn($"ZoneSetup: could not update zone names ({e.Message}); they update after a restart.");
            }
        }

        /// <summary>Called once from Mod.OnLoad.</summary>
        public void InstallAtLoad()
        {
            if (m_Installed)
            {
                return;
            }

            m_Installed = true;
            try
            {
                List<PrefabBase> prefabs = AllPrefabs();
                if (prefabs == null)
                {
                    Mod.log.Error("ZoneSetup: could not read the game's prefab list.");
                    return;
                }

                m_Category = CreateCategory(prefabs);
                m_GameZones = prefabs.OfType<ZonePrefab>().Where(z => !(z is RzbtZonePrefab)).ToList();
                foreach (ZoneDefinition definition in ZoneLibrary.Current.Zones)
                {
                    CreateFromDefinition(definition);
                }
            }
            catch (Exception e)
            {
                Mod.log.Error(e, "ZoneSetup: InstallAtLoad failed");
            }
        }

        private RzbtZonePrefab CreateFromDefinition(ZoneDefinition definition)
        {
            if (m_GameZones == null || Zones.ContainsKey(definition.PrefabName))
            {
                return null;
            }

            string baseName = string.IsNullOrEmpty(definition.Base) ? ZoneLibrary.DefaultBaseZone(definition) : definition.Base;
            var baseZone = m_GameZones.FirstOrDefault(z => z.name == baseName);
            if (baseZone == null)
            {
                Mod.log.Error($"ZoneSetup: zone '{definition.Id}': base zone '{baseName}' not found; zone skipped.");
                return null;
            }

            var iconZone = string.IsNullOrEmpty(definition.Icon) ? baseZone : m_GameZones.FirstOrDefault(z => z.name == definition.Icon) ?? baseZone;

            // No colour set: the zone keeps the colour of its icon's game zone (decision 2026-11-11).
            bool custom = ZoneLibrary.TryParseColor(definition.Color, out Color fill);
            // Toolbar order: by the base zone's density, lowest first (decision 2026-11-11), then by
            // creation order within the same base kind.
            int priority = DensityRank(baseZone) * 1000 + m_NextPriority++;
            BaseOf[definition.PrefabName] = baseZone;
            return CreateZone(definition.PrefabName, baseZone, iconZone, custom ? fill : iconZone.m_Color, custom ? (Color?)null : iconZone.m_Edge, priority);
        }

        /// <summary>
        /// Order of game zone kinds, lowest density first, as the game's own zoning tabs run:
        /// residential low, row, medium, high, mixed, low rent; then commercial low and high,
        /// office low and high, industry.
        /// </summary>
        public static int DensityRank(ZonePrefab zone)
        {
            string n = zone.name ?? string.Empty;
            switch (zone.m_AreaType)
            {
                case Game.Zones.AreaType.Residential:
                    if (n.Contains("LowRent") || n.Contains("Low Rent")) return 5;
                    if (n.Contains("Mixed")) return 4;
                    if (n.Contains("Row") || n.Contains("Terraced")) return 1;
                    if (n.Contains("High")) return 3;
                    if (n.Contains("Medium")) return 2;
                    return 0;
                case Game.Zones.AreaType.Commercial:
                    return n.Contains("High") ? 7 : 6;
                case Game.Zones.AreaType.Industrial:
                    if (zone.m_Office) return n.Contains("High") ? 9 : 8;
                    return 10;
                default:
                    return 11;
            }
        }

        /// <summary>Toolbar priority of one of our zones (its sort key); int.MaxValue when it is not in the game yet.</summary>
        public int OrderOf(string prefabName)
        {
            return Zones.TryGetValue(prefabName, out RzbtZonePrefab zone) && zone.GetComponent<UIObject>() is UIObject ui ? ui.m_Priority : int.MaxValue;
        }

        private List<PrefabBase> AllPrefabs()
        {
            // PrefabSystem keeps its list private; it is complete by the time Mod.OnLoad runs.
            var field = typeof(PrefabSystem).GetField("m_Prefabs", BindingFlags.NonPublic | BindingFlags.Instance);
            return field?.GetValue(m_PrefabSystem) as List<PrefabBase>;
        }

        private UIAssetCategoryPrefab CreateCategory(List<PrefabBase> prefabs)
        {
            var baseCategory = prefabs.OfType<UIAssetCategoryPrefab>().FirstOrDefault(c => c.name == BaseCategoryName)
                ?? prefabs.OfType<UIAssetCategoryPrefab>().FirstOrDefault(c => c.name != null && c.name.StartsWith("Zones", StringComparison.Ordinal));
            if (baseCategory == null)
            {
                Mod.log.Error("ZoneSetup: no game zoning category found; our zones will not appear in the toolbar.");
                return null;
            }

            var category = ScriptableObject.CreateInstance<UIAssetCategoryPrefab>();
            category.name = CategoryName;
            category.m_Menu = baseCategory.m_Menu;
            category.active = true;
            category.isDirty = true;
            category.components.AddRange(baseCategory.components.Where(c => !(c is UIObject)));

            var baseUi = baseCategory.GetComponent<UIObject>();
            var ui = ScriptableObject.CreateInstance<UIObject>();
            ui.name = CategoryName;
            ui.active = true;
            ui.m_Priority = 100; // after the game's zoning tabs
            ui.m_Icon = baseUi != null ? baseUi.m_Icon : null;
            ui.m_Icon = TabIcon.Create(ui.m_Icon) ?? ui.m_Icon; // light red copy (decision 2026-11-11)
            ui.m_Group = baseUi != null ? baseUi.m_Group : null;
            ui.m_IsDebugObject = false;
            category.AddComponentFrom(ui);

            if (!m_PrefabSystem.AddPrefab(category))
            {
                Mod.log.Error("ZoneSetup: AddPrefab failed for the toolbar category.");
                return null;
            }

            Mod.log.Info($"ZoneSetup: created toolbar tab '{CategoryName}' next to '{baseCategory.name}'.");
            return category;
        }

        private RzbtZonePrefab CreateZone(string zoneName, ZonePrefab baseZone, ZonePrefab iconZone, Color color, Color? edge, int priority)
        {
            var zone = ScriptableObject.CreateInstance<RzbtZonePrefab>();
            zone.name = zoneName;
            zone.active = true;
            zone.isDirty = true;

            // Same zone properties, pollution, service use and so on as the base zone, without its
            // theme (custom zones are not tied to one region), its milestone, or its toolbar entry.
            zone.components.AddRange(baseZone.components.Where(c => !(c is UIObject) && !(c is ThemeObject) && !(c is Unlockable)));

            // ZoneSystem only indexes zones with an area type.
            zone.m_AreaType = baseZone.m_AreaType;
            zone.m_Office = baseZone.m_Office;
            zone.m_Color = color;
            zone.m_Edge = edge ?? Color.Lerp(color, Color.white, 0.25f);

            var baseUi = iconZone.GetComponent<UIObject>();
            var ui = ScriptableObject.CreateInstance<UIObject>();
            ui.name = zoneName;
            ui.active = m_Category != null;
            ui.m_Group = m_Category;
            ui.m_Priority = priority;
            ui.m_Icon = baseUi != null ? baseUi.m_Icon : null;
            ui.m_IsDebugObject = false;
            zone.AddComponentFrom(ui);

            if (!m_PrefabSystem.AddPrefab(zone))
            {
                Mod.log.Error($"ZoneSetup: AddPrefab failed for zone '{zoneName}'.");
                return null;
            }

            Zones[zoneName] = zone;
            Mod.log.Info($"ZoneSetup: created zone '{zoneName}' (area {zone.m_AreaType}, office {zone.m_Office}, based on '{baseZone.name}').");
            return zone;
        }
    }
}
