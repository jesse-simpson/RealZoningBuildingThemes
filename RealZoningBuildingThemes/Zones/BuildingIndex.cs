using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Colossal.IO.AssetDatabase;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;

namespace RealZoningBuildingThemes.Zones
{
    /// <summary>The zoning facts the rules engine needs about one building prefab (one level of one design).</summary>
    public class BuildingInfo
    {
        public Entity Entity;
        public string Name;
        public string Family;     // name without its _L1.._L5 level
        public string HomeZone;
        public string Use;        // one of ZoneLibrary.UseKeys
        public string Theme;      // zone prefix such as EU or UK; empty for shared base-game buildings
        public List<string> Packs = new List<string>();
        public int Level;
        public int LotWidth;      // cells along the road
        public int LotDepth;      // cells back from the road
        public float Height;
        public float? SetbackMin; // narrower side, metres; null when the model has no mesh data
        public bool Parking;      // any on-lot parking, driveways included
        public bool Signature;

        /// <summary>Find It's zone type, plus low rent on its own: low, row, medium, high, lowrent; empty for non-residential.</summary>
        public string ZoneType = string.Empty;

        /// <summary>game (base game and DLC), pack (Paradox region packs) or custom (creator assets, subscribed or local).</summary>
        public string Origin = BuildingIndex.OriginGame;

        /// <summary>For custom buildings, the source the panel groups them by: "zone:Name" for a custom zone, "pack:Name" for a creator asset pack, "asset:Name" for a single package.</summary>
        public string Source = string.Empty;

        /// <summary>Paradox Mods id of a custom building's mod, when it has one.</summary>
        public string ModId = string.Empty;
    }

    /// <summary>
    /// Builds <see cref="BuildingInfo"/> for growable building prefabs. Use and theme come from the
    /// home zone (checked against all 7,227 growables in the Phase 1 export); setbacks from the union
    /// of mesh bounds, as in the export.
    /// </summary>
    public static class BuildingIndex
    {
        private const float kCellSize = 8f;
        private static readonly HashSet<string> s_UseWords = new HashSet<string> { "Residential", "Commercial", "Office", "Industrial" };
        private static readonly Regex s_Level = new Regex(@"_L[1-5](?=_|$)", RegexOptions.Compiled);
        private static readonly Regex s_PackPrefix = new Regex(@"^([A-Za-z0-9]+)-", RegexOptions.Compiled);

        public const string OriginGame = "game";
        public const string OriginPack = "pack";
        public const string OriginCustom = "custom";

        /// <summary>Theme key that allows every custom building.</summary>
        public const string CustomAll = "custom:*";

        /// <summary>Theme keys of Paradox's region packs (zone name prefixes and "KEY Pack Filter" asset packs).</summary>
        public static readonly HashSet<string> RegionPackKeys = new HashSet<string> { "UK", "DE", "FR", "NL", "EE", "JP", "CN", "USNE", "USSW" };

        /// <summary>
        /// Other spellings a region pack might use for its zone prefix or pack filter, mapped to the
        /// theme key. China's pack is not installed here, so its prefix is unconfirmed: CN is assumed,
        /// and the likely alternatives are mapped to it.
        /// </summary>
        private static readonly Dictionary<string, string> s_ThemeAliases = new Dictionary<string, string>
        {
            ["CHN"] = "CN", ["China"] = "CN", ["Chinese"] = "CN",
            ["GER"] = "DE", ["Germany"] = "DE",
            ["NED"] = "NL", ["Netherlands"] = "NL", ["Dutch"] = "NL",
            ["JPN"] = "JP", ["Japan"] = "JP",
            ["FRA"] = "FR", ["France"] = "FR",
            ["GB"] = "UK",
        };

        /// <summary>
        /// Resource words in the names of the game's specialized-industry hub zones ("Industrial
        /// Forestry", "Industrial Offshore Oil", "Industrial Aquaculture - Land" ...). Their buildings
        /// belong next to the matching extraction area, not on ordinary zoning.
        /// </summary>
        private static readonly string[] s_HubZoneWords = { "Forestry", "Agriculture", "Ore", "Oil", "Aquaculture", "Fishing" };

