using System;
using System.Collections.Generic;
using System.Linq;
using Colossal.UI.Binding;
using Game;
using Game.Input;
using Game.Prefabs;
using Game.SceneFlow;
using Game.UI;
using Newtonsoft.Json;
using RealZoningBuildingThemes.Zones;
using UnityEngine;

namespace RealZoningBuildingThemes.Systems
{
    /// <summary>
    /// Connects the zone panel (the React UI module in UI\src) to the mod. Bindings live in the
    /// "rzbt" group and carry JSON strings, which keeps the C# side simple while the panel's shape
    /// is still changing.
    ///
    /// "zones": every zone in zones.json (its full definition) plus colour, toolbar icon and the
    /// counts the rules last produced. "buildings": the building index, sent once per index
    /// rebuild, so the panel can evaluate draft rules itself and show live counts while editing
    /// (the same rules as BuildingIndex.Matches). Triggers: "applyZone" (a zone definition as JSON;
    /// saves zones.json and re-applies rules, which runs the removal pass), "createZone",
    /// "duplicateZone", "retireZone", "restoreZone", "deleteZone", "missingThumbs"; value "togglePanel" (hotkey presses).
    /// </summary>
    public partial class ZonePanelUISystem : UISystemBase
    {
        public const string Group = "rzbt";

        private ValueBinding<string> m_Zones;
        private ValueBinding<string> m_Buildings;
        private ValueBinding<string> m_Notice;
        private RezoneSystem m_Rezone;
        private int m_SentNoticeVersion;
        private AllowListSystem m_AllowList;
        private ZoneSetupSystem m_ZoneSetup;
        private PrefabSystem m_PrefabSystem;
        private int m_SentRulesVersion = -1;
        private int m_SentLibraryRevision = -1;
        private int m_SentIndexVersion = -1;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_AllowList = World.GetOrCreateSystemManaged<AllowListSystem>();
            m_ZoneSetup = World.GetOrCreateSystemManaged<ZoneSetupSystem>();
            m_Rezone = World.GetOrCreateSystemManaged<RezoneSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            AddBinding(m_Zones = new ValueBinding<string>(Group, "zones", "[]"));
            AddBinding(m_Buildings = new ValueBinding<string>(Group, "buildings", "{\"thumb\":null,\"rows\":[]}"));
            AddBinding(new TriggerBinding<string>(Group, "applyZone", ApplyZone));
            AddBinding(new TriggerBinding<string, string>(Group, "createZone", CreateZone));
            AddBinding(new TriggerBinding<string>(Group, "missingThumbs", ReportMissingThumbs));
            AddBinding(m_Notice = new ValueBinding<string>(Group, "notice", string.Empty));
            AddBinding(new TriggerBinding<string>(Group, "duplicateZone", DuplicateZone));
            AddBinding(new TriggerBinding<string>(Group, "retireZone", RetireZone));
            AddBinding(new TriggerBinding<string>(Group, "restoreZone", RestoreZone));
            AddBinding(new TriggerBinding<string>(Group, "deleteZone", DeleteZone));
            AddBinding(m_TogglePanel = new ValueBinding<int>(Group, "togglePanel", 0));
        }

        /// <summary>A copy of the zone (rules, hand-picks, base), created in the game at once.</summary>
        private void DuplicateZone(string id)
        {
            try
            {
                ZoneDefinition copy = ZoneLibrary.DuplicateZone(id);
                if (copy != null)
                {
                    m_ZoneSetup.RequestCreate(copy);
                    AllowListSystem.RequestRebuild();
                }
            }
            catch (Exception e)
            {
                Mod.log.Error(e, $"ZonePanel: could not duplicate zone '{id}'.");
            }
        }

        /// <summary>
        /// Retire: rezone this city's cells of the zone to game zones, then take the zone out of the
        /// toolbar. The zone stays in the file (marked retired) and is still created at load, so
        /// other cities that use it keep working.
        /// </summary>
        private void RetireZone(string id)
        {
            ZoneDefinition zone = ZoneLibrary.Current.Zones.FirstOrDefault(z => z.Id == id);
            if (zone == null)
            {
                return;
            }

            m_Rezone.RequestRezone(new[] { zone.PrefabName }, $"Retired '{zone.Name}'", "Notice.Retired", new Dictionary<string, object> { ["name"] = zone.Name }, () =>
            {
                ZoneLibrary.SetRetired(id, true);
                AllowListSystem.RequestRebuild();
            });
        }

        private void RestoreZone(string id)
        {
            ZoneDefinition zone = ZoneLibrary.Current.Zones.FirstOrDefault(z => z.Id == id);
            if (zone != null && ZoneLibrary.SetRetired(id, false))
            {
                m_ZoneSetup.RequestList(zone.PrefabName);
                AllowListSystem.RequestRebuild();
            }
        }

