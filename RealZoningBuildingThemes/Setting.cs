using System.Collections.Generic;
using Colossal;
using Colossal.IO.AssetDatabase;
using Game;
using Game.Input;
using Game.Modding;
using Game.SceneFlow;
using Game.Settings;
using RealZoningBuildingThemes.Zones;
using Unity.Entities;

namespace RealZoningBuildingThemes
{
    [FileLocation(nameof(RealZoningBuildingThemes))]
    [SettingsUIGroupOrder(kZonesGroup, kHotkeyGroup)]
    [SettingsUIShowGroupName(kZonesGroup, kHotkeyGroup)]
    [SettingsUIKeyboardAction(kTogglePanelAction, ActionType.Button, usages: new[] { Usages.kDefaultUsage, Usages.kToolUsage })]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kZonesGroup = "Zones";
        public const string kHotkeyGroup = "Hotkey";
        public const string kTogglePanelAction = "TogglePanel";

        public Setting(IMod mod)
            : base(mod)
        {
        }

        /// <summary>
        /// Button: rezones every one of our zones in the open city to game zones, so the city loads
        /// safely without the mod. Asks for confirmation; only available with a city open.
        /// </summary>
        [SettingsUIButton]
        [SettingsUIConfirmation]
        [SettingsUISection(kSection, kZonesGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(NoCityOpen))]
        public bool PrepareUninstall
        {
            set
            {
                World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<Systems.ZonePanelUISystem>()?.PrepareUninstall();
            }
        }

        /// <summary>Keyboard shortcut that opens and closes the zone panel (Shift+Z by default; can be cleared).</summary>
        [SettingsUIKeyboardBinding(BindingKeyboard.Z, kTogglePanelAction, shift: true)]
        [SettingsUISection(kSection, kHotkeyGroup)]
        public ProxyBinding TogglePanelBinding { get; set; }

        /// <summary>Button: puts the shortcut back to Shift+Z.</summary>
        [SettingsUIButton]
        [SettingsUISection(kSection, kHotkeyGroup)]
        public bool ResetHotkey
        {
            set
            {
                ResetKeyBindings();
            }
        }

        public bool NoCityOpen() => GameManager.instance == null || !GameManager.instance.gameMode.IsGame();

        public override void SetDefaults()
        {
            // No stored options besides the key binding, which has its own default.
        }
    }

    public class LocaleEN : IDictionarySource
    {
        private readonly Setting m_Setting;

        public LocaleEN(Setting setting)
        {
            m_Setting = setting;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            var entries = new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "Real Zoning & Building Themes" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "Main" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kZonesGroup), "Zones" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kHotkeyGroup), "Hotkey" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.PrepareUninstall)), "Prepare for uninstall" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.PrepareUninstall)), "Makes this city safe to load without the mod" },
                { m_Setting.GetOptionWarningLocaleID(nameof(Setting.PrepareUninstall)), "Rezone every custom zone in this city to the game's zones? Buildings keep their own game zone under them; empty lots get each zone's base zone. Then save the city. Do this in each city before uninstalling the mod." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.TogglePanelBinding)), "Open or close the zone panel" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.TogglePanelBinding)), "Keyboard shortcut for the zone panel. Clear it to turn the shortcut off." },
                { m_Setting.GetBindingKeyLocaleID(Setting.kTogglePanelAction), "Open or close the zone panel" },
                { m_Setting.GetBindingMapLocaleID(), "Real Zoning & Building Themes" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ResetHotkey)), "Reset hotkey" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ResetHotkey)), "Sets the shortcut back to Shift+Z." },

                // Toolbar tabs (UIAssetCategoryPrefab) use the SubServices / SUB_SERVICE keys.
                { $"SubServices.NAME[{ZoneSetupSystem.CategoryName}]", "Real Zoning" },
                { $"Assets.SUB_SERVICE_DESCRIPTION[{ZoneSetupSystem.CategoryName}]", "Custom zones from Real Zoning & Building Themes. Each grows only the buildings chosen for it." },
            };

            // Zone names and descriptions from the zone library.
            foreach (ZoneDefinition zone in ZoneLibrary.Current.Zones)
            {
                entries[$"Assets.NAME[{zone.PrefabName}]"] = zone.Name;
                entries[$"Assets.DESCRIPTION[{zone.PrefabName}]"] = "Real Zoning & Building Themes zone. Grows only the buildings its rules allow.";
            }

            return entries;
        }

        public void Unload()
        {
        }
    }
}
