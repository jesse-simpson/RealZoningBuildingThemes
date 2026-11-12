using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using RealZoningBuildingThemes.Systems;
using RealZoningBuildingThemes.Zones;

namespace RealZoningBuildingThemes
{
    public class Mod : IMod
    {
        public static ILog log = LogManager.GetLogger($"{nameof(RealZoningBuildingThemes)}.{nameof(Mod)}").SetShowsErrorsInUI(false);
        private Setting m_Setting;

        /// <summary>The mod's settings (key bindings are read from here).</summary>
        public static Setting Settings { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));

            // The zone library first: zone names feed the translations registered below.
            ZoneLibrary.Load();

            string modFolder = null;
            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                log.Info($"Current mod asset at {asset.path}");
                modFolder = System.IO.Path.GetDirectoryName(asset.path);
            }

            m_Setting = new Setting(this);
            m_Setting.RegisterInOptionsUI();
            ModLocalization.Register(modFolder, m_Setting);
            m_Setting.RegisterKeyBindings();
            Settings = m_Setting;

            AssetDatabase.global.LoadSettings(nameof(RealZoningBuildingThemes), m_Setting, new Setting(this));


            // Phase 2, step 1: our zones. ZoneInitSystem finishes each zone entity right after the
            // game's ZoneSystem indexes it. The zones themselves are created now, in OnLoad, so the
            // first ZoneSystem pass indexes them together with the game's zones.
            updateSystem.UpdateAfter<ZoneInitSystem, Game.Prefabs.ZoneSystem>(SystemUpdatePhase.PrefabUpdate);
            updateSystem.World.GetOrCreateSystemManaged<ZoneSetupSystem>().InstallAtLoad();
            // ...and runs before ZoneSystem afterwards, to create zones made in the panel during a game.
            updateSystem.UpdateBefore<ZoneSetupSystem, Game.Prefabs.ZoneSystem>(SystemUpdatePhase.PrefabUpdate);

            // Phase 2, step 2: growth on our zones.
            // AllowList resolves which buildings each zone allows. Our spawner replaces the game's
            // ZoneSpawnSystem (same pace, the game's own evaluation for its zones, allow-lists for
            // ours). The pardon pass lifts the game's condemnation of allowed buildings before the
            // game's CondemnedBuildingSystem can demolish them.
            updateSystem.UpdateBefore<RzbtZoneSpawnSystem, Game.Simulation.ZoneSpawnSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateBefore<AllowListSystem, RzbtZoneSpawnSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<PardonSystem, Game.Common.ModificationEndBarrier>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateBefore<PardonSystem, Game.Simulation.CondemnedBuildingSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.World.GetOrCreateSystemManaged<Game.Simulation.ZoneSpawnSystem>().Enabled = false;

            // Our zone check replaces the game's (or Plop the Growables'), so buildings grown in our
            // zones are no longer condemned and pardoned over and over; the pardon pass stays as a
            // safety net.
            updateSystem.UpdateBefore<RzbtZoneCheckSystem, Game.Common.ModificationEndBarrier>(SystemUpdatePhase.ModificationEnd);

            // Rezoning to game zones (retire a zone, prepare a city for uninstall), early in the
            // modification phases so the game's zone systems pick the changed blocks up the same frame.
            updateSystem.UpdateAt<RezoneSystem>(SystemUpdatePhase.Modification1);

            // Phase 3: rules that change after buildings have grown.
            // Removal condemns buildings a zone no longer allows (after a reload, and once per city
            // load). LevelUpSwap keeps level-ups inside the zone's allow-list, just before the game's
            // BuildingConstructionSystem swaps the prefab.
            updateSystem.UpdateAfter<RemovalSystem, AllowListSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateBefore<LevelUpSwapSystem, Game.Simulation.BuildingConstructionSystem>(SystemUpdatePhase.GameSimulation);

            // Phase 4: the zone panel's bindings (the panel itself is the UI module built from UI\).
            updateSystem.UpdateAt<ZonePanelUISystem>(SystemUpdatePhase.UIUpdate);
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
            if (m_Setting != null)
            {
                m_Setting.UnregisterInOptionsUI();
                m_Setting = null;
            }
        }
    }
}