        /// <summary>Deletes a retired zone: it leaves the panel for good but stays registered, so other cities that use it still load.</summary>
        private void DeleteZone(string id)
        {
            if (ZoneLibrary.DeleteZone(id))
            {
                AllowListSystem.RequestRebuild();
            }
        }

        /// <summary>Rezones every one of our zones in this city to game zones, so it loads safely without the mod (the Options page's button).</summary>
        public void PrepareUninstall()
        {
            var names = ZoneLibrary.Current.Zones.Select(z => z.PrefabName).ToList();
            m_Rezone.RequestRezone(names, "Prepared this city for uninstall", "Notice.Uninstall");
        }

        private readonly HashSet<string> m_ReportedThumbs = new HashSet<string>();

        /// <summary>The panel reports thumbnails that failed to load (a JSON list of prefab names); logged once each, with what the game returned.</summary>
        private void ReportMissingThumbs(string json)
        {
            try
            {
                var names = JsonConvert.DeserializeObject<List<string>>(json);
                var fresh = names.Where(n => m_ReportedThumbs.Add(n)).ToList();
                if (fresh.Count == 0)
                {
                    return;
                }

                var byName = new Dictionary<string, BuildingInfo>();
                foreach (var info in m_AllowList.Index)
                {
                    byName[info.Name] = info;
                }
                foreach (string name in fresh.Take(40))
                {
                    byName.TryGetValue(name, out BuildingInfo b);
                    string url = b != null ? SafeThumbnail(b.Entity) : null;
                    Mod.log.Info($"Thumbnails: no image for '{name}' ({b?.Origin ?? "?"}{(string.IsNullOrEmpty(b?.Source) ? string.Empty : ", " + b.Source)}); the game gives '{url ?? "nothing"}'.");
                }

                if (fresh.Count > 40)
                {
                    Mod.log.Info($"Thumbnails: {fresh.Count - 40} more without an image.");
                }
            }
            catch (Exception e)
            {
                Mod.log.Warn($"Thumbnails: could not read the panel's report ({e.Message}).");
            }
        }

