// Zone definitions, the building index and the rules engine, as the panel sees them.
// The rules here mirror BuildingIndex.Matches in C# exactly, so the counts shown while editing
// match what Apply produces. Keep the two in step.

import allIcon from "./images/all.svg";
import sharedIcon from "./images/shared.svg";
import naWaterfront from "./images/wf_na.svg";
import euWaterfront from "./images/wf_eu.svg";
import naOldTown from "./images/ot_na.svg";
import euOldTown from "./images/ot_eu.svg";
import packUK from "./images/pack_UK.svg";
import packDE from "./images/pack_DE.svg";
import packFR from "./images/pack_FR.svg";
import packNL from "./images/pack_NL.svg";
import packEE from "./images/pack_EE.svg";
import packJP from "./images/pack_JP.svg";
import packCN from "./images/pack_CN.svg";
import packNE from "./images/pack_NE.svg";
import packSW from "./images/pack_SW.svg";
import lowRentIcon from "./images/lowrent.svg";
import { t } from "i18n";

/** A zone as stored in zones.json (ZoneDefinition in C#). */
export interface ZoneDef {
  id: string;
  name: string;
  color?: string | null;
  icon?: string | null;
  base?: string | null;
  uses: string[];
  themes: string[];
  includeShared: boolean;
  minHeight?: number | null;
  maxHeight?: number | null;
  /** Lot width (cells along the road) and depth (cells back), inclusive; null = no limit. */
  minLotWidth?: number | null;
  maxLotWidth?: number | null;
  minLotDepth?: number | null;
  maxLotDepth?: number | null;
  /** Allowed setback classes, any combination of "attached", "narrow", "detached"; empty = any. */
  setbacks: string[];
  minSetback?: number | null;
  maxSetback?: number | null;
  parking: string;
  include: string[];
  exclude: string[];
  preferSameDesign: boolean;
  /** Out of the toolbar and main list; still loaded so other cities that use it keep working. */
  retired?: boolean;
}

/** One growable building prefab from the index. */
export interface Bldg {
  name: string;
  family: string;
  use: string;
  theme: string;
  packs: string[];
  level: number;
  lotW: number;
  lotD: number;
  height: number;
  setback: number | null;
  parking: boolean;
  signature: boolean;
  thumb: string;
  /** Find It's zone type plus low rent: low, row, medium, high, lowrent; "" for non-residential. */
  zoneType: string;
  /** game (base game and DLC), pack (Paradox region packs) or custom (creator assets). */
  origin: string;
  /** For custom buildings: "zone:Name", "pack:Name" or "asset:Name". */
  source: string;
  modId: string;
}

export const PLACEHOLDER = "Media/Placeholder.svg";

export function parseBuildings(json: string): Bldg[] {
  try {
    const data = JSON.parse(json) as { thumb: string | null; rows: any[][] };
    return data.rows.map((r) => ({
      name: r[0],
      family: (r[0] as string).replace(/_L[1-5](?=_|$)/, ""),
      use: r[1],
      theme: r[2],
      packs: r[3] ?? [],
      level: r[4],
      lotW: r[5],
      lotD: r[6],
      height: r[7],
      setback: r[8] ?? null, // negative when the model overhangs the lot
      parking: r[9] === 1,
      signature: r[10] === 1,
      thumb: r[11] ?? (data.thumb ? data.thumb.replace("{0}", r[0]) : PLACEHOLDER),
      zoneType: r[12] ?? "",
      origin: r[13] ?? "game",
      source: r[14] ?? "",
      modId: r[15] ?? "",
    }));
  } catch {
    return [];
  }
}

// ---- Uses, in the game's own zone categories

