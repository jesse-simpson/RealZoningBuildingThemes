# Real Zoning & Building Themes

A Cities: Skylines II mod in the spirit of Building Themes from the original Cities: Skylines. Create zones with custom rules, choose exactly which buildings grow in each, and paint them with normal zoning tools.

Tired of micromanaging 20 different zone types or plopping buildings by hand to get a range of buildings and realistic cities? Want to allow both residential and commercial buildings in the same zone? Want to finely control building heights? Then this mod is for you. It adds the ability to easily zone for any available building using a set of basic zoning rules, and then customize the buildings chosen to your heart's content.

## Features

- **Custom zones**: each zone is based on a game zone (its demand, services and toolbar icon) and can be customized however you would like.
- **Zoning rules**: restrict what grows by its use, theme or region pack, height, lot width and depth, side setbacks, and parking.
- **Per-building control**: include or exclude any building by hand, with thumbnails and filters.
- **All sources**: vanilla, DLC, region packs and custom assets are all supported. Custom assets are grouped by the asset pack they came from.
- **Safe retirement and uninstall**: retiring a custom zone rezones it back to game zones in the open city, with each building returned to its own game zone and empty lots given the zone's base zone. Retired zones can be restored or deleted. "Prepare for uninstall" (in Options) does this for every custom zone in a city, for safe removal.
- **Translations**: 12 languages, machine translated; corrections welcome.

## Use

1. Open the panel with the zoning button in the top-left toolbar, or press Shift+Z (rebindable in Options).
2. Create a zone, set its rules, review the buildings, and Apply.
3. Paint the zone from the "Real Zoning" tab of the zoning toolbar.

Zones are saved in `ModsData\RealZoningBuildingThemes\zones.json` and shared by all your cities. Where you paint them is saved with each city.

## Requirements

- **Unified Icon Library**: required for the zone type icons.

## Compatibility

- **Plop the Growables**: supported. Its plopped buildings are left alone, and its despawn setting is honored.
- **Zone Organizer**: supported. Custom zones stay in a separate tab.
- **Platter**: mostly works. Custom zones can be painted on parcels created by Platter and the appropriate buildings will grow; however, Platter's building counts will show 0 for each configuration.

## Uninstalling

Before removing the mod, open each city, go to Options > Real Zoning & Building Themes and press **Prepare for uninstall**, then save. Otherwise any custom-zoned cells load unzoned and their buildings are condemned.

Deleting a zone hides it but keeps it registered, so other cities that use it still load safely.

## How it works

This section is for modders and the curious. In the base game, every growable building belongs to one "home" zone (for example, "EU Residential Medium"), and the game only grows, keeps and levels up a building on cells of its home zone. The mod adds zones that can hold buildings from many home zones, so it has to step in at each of those points.

**Zones.** Each zone in `zones.json` becomes a real game zone prefab named `RZBT_<id>`. It is created when the mod loads, so the game indexes it alongside its own zones, and it copies the settings of its base game zone (area type, demand and services). Zones created in the panel during a game are created the same way and unlocked on the spot. Painted cells are saved with the city like any other zone.

**Building index and rules.** When a city loads, every growable building prefab (each level of each design) is indexed once. For each one the mod records:
- use and theme, from its home zone;
- lot width and depth;
- height, from its model;
- side setback on the narrower side, from its mesh bounds;
- whether it has parking on the lot (garages, parking spaces or parking lanes).

A zone's rules filter on these facts, and a building must pass every rule that is set. Hand-picked exclusions always win, and hand-picked inclusions skip the rules. Signature buildings never grow from zones. The result is each zone's allow-list. Custom assets are matched by the pack or source they came from rather than by the vanilla theme they were made for, so subscribing to a new asset never changes an existing zone.

**Growth.** The game's zone spawner only offers buildings whose home zone matches the cell, so on its own it would grow nothing in a custom zone. The mod replaces it with a spawner that runs at the same pace:
- Game zones are evaluated by the game's own job, unchanged.
- Custom zones draw level 1 candidates from their allow-list. Each candidate is scored by the rules of its own home zone (demand, land value, pollution and milestones) and competes in the game's normal queues.

This is what lets one zone mix themes, densities, and uses such as residential and commercial. Residential candidates of different densities are ranked on the custom zone's own density demand, so a house and a row house compete on fit and location.

**Keeping buildings.** The game's zone check condemns a grown building whose lot cells are not its home zone, which would include everything grown in a custom zone. The mod replaces that check with one that also accepts custom-zone cells that allow the building. When Plop the Growables is installed, this check runs in place of Plop the Growables' own and follows its despawn setting. A pardon pass stays as a safety net, lifting any such condemnation before the game can demolish the building.

**Level-ups.** The game levels a building up by picking a random next-level design from its home zone. For buildings in custom zones, the mod:
- swaps that choice for one the zone allows, preferring the building's own design at the next level;
- starts a level-up the game missed because only the custom zone had an option;
- cancels the materials request when the zone allows no next level, so the building waits like a top-level building until the rules change.

**Rule changes.** When you Apply new rules, and once each time a city loads, every grown building on a custom zone is checked against the current allow-lists. Any building the zone no longer allows is condemned with the game's usual icon, demolished, and replaced by an allowed building. Plopped and signature buildings are left alone.

**Rezoning.** A save stores which zone prefab each cell had. If that prefab is missing when the city loads, its cells come back unzoned and every building on them is condemned. Retire and Prepare for uninstall avoid this by rewriting the city's custom-zone cells:
- cells under a building take that building's own home zone, so the game's check accepts it and nothing is demolished;
- empty cells take the zone's base zone.

For the same reason, deleted zones stay registered.

**Panel.** The zone panel is a React module built with the game's UI modding toolchain. It talks to the C# side through bindings in the `rzbt` group. It receives the whole building index, so it can preview rule changes and building counts live before you Apply.

**Fallbacks.** If the replacement spawner or zone check ever throws an error, it switches the game's own system back on and turns itself off.

### Source layout

- `RealZoningBuildingThemes/` holds the project. Inside it:
- `Mod.cs`, `Setting.cs`, `Localization.cs`: entry point, Options page and translations.
- `Zones/`: zone creation (`ZoneSetupSystem`), the building index and rules (`BuildingIndex`, `AllowListSystem`), growth (`RzbtZoneSpawnSystem`), zone check and pardon (`RzbtZoneCheckSystem`, `PardonSystem`), level-ups (`LevelUpSwapSystem`), rule changes (`RemovalSystem`), rezoning (`RezoneSystem`), and the zone library (`ZoneLibrary`).
- `Systems/ZonePanelUISystem.cs`: the panel's bindings.
- `UI/`: the panel (React and TypeScript) and the translation files in `UI/l10n/`.

### Building from source

Requires the Cities: Skylines II modding toolchain. Build the panel with `npm install` and `npm run build` in `RealZoningBuildingThemes\UI\`, then build the project in Visual Studio. The UI is copied into the mod folder on build.

## Credits and license

Code was written by Claude Opus 5.5, with extensive human oversight in every step in the process. Results have been tested on multiple heavily-modded cities without issue.

Includes code adapted from Plop the Growables by algernon (Apache 2.0) and Custom Zones by dvovo (MIT). See [NOTICE.txt](RealZoningBuildingThemes/NOTICE.txt) and [LICENSES](RealZoningBuildingThemes/LICENSES/). Inspired by Building Themes by boformer.

This mod is released under the MIT License ([LICENSE](LICENSE)).
