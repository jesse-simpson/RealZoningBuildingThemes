using System;
using System.Collections.Generic;
using System.IO;
using Colossal;
using Game.SceneFlow;
using Newtonsoft.Json;
using RealZoningBuildingThemes.Zones;

namespace RealZoningBuildingThemes
{
    /// <summary>
    /// Translations. The mod folder's l10n\&lt;locale&gt;.json files (en-US, de-DE, fr-FR and so on;
    /// the same files the panel bundles) are registered with the game's localization: panel keys
    /// as "RZBT.&lt;key&gt;", and a few keys mapped to the game's own ids (options page, toolbar tab,
    /// zone descriptions). A locale without a file falls back to English.
    /// </summary>
    public static class ModLocalization
    {
        public static void Register(string modFolder, Setting setting)
        {
            var manager = GameManager.instance.localizationManager;
            string folder = string.IsNullOrEmpty(modFolder) ? null : Path.Combine(modFolder, "l10n");
            var registered = new List<string>();
            if (folder != null && Directory.Exists(folder))
            {
                foreach (string file in Directory.GetFiles(folder, "*.json"))
                {
                    string locale = Path.GetFileNameWithoutExtension(file);
                    manager.AddSource(locale, new LocaleFile(file, setting));
                    registered.Add(locale);
                }
            }

            if (!registered.Contains("en-US"))
            {
                manager.AddSource("en-US", new LocaleEN(setting));
            }

            Mod.log.Info(registered.Count > 0
                ? $"Localization: registered {registered.Count} languages ({string.Join(", ", registered)})."
                : $"Localization: no l10n folder at {folder}; English only.");
        }

        /// <summary>A translated string from the game's active language, or the given English.</summary>
        public static string Get(string key, string english)
        {
            try
            {
                var dictionary = GameManager.instance.localizationManager.activeDictionary;
                if (dictionary != null && dictionary.TryGetValue("RZBT." + key, out string value) && !string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
            catch (Exception)
            {
                // fall back to English
            }

            return english;
        }
    }

    /// <summary>One l10n\&lt;locale&gt;.json file as a game dictionary source, read when the game asks for it.</summary>
    public class LocaleFile : IDictionarySource
    {
        private readonly string m_Path;
        private readonly Setting m_Setting;

        public LocaleFile(string path, Setting setting)
        {
            m_Path = path;
            m_Setting = setting;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            var entries = new Dictionary<string, string>();
            Dictionary<string, string> strings;
            try
            {
                strings = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(m_Path)) ?? new Dictionary<string, string>();
            }
            catch (Exception e)
            {
                Mod.log.Warn($"Localization: could not read {m_Path} ({e.Message}).");
                return entries;
            }

            foreach (var pair in strings)
            {
                entries["RZBT." + pair.Key] = pair.Value;
            }

            // Game ids for the options page and the toolbar tab.
            void Map(string key, string gameId)
            {
                if (strings.TryGetValue(key, out string value))
                {
                    entries[gameId] = value;
                }
            }

            Map("Options.Name", m_Setting.GetSettingsLocaleID());
            Map("Options.Tab", m_Setting.GetOptionTabLocaleID(Setting.kSection));
            Map("Options.GroupZones", m_Setting.GetOptionGroupLocaleID(Setting.kZonesGroup));
            Map("Options.GroupHotkey", m_Setting.GetOptionGroupLocaleID(Setting.kHotkeyGroup));
            Map("Zones.Uninstall", m_Setting.GetOptionLabelLocaleID(nameof(Setting.PrepareUninstall)));
            Map("Zones.UninstallTip", m_Setting.GetOptionDescLocaleID(nameof(Setting.PrepareUninstall)));
            Map("Zones.UninstallConfirm", m_Setting.GetOptionWarningLocaleID(nameof(Setting.PrepareUninstall)));
            Map("Options.TogglePanel", m_Setting.GetOptionLabelLocaleID(nameof(Setting.TogglePanelBinding)));
            Map("Options.TogglePanelDesc", m_Setting.GetOptionDescLocaleID(nameof(Setting.TogglePanelBinding)));
            Map("Options.TogglePanel", m_Setting.GetBindingKeyLocaleID(Setting.kTogglePanelAction));
            Map("Options.Name", m_Setting.GetBindingMapLocaleID());
            Map("Options.ResetHotkey", m_Setting.GetOptionLabelLocaleID(nameof(Setting.ResetHotkey)));
            Map("Options.ResetHotkeyDesc", m_Setting.GetOptionDescLocaleID(nameof(Setting.ResetHotkey)));
            Map("Toolbar.Tab", $"SubServices.NAME[{ZoneSetupSystem.CategoryName}]");
            Map("Toolbar.TabDesc", $"Assets.SUB_SERVICE_DESCRIPTION[{ZoneSetupSystem.CategoryName}]");

            // Zone names are the player's own; descriptions are translated.
            strings.TryGetValue("Zone.Description", out string description);
            foreach (ZoneDefinition zone in ZoneLibrary.Current.Zones)
            {
                entries[$"Assets.NAME[{zone.PrefabName}]"] = zone.Name;
                entries[$"Assets.DESCRIPTION[{zone.PrefabName}]"] = description ?? "Real Zoning & Building Themes zone. Grows only the buildings its rules allow.";
            }

            return entries;
        }

        public void Unload()
        {
        }
    }
}