export const USE_GROUPS: { label: string; items: [string, string][] }[] = [
  { label: "Residential", items: [["rlow", "Low"], ["rrow", "Row"], ["rmed", "Medium"], ["rhigh", "High"], ["rmix", "Mixed"], ["rrent", "Low rent"]] },
  { label: "Commercial", items: [["clow", "Low"], ["chigh", "High"]] },
  { label: "Office", items: [["olow", "Low"], ["ohigh", "High"]] },
  { label: "Industrial", items: [["iman", "Manufacturing"], ["iware", "Warehouses"]] },
];

export const USE_SHORT: Record<string, string> = {
  rlow: "Res low", rrow: "Res row", rmed: "Res medium", rhigh: "Res high", rmix: "Mixed", rrent: "Low rent",
  clow: "Com low", chigh: "Com high", olow: "Office low", ohigh: "Office high", iman: "Manufacturing", iware: "Warehouses",
};

/** Use filter groups on the Buildings tab. */
export const USE_FILTERS: [string, string, string[]][] = [
  ["all", "All", []],
  ["res", "Residential", ["rlow", "rrow", "rmed", "rhigh", "rrent"]],
  ["mix", "Mixed", ["rmix"]],
  ["com", "Commercial", ["clow", "chigh"]],
  ["off", "Office", ["olow", "ohigh"]],
  ["ind", "Industrial", ["iman", "iware"]],
];

/** Find It's zone type filter (same icons, from Unified Icon Library), with low rent as its own type. */
export const ZONE_TYPES: [string, string, string][] = [
  ["", "Any", "coui://uil/Standard/StarAll.svg"],
  ["low", "Low density", "coui://uil/Standard/LowLevel.svg"],
  ["row", "Row housing", "coui://uil/Standard/Row.svg"],
  ["medium", "Medium density", "coui://uil/Standard/MediumLevel.svg"],
  ["high", "High density", "coui://uil/Standard/HighLevel.svg"],
  ["lowrent", "Low rent", lowRentIcon],
];

// ---- Custom buildings (creator assets), chosen by source: a custom zone, a creator's asset pack or one package

export const CUSTOM_ALL = "custom:*";
export const customKey = (source: string) => "custom:" + source;

/** "zone:Aussie Residential" reads "Aussie Residential"; the kind is shown separately. */
export const sourceName = (source: string) => (source.startsWith("other:") ? t("Custom.Other") : source.replace(/^(zone|pack|asset):/, ""));
export const sourceKind = (source: string) =>
  source.startsWith("zone:") ? t("Custom.KindZone") : source.startsWith("pack:") ? t("Custom.KindPack") : t("Custom.KindAsset");

// Display names, in the game's language (the tables above keep the English for reference).
export const useGroupName = (label: string) => t(`UseGroup.${label}`);
export const useName = (k: string) => t(`Use.${k}`);
export const useShortName = (k: string) => (USE_SHORT[k] ? t(`UseShort.${k}`) : k || t("Why.Unknown"));
export const useFilterName = (k: string) => t(`UseFilter.${k}`);
export const zoneTypeName = (k: string) => t(`ZoneType.${k || "any"}`);
export const themeName = (key: string) => (themeDef(key) ? t(`Theme.${key || "Shared"}`) : key);
export const themeRowName = (row: string) => t(`ThemeRow.${row}`);
export const statusLabel = (s: Status) => t(`Status.${s}`);
export const actionLabel = (s: Status) => t(`Action.${s}`);

export interface CustomSource {
  source: string;
  count: number;
  modId: string;
}

/** Installed custom sources with their building counts, custom zones first, then by name. */
export function customSources(buildings: Bldg[]): CustomSource[] {
  const map = new Map<string, CustomSource>();
  buildings.forEach((b) => {
    if (b.origin !== "custom") return;
    const s = map.get(b.source) ?? { source: b.source, count: 0, modId: b.modId };
    s.count++;
    map.set(b.source, s);
  });
  // Custom zones, then asset packs, then single packages; single-building sources ("Other custom assets") last.
  const rank = (s: string) => (s.startsWith("zone:") ? 0 : s.startsWith("pack:") ? 1 : s.startsWith("other:") ? 3 : 2);
  return [...map.values()].sort((a, b) => rank(a.source) - rank(b.source) || sourceName(a.source).localeCompare(sourceName(b.source)));
}

