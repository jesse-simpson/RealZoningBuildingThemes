using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Colossal.PSI.Environment;
using Newtonsoft.Json;
using UnityEngine;

namespace RealZoningBuildingThemes.Zones
{
    /// <summary>One zone as stored in zones.json.</summary>
    public class ZoneDefinition
    {
        /// <summary>Stable id; the zone prefab is named "RZBT_" + id. Never rename a zone's id in a city that uses it.</summary>
        [JsonProperty("id")] public string Id;

        [JsonProperty("name")] public string Name;

        /// <summary>Fill colour as "#RRGGBB". Empty means the colour of the zone's icon (its game zone colour).</summary>
        [JsonProperty("color", NullValueHandling = NullValueHandling.Ignore)] public string Color;

        /// <summary>Toolbar icon borrowed from a game zone, by its prefab name (e.g. "EU Residential Medium Row"). Empty means the base zone's icon.</summary>
        [JsonProperty("icon", NullValueHandling = NullValueHandling.Ignore)] public string Icon;

        /// <summary>Game zone whose zone settings are copied. Empty means chosen from the first use.</summary>
        [JsonProperty("base", NullValueHandling = NullValueHandling.Ignore)] public string Base;

        /// <summary>Use keys: rlow, rrow, rmed, rhigh, rmix, rrent, clow, chigh, olow, ohigh, iman, iware. Empty means any use.</summary>
        [JsonProperty("uses")] public List<string> Uses = new List<string>();

        /// <summary>Theme keys (zone prefixes such as EU, NA, UK, USNE) or "pack:Name" for asset packs. Empty means any theme.</summary>
        [JsonProperty("themes")] public List<string> Themes = new List<string>();

        /// <summary>Whether shared base-game buildings (no theme prefix: generic offices, industry, low rent) pass a theme filter.</summary>
        [JsonProperty("includeShared")] public bool IncludeShared = true;

        [JsonProperty("minHeight", NullValueHandling = NullValueHandling.Ignore)] public float? MinHeight;

        [JsonProperty("maxHeight", NullValueHandling = NullValueHandling.Ignore)] public float? MaxHeight;

        /// <summary>Lot width in cells along the road, inclusive; null means no limit.</summary>
        [JsonProperty("minLotWidth", NullValueHandling = NullValueHandling.Ignore)] public int? MinLotWidth;

        [JsonProperty("maxLotWidth", NullValueHandling = NullValueHandling.Ignore)] public int? MaxLotWidth;

        /// <summary>Lot depth in cells back from the road, inclusive; null means no limit.</summary>
        [JsonProperty("minLotDepth", NullValueHandling = NullValueHandling.Ignore)] public int? MinLotDepth;

        [JsonProperty("maxLotDepth", NullValueHandling = NullValueHandling.Ignore)] public int? MaxLotDepth;

        /// <summary>Older single-choice setback ("attached", "narrow", "detached", "any"); read for old files and moved into Setbacks.</summary>
        [JsonProperty("setback", NullValueHandling = NullValueHandling.Ignore)] public string Setback;

        /// <summary>Allowed side setback classes on the narrower side, any combination: "attached" (under 0.5 m), "narrow" (0.5 to 3 m), "detached" (3 m and over). Empty means any.</summary>
        [JsonProperty("setbacks")] public List<string> Setbacks = new List<string>();

        /// <summary>Exact range in metres on the narrower side; overrides the preset when set.</summary>
        [JsonProperty("minSetback", NullValueHandling = NullValueHandling.Ignore)] public float? MinSetback;

        [JsonProperty("maxSetback", NullValueHandling = NullValueHandling.Ignore)] public float? MaxSetback;

        /// <summary>"any", "onlot" (driveways, lots or garages) or "none".</summary>
        [JsonProperty("parking")] public string Parking = "any";

        /// <summary>Buildings added by hand: a prefab name, or a design name without its _L1.._L5 level.</summary>
        [JsonProperty("include")] public List<string> Include = new List<string>();

        /// <summary>Buildings removed by hand, same naming as include. Exclude wins over include.</summary>
        [JsonProperty("exclude")] public List<string> Exclude = new List<string>();

        [JsonProperty("preferSameDesign")] public bool PreferSameDesign = true;

