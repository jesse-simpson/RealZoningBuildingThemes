/** Display names for the use keys in zones.json, matching the game's own zone names. */
export const USE_LABELS: Record<string, string> = {
  rlow: "Low density housing",
  rrow: "Medium density row housing",
  rmed: "Medium density housing",
  rhigh: "High density housing",
  rmix: "Mixed housing",
  rrent: "Low rent housing",
  clow: "Low density commercial",
  chigh: "High density commercial",
  olow: "Low density offices",
  ohigh: "High density offices",
  iman: "Industrial manufacturing",
  iware: "Industrial warehouses",
};

/** Display names for theme keys (zone prefixes and asset packs). */
export const THEME_LABELS: Record<string, string> = {
  NA: "North America",
  EU: "Europe",
  "NA Waterfront": "North America Waterfront",
  "EU Waterfront": "Europe Waterfront",
  "NA Old Town": "North America Old Town",
  "EU Old Town": "Europe Old Town",
  UK: "United Kingdom",
  DE: "Germany",
  FR: "France",
  NL: "Netherlands",
  EE: "Eastern Europe",
  JP: "Japan",
  CN: "China",
  USNE: "US Northeast",
  USSW: "US Southwest",
  CCP5: "Mediterranean Heritage",
  UbPr: "Urban Promenades",
  Pack9: "Office Evolution",
};

export const themeLabel = (key: string) =>
  key.startsWith("pack:") ? key.substring(5) : THEME_LABELS[key] ?? key;

export function setbackLabel(z: { setback: string; minSetback: number | null; maxSetback: number | null }): string {
  switch (z.setback) {
    case "attached":
      return "Attached (under 0.5 m)";
    case "narrow":
      return "Narrow (0.5 to 3 m)";
    case "detached":
      return "Detached (3 m and over)";
    case "range":
      if (z.minSetback != null && z.maxSetback != null) return `${z.minSetback} to ${z.maxSetback} m`;
      if (z.minSetback != null) return `${z.minSetback} m and over`;
      if (z.maxSetback != null) return `Under ${z.maxSetback} m`;
      return "Any";
    default:
      return "Any";
  }
}

export function heightLabel(min: number | null, max: number | null): string {
  if (min != null && max != null) return `${min} to ${max} m`;
  if (min != null) return `${min} m and over`;
  if (max != null) return `Up to ${max} m`;
  return "Any";
}

export const PARKING_LABELS: Record<string, string> = {
  any: "Any",
  onlot: "On lot",
  none: "None",
};