// ---- Themes, in Find It's layout: labelled rows of icon buttons

export interface ThemeDef {
  key: string; // theme key in zones.json ("" = shared)
  full: string;
  code: string;
  icon: string;
  row: "theme" | "pack" | "dlc" | "custom";
}

export const ALL_ICON = allIcon;
export const SHARED_KEY = "";

export const THEMES: ThemeDef[] = [
  { key: SHARED_KEY, full: "Shared: base-game buildings with no theme (generic offices, industry, low rent, plain commercial)", code: "SH", icon: sharedIcon, row: "theme" },
  { key: "NA", full: "North America", code: "NA", icon: "Media/Game/Themes/North American.svg", row: "theme" },
  { key: "NA Waterfront", full: "North America Waterfront", code: "NA", icon: naWaterfront, row: "theme" },
  { key: "NA Old Town", full: "North America Old Town", code: "NA", icon: naOldTown, row: "theme" },
  { key: "EU", full: "Europe", code: "EU", icon: "Media/Game/Themes/European.svg", row: "theme" },
  { key: "EU Waterfront", full: "Europe Waterfront", code: "EU", icon: euWaterfront, row: "theme" },
  { key: "EU Old Town", full: "Europe Old Town", code: "EU", icon: euOldTown, row: "theme" },
  { key: "UK", full: "United Kingdom region pack", code: "UK", icon: packUK, row: "pack" },
  { key: "DE", full: "Germany region pack", code: "DE", icon: packDE, row: "pack" },
  { key: "FR", full: "France region pack", code: "FR", icon: packFR, row: "pack" },
  { key: "NL", full: "Netherlands region pack", code: "NL", icon: packNL, row: "pack" },
  { key: "EE", full: "Eastern Europe region pack", code: "EE", icon: packEE, row: "pack" },
  { key: "JP", full: "Japan region pack", code: "JP", icon: packJP, row: "pack" },
  { key: "CN", full: "China region pack", code: "CN", icon: packCN, row: "pack" },
  { key: "USNE", full: "USA Northeast region pack", code: "NE", icon: packNE, row: "pack" },
  { key: "USSW", full: "USA Southwest region pack", code: "SW", icon: packSW, row: "pack" },
  { key: "UbPr", full: "Urban Promenades", code: "UP", icon: "Media/DLC/UrbanPromenades.svg", row: "dlc" },
  { key: "CCP5", full: "Mediterranean Heritage", code: "MH", icon: "Media/DLC/MediterraneanHeritage.svg", row: "dlc" },
  { key: "Pack9", full: "Office Evolution", code: "OE", icon: "Media/DLC/OfficeEvolution.svg", row: "dlc" },
];

export const THEME_ROWS: { row: ThemeDef["row"]; label: string }[] = [
  { row: "theme", label: "Theme" },
  { row: "pack", label: "Pack" },
  { row: "dlc", label: "DLC" },
];

export const themeDef = (key: string) => THEMES.find((t) => t.key === key);

/** Theme keys that have buildings installed (packs a player lacks are hidden). */
export function installedThemes(buildings: Bldg[]): Set<string> {
  const keys = new Set<string>();
  buildings.forEach((b) => keys.add(b.origin === "custom" ? CUSTOM_ALL : b.theme));
  return keys;
}

/**
 * The set of theme keys a zone allows, as the selector shows it. An empty theme list means any
 * theme, shown as every key on (including Shared).
 */
export function selectedThemes(z: ZoneDef, keys: string[]): Set<string> {
  if (z.themes.length === 0) {
    return new Set(keys);
  }

  const on = new Set(z.themes.filter((t) => !t.startsWith("pack:")));
  if (z.includeShared) on.add(SHARED_KEY);
  return on;
}