        /// <summary>
        /// Retired: out of the toolbar and the panel's main list, but still created at load, so
        /// any city that still has it painted keeps working. Retiring rezones the current city first.
        /// </summary>
        [JsonProperty("retired", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool Retired;

        /// <summary>
        /// Deleted: gone from the panel for good, but kept in the file and still created at load
        /// (always retired too). Zones are shared by every city, and a save stores painted cells by
        /// zone prefab; if the prefab vanished, any other city with this zone painted would load
        /// with those cells unzoned and their buildings condemned. Kept, the cells keep working
        /// there. Its id is never reused, since the prefab name must keep meaning this zone.
        /// </summary>
        [JsonProperty("deleted", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool Deleted;

        [JsonIgnore] public string PrefabName => ZoneSetupSystem.Prefix + Id;
    }

    public class ZoneLibraryFile
    {
        [JsonProperty("version")] public int Version = 1;

        [JsonProperty("zones")] public List<ZoneDefinition> Zones = new List<ZoneDefinition>();
    }

    /// <summary>
    /// The zone library: loads and saves zones.json in ModsData\RealZoningBuildingThemes. The zone
    /// panel edits it; a hand-edited file is read when the game starts.
    /// Zone prefabs are created once, when the mod loads (and for zones added in the panel), so a
    /// zone is never removed from the file: deleting only hides it (see ZoneDefinition.Deleted).
    /// </summary>
    public static class ZoneLibrary
    {

        public static ZoneLibraryFile Current { get; private set; } = new ZoneLibraryFile();

        /// <summary>Goes up each time the library is loaded or replaced; the zone panel watches it.</summary>
        public static int Revision { get; private set; }

        public static string FilePath => Path.Combine(EnvPath.kUserDataPath, "ModsData", nameof(RealZoningBuildingThemes), "zones.json");

        public static ZoneDefinition FindByPrefabName(string prefabName)
        {
            return Current.Zones.FirstOrDefault(z => z.PrefabName == prefabName);
        }

        public static bool TryParseColor(string hex, out Color color)
        {
            color = default;
            return !string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex, out color);
        }

        /// <summary>Reads zones.json, writing the starter file first if there is none. Never throws.</summary>
        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    Current = StarterLibrary();
                    Revision++;
                    Save();
                    Mod.log.Info($"ZoneLibrary: wrote starter zones to {FilePath}");
                    return;
                }

                var loaded = JsonConvert.DeserializeObject<ZoneLibraryFile>(File.ReadAllText(FilePath));
                if (loaded == null || loaded.Zones == null)
                {
                    Mod.log.Warn("ZoneLibrary: zones.json is empty or unreadable; keeping the previous zones.");
                    return;
                }

                // Drop zones without an id, and duplicate ids (first wins).
                var seen = new HashSet<string>();
                loaded.Zones = loaded.Zones
                    .Where(z => z != null && !string.IsNullOrWhiteSpace(z.Id) && seen.Add(z.Id.Trim()))
                    .ToList();
                foreach (var z in loaded.Zones)
                {
                    Normalize(z);
                }

                Current = loaded;
                Revision++;
                Mod.log.Info($"ZoneLibrary: loaded {Current.Zones.Count} zones from {FilePath}");
            }
            catch (Exception e)
            {
                Mod.log.Error(e, $"ZoneLibrary: could not read {FilePath}; keeping the previous zones. Check the file for a JSON typo.");
            }
        }

        /// <summary>Fills missing lists and names, and warns about unknown use keys.</summary>
        private static void Normalize(ZoneDefinition z)
        {
            z.Id = z.Id.Trim();
            z.Name = string.IsNullOrWhiteSpace(z.Name) ? z.Id : z.Name.Trim();
            z.Uses = z.Uses ?? new List<string>();
            z.Themes = z.Themes ?? new List<string>();
            z.Include = z.Include ?? new List<string>();
            z.Exclude = z.Exclude ?? new List<string>();
            z.Setbacks = z.Setbacks ?? new List<string>();
            if (z.Setbacks.Count == 0 && (z.Setback == "attached" || z.Setback == "narrow" || z.Setback == "detached"))
            {
                z.Setbacks.Add(z.Setback); // file written before setbacks could be combined
            }

            z.Setback = null;
            z.Setbacks = z.Setbacks.Where(c => c == "attached" || c == "narrow" || c == "detached").Distinct().ToList();
            z.Parking = string.IsNullOrEmpty(z.Parking) ? "any" : z.Parking;
            z.Retired |= z.Deleted; // a deleted zone is always out of the toolbar
            foreach (var use in z.Uses.Where(u => !UseKeys.Contains(u)))
            {
                Mod.log.Warn($"ZoneLibrary: zone '{z.Id}' has unknown use '{use}'. Known uses: {string.Join(", ", UseKeys)}.");
            }
        }

        /// <summary>
        /// Replaces one zone's definition (matched by id) and saves the file: the zone panel's Apply.
        /// The id cannot change here. Returns false when no zone has that id.
        /// </summary>
        public static bool ReplaceZone(ZoneDefinition zone)
        {
            if (zone == null || string.IsNullOrWhiteSpace(zone.Id))
            {
                return false;
            }

            Normalize(zone);
            int index = Current.Zones.FindIndex(z => z.Id == zone.Id);
            if (index < 0)
            {
                return false;
            }

            Current.Zones[index] = zone;
            Revision++;
            Save();
            Mod.log.Info($"ZoneLibrary: applied panel changes to zone '{zone.Id}' and saved {FilePath}");
            return true;
        }

