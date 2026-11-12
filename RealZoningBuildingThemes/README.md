# Real Zoning & Building Themes

A Cities: Skylines II mod in the spirit of Building Themes from the first game. Create custom zones, paint them with the normal zoning tools, and choose exactly which buildings grow in each.

## Features

- **Custom zones**: each zone is based on a game zone (its demand, services and toolbar icon) and has its own colour and rules.
- **Rules**: use (including mixed use and low rent), theme or region pack, height, lot width and depth, side setbacks, and parking.
- **Per-building control**: include or exclude any building by hand, with thumbnails and Find It style filters.
- **All sources**: vanilla, DLC, region packs and custom assets. Custom assets are grouped by the asset pack they came from.
- **Zone management**: create, duplicate, retire, restore and delete zones. "Prepare for uninstall" (in Options) rezones a city back to game zones.
- **Translations**: 12 languages, machine translated; corrections welcome.

## Use

1. Open the panel with the zoning button in the top-left toolbar, or press Shift+Z (rebindable in Options).
2. Create a zone, set its rules, review the buildings, and Apply.
3. Paint the zone from the "Real Zoning" tab of the zoning toolbar.

Zones are saved in `ModsData\RealZoningBuildingThemes\zones.json` and shared by all your cities. Where you paint them is saved with each city.

## Compatibility

- **Plop the Growables**: supported. Its plopped buildings are left alone, and its despawn setting is honoured.
- **Find It**: works alongside. Zone type filters match Find It's.
- **Zone Organizer**: supported. Custom zones stay in their own tab.
- **Platter**: works alongside. Custom zones can be painted on parcels; Platter's building counts show 0 for them.
- **Unified Icon Library**: required for the zone type icons.

## Uninstalling

Saves store painted cells by zone. Before removing the mod, open each city, go to Options > Real Zoning & Building Themes and press **Prepare for uninstall**, then save. Otherwise those cells load unzoned and their buildings are condemned.

Deleting a zone hides it but keeps it registered, so other cities that use it still load safely.

## Building from source

Requires the Cities: Skylines II modding toolchain. Build the panel with `npm install` and `npm run build` in `UI\`, then build the project in Visual Studio. The UI is copied into the mod folder on build.

## Credits and licence

Includes code adapted from Plop the Growables by algernon (Apache 2.0) and Custom Zones by dvovo (MIT). See [NOTICE.txt](NOTICE.txt) and [LICENSES](LICENSES/). Inspired by Building Themes by boformer.

This mod is released under the MIT License ([LICENSE.txt](LICENSE.txt)).