/** Whether a custom source is allowed by a selector state (all custom, or that source). */
export const customOn = (on: Set<string>, source: string) => on.has(CUSTOM_ALL) || on.has(customKey(source));

/** Writes a selector state back as themes + includeShared; every key on becomes "any theme". */
export function withThemes(z: ZoneDef, on: Set<string>, keys: string[]): ZoneDef {
  const packs = z.themes.filter((t) => t.startsWith("pack:"));
  const all = keys.every((k) => on.has(k));
  if (all && packs.length === 0) {
    return { ...z, themes: [], includeShared: true };
  }

  const themes = keys.filter((k) => k !== SHARED_KEY && on.has(k));
  // Single custom sources are kept unless all custom buildings are on anyway.
  const sources = on.has(CUSTOM_ALL) ? [] : [...on].filter((k) => k.startsWith("custom:") && !themes.includes(k)).sort();
  return { ...z, themes: [...themes, ...sources, ...packs], includeShared: on.has(SHARED_KEY) };
}

// ---- Rules engine (mirror of BuildingIndex.Matches)

/** Setback class on the narrower side; negative means the model overhangs the lot. */
export const setbackClass = (s: number) => (s < 0.5 ? "attached" : s < 3 ? "narrow" : "detached");

/** Why the zone's rules (ignoring hand-picks) reject a building; null when they accept it. */
export function ruleFailure(z: ZoneDef, b: Bldg): string | null {
  const reasons: string[] = [];
  if (z.uses.length > 0 && !z.uses.includes(b.use)) reasons.push(t("Why.UseIs", { use: useShortName(b.use) }));

  if (z.themes.length > 0) {
    const custom = b.origin === "custom";
    let ok = custom ? z.themes.includes(CUSTOM_ALL) || z.themes.includes(customKey(b.source)) : b.theme === "" ? z.includeShared : z.themes.includes(b.theme);
    if (!ok) ok = b.packs.some((p) => z.themes.includes("pack:" + p));
    if (!ok) reasons.push(custom ? t("Why.CustomNotChosen", { source: sourceName(b.source) }) : b.theme === "" ? t("Why.Shared") : t("Why.ThemeIs", { theme: themeName(b.theme) }));
  }

  if (z.minHeight != null && b.height < z.minHeight) reasons.push(t("Why.TooShort", { h: b.height }));
  if (z.maxHeight != null && b.height > z.maxHeight) reasons.push(t("Why.TooTall", { h: b.height }));
  if ((z.minLotWidth != null && b.lotW < z.minLotWidth) || (z.maxLotWidth != null && b.lotW > z.maxLotWidth)) reasons.push(t("Why.LotWide", { n: b.lotW }));
  if ((z.minLotDepth != null && b.lotD < z.minLotDepth) || (z.maxLotDepth != null && b.lotD > z.maxLotDepth)) reasons.push(t("Why.LotDeep", { n: b.lotD }));

  const exact = z.minSetback != null || z.maxSetback != null;
  if (exact || z.setbacks.length > 0) {
    if (b.setback == null) reasons.push(t("Why.NoSetbackData"));
    else if (exact ? (z.minSetback != null && b.setback < z.minSetback) || (z.maxSetback != null && b.setback >= z.maxSetback) : !z.setbacks.includes(setbackClass(b.setback)))
      reasons.push(t("Why.Setback", { m: b.setback }));
  }

  if (z.parking === "onlot" && !b.parking) reasons.push(t("Why.NoParking"));
  if (z.parking === "none" && b.parking) reasons.push(t("Why.HasParking"));

  return reasons.length > 0 ? reasons.slice(0, 2).join("; ") : null;
}

export type Status = "rule" | "manual" | "excluded" | "nomatch";

export interface Evaluation {
  status: Status;
  why: string;
  allowed: boolean;
}