        /// <summary>
        /// Adds a new zone made in the panel, with an id derived from its name (made unique), and
        /// saves the file. Returns the stored definition.
        /// </summary>
        public static ZoneDefinition AddZone(ZoneDefinition zone)
        {
            string stem = new string((zone.Name ?? "Zone").Where(char.IsLetterOrDigit).ToArray());
            if (stem.Length == 0)
            {
                stem = "Zone";
            }

            string id = stem;
            for (int n = 2; Current.Zones.Any(z => z.Id == id); n++)
            {
                id = stem + n;
            }

            zone.Id = id;
            Normalize(zone);
            Current.Zones.Add(zone);
            Revision++;
            Save();
            Mod.log.Info($"ZoneLibrary: added zone '{zone.Name}' (id {id}) from the panel and saved {FilePath}");
            return zone;
        }

        /// <summary>A copy of a zone under a new id and name ("Name copy"), saved. Null when the id is unknown.</summary>
        public static ZoneDefinition DuplicateZone(string id)
        {
            ZoneDefinition source = Current.Zones.FirstOrDefault(z => z.Id == id);
            if (source == null)
            {
                return null;
            }

            var copy = JsonConvert.DeserializeObject<ZoneDefinition>(JsonConvert.SerializeObject(source));
            copy.Name = (source.Name ?? "Zone") + " copy";
            copy.Retired = false;
            copy.Deleted = false;
            copy.Base = string.IsNullOrEmpty(source.Base) ? DefaultBaseZone(source) : source.Base; // same base as the original
            return AddZone(copy);
        }

        /// <summary>Marks a zone retired or active again and saves. False when the id is unknown.</summary>
        public static bool SetRetired(string id, bool retired)
        {
            ZoneDefinition zone = Current.Zones.FirstOrDefault(z => z.Id == id);
            if (zone == null || (zone.Deleted && !retired))
            {
                return false; // a deleted zone cannot come back
            }

            zone.Retired = retired;
            Revision++;
            Save();
            Mod.log.Info($"ZoneLibrary: zone '{id}' {(retired ? "retired" : "restored")}; saved {FilePath}");
            return true;
        }

        /// <summary>
        /// Deletes a retired zone: hidden from the panel for good, but kept in the file (marked
        /// deleted) so its prefab is still created and other cities that use it load safely.
        /// False when it is unknown or not retired.
        /// </summary>
        public static bool DeleteZone(string id)
        {
            ZoneDefinition zone = Current.Zones.FirstOrDefault(z => z.Id == id);
            if (zone == null || !zone.Retired)
            {
                return false;
            }

            zone.Deleted = true;
            Revision++;
            Save();
            Mod.log.Info($"ZoneLibrary: deleted zone '{id}' (kept hidden in the file so other cities that use it still load); saved {FilePath}");
            return true;
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(Current, Formatting.Indented));
            }
            catch (Exception e)
            {
                Mod.log.Error(e, $"ZoneLibrary: could not write {FilePath}");
            }
        }

        public static readonly string[] UseKeys = { "rlow", "rrow", "rmed", "rhigh", "rmix", "rrent", "clow", "chigh", "olow", "ohigh", "iman", "iware" };

        /// <summary>The game zone each use defaults to as its base (zone settings and toolbar icon).</summary>
        public static string DefaultBaseZone(ZoneDefinition zone)
        {
            string use = zone.Uses.FirstOrDefault() ?? "rmed";
            switch (use)
            {
                case "rlow": return "EU Residential Low";
                case "rrow": return "EU Residential Medium Row";
                case "rhigh": return "EU Residential High";
                case "rmix": return "EU Residential Mixed";
                case "rrent": return "Residential LowRent";
                case "clow": return "EU Commercial Low";
                case "chigh": return "EU Commercial High";
                case "olow": return "Office Low";
                case "ohigh": return "Office High";
                case "iman":
                case "iware": return "Industrial Manufacturing";
                default: return "EU Residential Medium";
            }
        }

        private static ZoneLibraryFile StarterLibrary()
        {
            return new ZoneLibraryFile
            {
                Zones = new List<ZoneDefinition>
                {
                    // Keeps the Phase 2 test zone (same prefab name), so painted cells survive.
                    new ZoneDefinition
                    {
                        Id = "TestRowhouses", Name = "Test: Rowhouses",
                        Uses = { "rrow" }, Themes = { "EU", "NA", "USNE", "UK" },
                    },
                    new ZoneDefinition
                    {
                        Id = "StreetcarSuburb", Name = "Streetcar Suburb",
                        Uses = { "rlow", "rrow" }, Themes = { "NA", "USNE" }, MaxHeight = 14f,
                    },
                    new ZoneDefinition
                    {
                        Id = "MainStreetMixed", Name = "Main Street Mixed",
                        Uses = { "rmix", "clow" }, Setbacks = { "attached" }, MaxHeight = 20f,
                    },
                },
            };
        }
    }
}