        /// <summary>True for buildings of the game's specialized-industry hub zones; they are left out of the index.</summary>
        public static bool IsResourceHub(BuildingInfo info)
        {
            if ((info.Use != "iman" && info.Use != "iware") || string.IsNullOrEmpty(info.HomeZone) ||
                !info.HomeZone.StartsWith("Industrial ", StringComparison.Ordinal))
            {
                return false;
            }

            foreach (string word in s_HubZoneWords)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(info.HomeZone, $"\\b{word}\\b"))
                {
                    return true;
                }
            }

            return false;
        }

        private static string CanonicalTheme(string key) => key != null && s_ThemeAliases.TryGetValue(key, out string canonical) ? canonical : key;

        public static BuildingInfo Build(EntityManager em, PrefabSystem prefabs, Entity entity)
        {
            var info = new BuildingInfo { Entity = entity };
            prefabs.TryGetPrefab(entity, out PrefabBase prefab);
            info.Name = prefab != null ? prefab.name : prefabs.GetPrefabName(entity);
            info.Family = s_Level.Replace(info.Name, string.Empty);
            info.Signature = em.HasComponent<SignatureBuildingData>(entity);

            if (prefab != null && prefab.TryGet(out AssetPackItem packItem) && packItem.m_Packs != null)
            {
                foreach (var pack in packItem.m_Packs)
                {
                    if (pack != null)
                    {
                        info.Packs.Add(pack.name);
                    }
                }
            }

            var spawnable = em.GetComponentData<SpawnableBuildingData>(entity);
            info.Level = spawnable.m_Level;
            Entity zone = spawnable.m_ZonePrefab;
            info.HomeZone = zone != Entity.Null && em.Exists(zone) ? prefabs.GetPrefabName(zone) : string.Empty;
            info.Theme = ThemeOf(info.HomeZone);
            info.Use = UseOf(em, zone, info.HomeZone, info.Name, entity);

            if (em.HasComponent<ObjectGeometryData>(entity))
            {
                info.Height = em.GetComponentData<ObjectGeometryData>(entity).m_Size.y;
            }

            var building = em.GetComponentData<BuildingData>(entity);
            info.LotWidth = building.m_LotSize.x;
            info.LotDepth = building.m_LotSize.y;
            if (TryGetMeshBounds(em, entity, out float3 min, out float3 max))
            {
                float halfLot = building.m_LotSize.x * kCellSize * 0.5f;
                info.SetbackMin = Math.Min(min.x + halfLot, halfLot - max.x);
            }

            info.Parking = HasParking(em, entity);
            SetOrigin(info, prefab, zone, prefabs);
            return info;
        }

        /// <summary>
        /// Game, region pack or custom. Region packs come from Paradox Mods like creator assets, so
        /// they are told apart by their theme (zone prefix) or their "KEY Pack Filter" asset pack.
        /// Custom buildings are grouped by their custom zone if they have one, else by the creator's
        /// asset pack, else by their package (one Paradox Mods upload, usually one building family).
        /// </summary>
        private static void SetOrigin(BuildingInfo info, PrefabBase prefab, Entity zone, PrefabSystem prefabs)
        {
            if (prefab == null || prefab.isBuiltin)
            {
                info.Origin = OriginGame;
                return;
            }

            bool regionPack = RegionPackKeys.Contains(info.Theme) ||
                info.Packs.Any(p => p.EndsWith(" Pack Filter", StringComparison.Ordinal) && RegionPackKeys.Contains(CanonicalTheme(p.Substring(0, p.Length - " Pack Filter".Length))));
            if (regionPack)
            {
                info.Origin = OriginPack;
                return;
            }

            info.Origin = OriginCustom;
            string packageName = null;
            try
            {
                if (prefab.asset != null)
                {
                    SourceMeta meta = prefab.asset.GetMeta();
                    info.ModId = meta.platformID ?? string.Empty;
                    packageName = meta.packaged ? meta.packageName : prefab.asset.name;
                }
            }
            catch (Exception)
            {
                // Asset metadata is a nicety; fall back to the prefab name below.
            }

            bool customZone = zone != Entity.Null && prefabs.TryGetPrefab(zone, out PrefabBase zonePrefab) && zonePrefab != null && !zonePrefab.isBuiltin;
            if (customZone)
            {
                info.Source = "zone:" + info.HomeZone;
            }
            else if (info.Packs.Count > 0)
            {
                info.Source = "pack:" + info.Packs[0];
            }
            else
            {
                info.Source = "asset:" + (string.IsNullOrEmpty(packageName) ? info.Family : packageName);
            }
        }

        /// <summary>Source of custom buildings whose own source has only one building.</summary>
        public const string OtherSource = "other:Other custom assets";

        /// <summary>
        /// Custom sources with a single building are folded into one "Other custom assets" source,
        /// so the panel's Custom list shows only real packs and zones (decision 2026-11-11).
        /// </summary>
        public static void GroupSmallSources(IReadOnlyList<BuildingInfo> index)
        {
            var counts = new Dictionary<string, int>();
            foreach (var b in index)
            {
                if (b.Origin == OriginCustom)
                {
                    counts.TryGetValue(b.Source, out int n);
                    counts[b.Source] = n + 1;
                }
            }

            foreach (var b in index)
            {
                if (b.Origin == OriginCustom && counts[b.Source] < 2)
                {
                    b.Source = OtherSource;
                }
            }
        }

        /// <summary>
        /// Find It's zone type for residential buildings, from the home zone's properties: low when the
        /// zone does not scale homes, high at one or more homes per unit of space, otherwise row when
        /// every building of that zone is at most 2 cells wide, else medium. Low rent is its own type
        /// here (Find It counts it by density). Everything else has no zone type, as in Find It.
        /// </summary>
        public static void AssignZoneTypes(EntityManager em, IReadOnlyList<BuildingInfo> index)
        {
            var wideZones = new HashSet<string>();
            foreach (var b in index)
            {
                if (b.LotWidth > 2)
                {
                    wideZones.Add(b.HomeZone);
                }
            }

            var byZone = new Dictionary<Entity, string>();
            foreach (var b in index)
            {
                Entity zone = em.GetComponentData<SpawnableBuildingData>(b.Entity).m_ZonePrefab;
                if (!byZone.TryGetValue(zone, out string type))
                {
                    type = string.Empty;
                    if (zone != Entity.Null && em.Exists(zone) && em.HasComponent<ZonePropertiesData>(zone))
                    {
                        var props = em.GetComponentData<ZonePropertiesData>(zone);
                        if (props.m_ResidentialProperties > 0f)
                        {
                            float perSpace = props.m_ResidentialProperties / props.m_SpaceMultiplier;
                            type = !props.m_ScaleResidentials ? "low" : perSpace >= 1f ? "high" : wideZones.Contains(b.HomeZone) ? "medium" : "row";
                        }
                    }

                    byZone[zone] = type;
                }

                b.ZoneType = b.Use == "rrent" ? "lowrent" : type;
            }
        }

        /// <summary>
        /// First word of the home zone name unless it is a use word: "UK Residential Low" gives UK,
        /// "Office High" gives "". Waterfront and Old Town zones are themes of their own, per region:
        /// "NA Residential Low Waterfront" gives "NA Waterfront", "EU Residential Mixed Old Town"
        /// gives "EU Old Town" (decision 2026-11-11).
        /// </summary>
        public static string ThemeOf(string homeZone)
        {
            if (string.IsNullOrEmpty(homeZone))
            {
                return string.Empty;
            }

            string[] words = homeZone.Split(' ');
            if (words.Length > 1 && !s_UseWords.Contains(words[0]))
            {
                if (homeZone.EndsWith(" Waterfront", StringComparison.Ordinal))
                {
                    return words[0] + " Waterfront";
                }

                if (homeZone.EndsWith(" Old Town", StringComparison.Ordinal))
                {
                    return words[0] + " Old Town";
                }

                return CanonicalTheme(words[0]);
            }

            Match pack = s_PackPrefix.Match(homeZone);
            return pack.Success ? CanonicalTheme(pack.Groups[1].Value) : string.Empty;
        }

        private static string UseOf(EntityManager em, Entity zone, string zoneName, string buildingName, Entity building)
        {
            var area = Game.Zones.AreaType.None;
            bool office = false;
            string ambience = string.Empty;
            if (zone != Entity.Null && em.Exists(zone))
            {
                if (em.HasComponent<ZoneData>(zone))
                {
                    var zoneData = em.GetComponentData<ZoneData>(zone);
                    area = zoneData.m_AreaType;
                    office = zoneData.IsOffice();
                }

                if (em.HasComponent<GroupAmbienceData>(zone))
                {
                    ambience = em.GetComponentData<GroupAmbienceData>(zone).m_AmbienceType.ToString();
                }
            }

            switch (area)
            {
                case Game.Zones.AreaType.Residential:
                    if (zoneName.Contains("LowRent") || ambience == "ResidentialLowRent") return "rrent";
                    if (zoneName.Contains("Mixed") || ambience == "ResidentialMixed" || ambience == "OldTown") return "rmix";
                    if (zoneName.Contains("Row") || zoneName.Contains("Terraced")) return "rrow";
                    if (zoneName.Contains("High")) return "rhigh";
                    if (zoneName.Contains("Medium")) return "rmed";
                    return "rlow";
                case Game.Zones.AreaType.Commercial:
                    return zoneName.Contains("High") ? "chigh" : "clow";
                case Game.Zones.AreaType.Industrial:
                    if (office) return zoneName.Contains("High") ? "ohigh" : "olow";
                    return em.HasComponent<WarehouseData>(building) ? "iware" : "iman";
                default:
                    return string.Empty;
            }
        }

        private static bool HasParking(EntityManager em, Entity building)
        {
            if (em.HasComponent<ParkingFacilityData>(building) && em.GetComponentData<ParkingFacilityData>(building).m_GarageMarkerCapacity > 0)
            {
                return true;
            }

            if (HasParkingLane(em, building))
            {
                return true;
            }

            if (em.HasBuffer<Game.Prefabs.SubObject>(building))
            {
                var subObjects = em.GetBuffer<Game.Prefabs.SubObject>(building, true);
                for (int i = 0; i < subObjects.Length; i++)
                {
                    Entity sub = subObjects[i].m_Prefab;
                    if (sub != Entity.Null && em.Exists(sub) && (em.HasComponent<ParkingSpaceData>(sub) || HasParkingLane(em, sub)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool HasParkingLane(EntityManager em, Entity prefab)
        {
            if (!em.HasBuffer<Game.Prefabs.SubLane>(prefab))
            {
                return false;
            }

            var lanes = em.GetBuffer<Game.Prefabs.SubLane>(prefab, true);
            for (int i = 0; i < lanes.Length; i++)
            {
                Entity lane = lanes[i].m_Prefab;
                if (lane != Entity.Null && em.Exists(lane) && em.HasComponent<ParkingLaneData>(lane))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetMeshBounds(EntityManager em, Entity prefab, out float3 min, out float3 max)
        {
            min = new float3(float.MaxValue);
            max = new float3(float.MinValue);
            if (!em.HasBuffer<SubMesh>(prefab))
            {
                return false;
            }

            int count = 0;
            var subMeshes = em.GetBuffer<SubMesh>(prefab, true);
            for (int i = 0; i < subMeshes.Length; i++)
            {
                SubMesh sub = subMeshes[i];
                if ((sub.m_Flags & (SubMeshFlags.RequireEditor | SubMeshFlags.OutlineOnly)) != 0 ||
                    sub.m_SubMesh == Entity.Null || !em.Exists(sub.m_SubMesh) || !em.HasComponent<MeshData>(sub.m_SubMesh))
                {
                    continue;
                }

                var bounds = em.GetComponentData<MeshData>(sub.m_SubMesh).m_Bounds;
                for (int c = 0; c < 8; c++)
                {
                    var corner = new float3(
                        (c & 1) == 0 ? bounds.min.x : bounds.max.x,
                        (c & 2) == 0 ? bounds.min.y : bounds.max.y,
                        (c & 4) == 0 ? bounds.min.z : bounds.max.z);
                    float3 p = sub.m_Position + math.rotate(sub.m_Rotation, corner);
                    min = math.min(min, p);
                    max = math.max(max, p);
                }

                count++;
            }

            return count > 0;
        }

        /// <summary>The rules engine: does this building belong in this zone?</summary>
        public static bool Matches(ZoneDefinition zone, BuildingInfo b)
        {
            if (b.Signature || zone.Exclude.Contains(b.Name) || zone.Exclude.Contains(b.Family))
            {
                return false;
            }

            if (zone.Include.Contains(b.Name) || zone.Include.Contains(b.Family))
            {
                return true;
            }

            if (zone.Uses.Count > 0 && !zone.Uses.Contains(b.Use))
            {
                return false;
            }

            if (zone.Themes.Count > 0)
            {
                // Custom buildings are chosen by their source in the Custom list (or all at once),
                // never by the theme of the vanilla zone they were made for, so subscribing to an
                // asset does not change a zone that was set up before it was installed.
                bool themeOk = b.Origin == OriginCustom
                    ? zone.Themes.Contains(CustomAll) || zone.Themes.Contains("custom:" + b.Source)
                    : string.IsNullOrEmpty(b.Theme)
                        ? zone.IncludeShared
                        : zone.Themes.Contains(b.Theme);
                if (!themeOk)
                {
                    foreach (string pack in b.Packs)
                    {
                        if (zone.Themes.Contains("pack:" + pack))
                        {
                            themeOk = true;
                            break;
                        }
                    }
                }

                if (!themeOk)
                {
                    return false;
                }
            }

            if ((zone.MinHeight.HasValue && b.Height < zone.MinHeight.Value) ||
                (zone.MaxHeight.HasValue && b.Height > zone.MaxHeight.Value))
            {
                return false;
            }

            if ((zone.MinLotWidth.HasValue && b.LotWidth < zone.MinLotWidth.Value) ||
                (zone.MaxLotWidth.HasValue && b.LotWidth > zone.MaxLotWidth.Value) ||
                (zone.MinLotDepth.HasValue && b.LotDepth < zone.MinLotDepth.Value) ||
                (zone.MaxLotDepth.HasValue && b.LotDepth > zone.MaxLotDepth.Value))
            {
                return false;
            }

            if (!SetbackMatches(zone, b))
            {
                return false;
            }

            switch (zone.Parking)
            {
                case "onlot": return b.Parking;
                case "none": return !b.Parking;
                default: return true;
            }
        }

        /// <summary>For the log: the first rule a building fails in a zone (mirrors <see cref="Matches"/>).</summary>
        public static string Explain(ZoneDefinition zone, BuildingInfo b)
        {
            if (b.Signature) return "signature building";
            if (zone.Exclude.Contains(b.Name) || zone.Exclude.Contains(b.Family)) return "excluded by hand";
            if (zone.Include.Contains(b.Name) || zone.Include.Contains(b.Family)) return "added by hand (allowed)";
            if (zone.Uses.Count > 0 && !zone.Uses.Contains(b.Use)) return $"use is {b.Use}";
            if (zone.Themes.Count > 0)
            {
                var only = new ZoneDefinition { Themes = zone.Themes, IncludeShared = zone.IncludeShared };
                if (!Matches(only, b))
                {
                    return b.Origin == OriginCustom ? $"custom building ({b.Source}) not chosen" : $"theme '{b.Theme}' ({b.Origin}{(b.Packs.Count > 0 ? ", packs " + string.Join("/", b.Packs) : string.Empty)}) not chosen";
                }
            }

            if ((zone.MinHeight.HasValue && b.Height < zone.MinHeight.Value) || (zone.MaxHeight.HasValue && b.Height > zone.MaxHeight.Value)) return $"height {b.Height:0.#} m";
            if ((zone.MinLotWidth.HasValue && b.LotWidth < zone.MinLotWidth.Value) || (zone.MaxLotWidth.HasValue && b.LotWidth > zone.MaxLotWidth.Value)) return $"lot {b.LotWidth} wide";
            if ((zone.MinLotDepth.HasValue && b.LotDepth < zone.MinLotDepth.Value) || (zone.MaxLotDepth.HasValue && b.LotDepth > zone.MaxLotDepth.Value)) return $"lot {b.LotDepth} deep";
            if (!SetbackMatches(zone, b)) return b.SetbackMin.HasValue ? $"side setback {b.SetbackMin.Value:0.##} m" : "no model data for setback";
            if (zone.Parking == "onlot" && !b.Parking) return "no parking on the lot";
            if (zone.Parking == "none" && b.Parking) return "has parking on the lot";
            return "matches the rules";
        }

        private static bool SetbackMatches(ZoneDefinition zone, BuildingInfo b)
        {
            bool exact = zone.MinSetback.HasValue || zone.MaxSetback.HasValue;
            if (!exact && zone.Setbacks.Count == 0)
            {
                return true;
            }

            if (!b.SetbackMin.HasValue)
            {
                return false; // no mesh data: only "any" accepts it
            }

            float s = b.SetbackMin.Value;
            if (exact)
            {
                return (!zone.MinSetback.HasValue || s >= zone.MinSetback.Value) && (!zone.MaxSetback.HasValue || s < zone.MaxSetback.Value);
            }

            // Classes on the narrower side; negative means the model overhangs the lot (attached).
            string setbackClass = s < 0.5f ? "attached" : s < 3f ? "narrow" : "detached";
            return zone.Setbacks.Contains(setbackClass);
        }
    }
}