const listed = (list: string[], b: Bldg) => list.includes(b.name) || list.includes(b.family);

export function evaluate(z: ZoneDef, b: Bldg): Evaluation {
  if (b.signature) return { status: "nomatch", why: t("Why.Signature"), allowed: false };
  const fail = ruleFailure(z, b);
  if (listed(z.exclude, b)) return { status: "excluded", why: fail ? t("Why.ExcludedFail", { why: fail }) : t("Why.ExcludedMatch"), allowed: false };
  if (listed(z.include, b)) return fail ? { status: "manual", why: t("Why.AddedFail", { why: fail }), allowed: true } : { status: "rule", why: t("Why.AddedMatch"), allowed: true };
  return fail ? { status: "nomatch", why: fail, allowed: false } : { status: "rule", why: t("Why.Match"), allowed: true };
}

/** The hand-pick change for a row's action button (Exclude, Remove, Restore or Add). */
export function rowAction(z: ZoneDef, b: Bldg, status: Status): ZoneDef {
  const drop = (list: string[]) => list.filter((n) => n !== b.name && n !== b.family);
  switch (status) {
    case "rule": return { ...z, include: drop(z.include), exclude: [...drop(z.exclude), b.name] };
    case "manual": return { ...z, include: drop(z.include) };
    case "excluded": return { ...z, exclude: drop(z.exclude) };
    default: return { ...z, include: [...drop(z.include), b.name], exclude: drop(z.exclude) };
  }
}

/**
 * A readable name for a growable prefab: "FR_ResidentialMediumRow02_L2_1x2" becomes
 * "FR Residential Medium Row 02" (level and lot have their own columns).
 */
export function prettyName(name: string): string {
  return name
    .replace(/_L[1-5](?=_|$)/, "")
    .replace(/_\d+x\d+$/, "")
    .replace(/_/g, " ")
    .replace(/([a-z])([A-Z0-9])/g, "$1 $2")
    .replace(/([0-9])([A-Za-z])/g, "$1 $2")
    .replace(/([A-Z]+)([A-Z][a-z])/g, "$1 $2")
    .replace(/\s+/g, " ")
    .trim();
}

export type Sign = "eq" | "ne" | "gt" | "lt";

/** Find It's value filter: 0 means any; the sign cycles = ≠ > < ; maxValue counts as "n+". */
export function passesValue(v: number, filter: number, sign: Sign, maxValue: number): boolean {
  if (filter === 0) return true;
  const top = filter === maxValue;
  switch (sign) {
    case "lt": return v < filter;
    case "gt": return v > filter;
    case "ne": return top ? v < maxValue : v !== filter;
    default: return top ? v >= maxValue : v === filter;
  }
}

export const ACTION_LABEL: Record<Status, string> = { rule: "Exclude", manual: "Remove", excluded: "Restore", nomatch: "Add" };
export const STATUS_LABEL: Record<Status, string> = { rule: "Included by rule", manual: "Added by hand", excluded: "Excluded by hand", nomatch: "Not matching" };

// ---- Pending changes

const FIELDS: (keyof ZoneDef)[] = ["name", "uses", "themes", "includeShared", "minHeight", "maxHeight", "minLotWidth", "maxLotWidth", "minLotDepth", "maxLotDepth", "setbacks", "minSetback", "maxSetback", "parking", "preferSameDesign"];

const same = (a: unknown, b: unknown) => JSON.stringify(a ?? null) === JSON.stringify(b ?? null);

/** How many rule changes and hand-picks a draft has over the saved zone. */
export function countChanges(saved: ZoneDef, draft: ZoneDef): number {
  let n = FIELDS.filter((f) => !same(saved[f], draft[f])).length;
  const diff = (a: string[], b: string[]) => a.filter((x) => !b.includes(x)).length + b.filter((x) => !a.includes(x)).length;
  n += diff(saved.include, draft.include) + diff(saved.exclude, draft.exclude);
  return n;
}