        /// <summary>The game's thumbnail for a prefab; null when the prefab's icon data is broken (some custom assets throw here).</summary>
        private string SafeThumbnail(Unity.Entities.Entity entity)
        {
            try
            {
                return m_PrefabSystem.TryGetPrefab(entity, out PrefabBase prefab) ? ImageSystem.GetThumbnail(prefab) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private ValueBinding<int> m_TogglePanel;
        private ProxyAction m_ToggleAction;

        /// <summary>
        /// The panel's keyboard shortcut (Options page, Shift+Z by default). The panel's open state
        /// lives in the UI, so each press bumps a counter the toolbar button watches.
        /// </summary>
        private void CheckHotkey()
        {
            if (m_ToggleAction == null)
            {
                m_ToggleAction = Mod.Settings?.GetAction(Setting.kTogglePanelAction);
                if (m_ToggleAction == null)
                {
                    return;
                }
            }

            bool inGame = GameManager.instance != null && GameManager.instance.gameMode.IsGame();
            if (m_ToggleAction.shouldBeEnabled != inGame)
            {
                m_ToggleAction.shouldBeEnabled = inGame;
            }

            if (inGame && m_ToggleAction.WasPerformedThisFrame())
            {
                m_TogglePanel.Update(m_TogglePanel.value + 1);
            }
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();
            CheckHotkey();
            if (m_AllowList.IndexVersion != m_SentIndexVersion)
            {
                m_SentIndexVersion = m_AllowList.IndexVersion;
                m_Buildings.Update(BuildBuildings());
            }

            if (m_Rezone.NoticeVersion != m_SentNoticeVersion)
            {
                m_SentNoticeVersion = m_Rezone.NoticeVersion;
                m_Notice.Update(m_Rezone.Notice);
            }

            if (m_AllowList.RulesVersion != m_SentRulesVersion || ZoneLibrary.Revision != m_SentLibraryRevision)
            {
                m_SentRulesVersion = m_AllowList.RulesVersion;
                m_SentLibraryRevision = ZoneLibrary.Revision;
                m_Zones.Update(JsonConvert.SerializeObject(BuildZones()));
            }
        }

        private void ApplyZone(string json)
        {
            try
            {
                var zone = JsonConvert.DeserializeObject<ZoneDefinition>(json);
                if (ZoneLibrary.ReplaceZone(zone))
                {
                    AllowListSystem.RequestRebuild();
                    m_ZoneSetup.PushLocale(); // renames show at once
                }
                else
                {
                    Mod.log.Warn($"ZonePanel: Apply ignored; no zone with id '{zone?.Id}'.");
                }
            }
            catch (Exception e)
            {
                Mod.log.Error(e, "ZonePanel: could not apply zone changes.");
            }
        }

        /// <summary>
        /// A zone made in the panel: saved to zones.json and created in the running game (its
        /// toolbar button appears a few frames later), so it can be painted at once.
        /// </summary>
        private void CreateZone(string name, string use)
        {
            try
            {
                var definition = new ZoneDefinition { Name = string.IsNullOrWhiteSpace(name) ? "New zone" : name.Trim() };
                if (!string.IsNullOrEmpty(use) && ZoneLibrary.UseKeys.Contains(use))
                {
                    definition.Uses.Add(use);
                }

                definition = ZoneLibrary.AddZone(definition);
                m_ZoneSetup.RequestCreate(definition);
                AllowListSystem.RequestRebuild();
            }
            catch (Exception e)
            {
                Mod.log.Error(e, "ZonePanel: could not create the zone.");
            }
        }

        private List<ZoneSummary> BuildZones()
        {
            var rulesByPrefab = m_AllowList.AllZones.Where(r => r.PrefabName != null).ToDictionary(r => r.PrefabName);
            var list = new List<ZoneSummary>();
            foreach (ZoneDefinition d in ZoneLibrary.Current.Zones)
            {
                if (d.Deleted)
                {
                    continue; // kept registered for other cities, never shown
                }

                var summary = new ZoneSummary { Definition = d };

                // The zone prefab exists only if the zone was in the file when the game started.
                if (m_ZoneSetup.Zones.TryGetValue(d.PrefabName, out RzbtZonePrefab prefab))
                {
                    summary.Active = true;
                    summary.Color = "#" + ColorUtility.ToHtmlStringRGB(prefab.m_Color);
                    summary.Icon = prefab.GetComponent<UIObject>()?.m_Icon;
                    summary.BaseName = m_ZoneSetup.BaseOf.TryGetValue(d.PrefabName, out ZonePrefab baseZone) ? baseZone.name : null;
                }

                if (rulesByPrefab.TryGetValue(d.PrefabName, out AllowListSystem.ZoneRules rules))
                {
                    summary.Allowed = rules.Allowed.Count;
                    summary.LevelOne = rules.Allowed.Count(b => b.Level == 1);
                }

                list.Add(summary);
            }

            // Same order as the toolbar: by base zone density, lowest first.
            return list.OrderBy(z => m_ZoneSetup.OrderOf(z.Definition.PrefabName)).ToList();
        }

        /// <summary>
        /// The index as compact rows: [name, use, theme, packs, level, lotWidth, lotDepth, height,
        /// setback (null without model data; can be negative when the model overhangs the lot), parking (0/1), signature (0/1), thumbnail,
        /// zone type (Find It's, plus lowrent), origin (game/pack/custom), custom source, Paradox Mods id]. Thumbnails usually share one
        /// pattern around the prefab name; then "thumb" holds that pattern with {0} for the name and
        /// the per-row thumbnail is left empty.
        /// </summary>
        private string BuildBuildings()
        {
            var index = m_AllowList.Index;
            var thumbs = new string[index.Count];
            for (int i = 0; i < index.Count; i++)
            {
                thumbs[i] = SafeThumbnail(index[i].Entity);
            }

            string pattern = null;
            if (index.Count > 0 && thumbs[0] != null)
            {
                int at = thumbs[0].IndexOf(index[0].Name, StringComparison.Ordinal);
                if (at >= 0)
                {
                    string candidate = thumbs[0].Substring(0, at) + "{0}" + thumbs[0].Substring(at + index[0].Name.Length);
                    bool all = true;
                    for (int i = 0; i < index.Count && all; i++)
                    {
                        all = thumbs[i] != null && thumbs[i] == candidate.Replace("{0}", index[i].Name);
                    }

                    pattern = all ? candidate : null;
                }
            }

            var rows = new List<object[]>(index.Count);
            for (int i = 0; i < index.Count; i++)
            {
                BuildingInfo b = index[i];
                rows.Add(new object[]
                {
                    b.Name, b.Use, b.Theme, b.Packs, b.Level, b.LotWidth, b.LotDepth,
                    Math.Round(b.Height, 1),
                    b.SetbackMin.HasValue ? (object)Math.Round(b.SetbackMin.Value, 2) : null,
                    b.Parking ? 1 : 0,
                    b.Signature ? 1 : 0,
                    pattern != null ? null : thumbs[i],
                    b.ZoneType,
                    b.Origin,
                    b.Source,
                    b.ModId,
                });
            }

            return JsonConvert.SerializeObject(new { thumb = pattern, rows });
        }

        private class ZoneSummary
        {
            [JsonProperty("def")] public ZoneDefinition Definition;
            [JsonProperty("active")] public bool Active;
            [JsonProperty("color")] public string Color;
            [JsonProperty("icon")] public string Icon;
            [JsonProperty("baseName")] public string BaseName;

            // Null until a city has loaded and the rules have been applied.
            [JsonProperty("allowed")] public int? Allowed;
            [JsonProperty("levelOne")] public int? LevelOne;
        }
    }
}
