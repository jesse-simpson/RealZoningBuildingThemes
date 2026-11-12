import { bindLocalValue, bindValue, trigger } from "cs2/api";
import { ZoneDef } from "model";

/** Binding group shared with ZonePanelUISystem (C#). */
export const GROUP = "rzbt";

/** One zone as ZonePanelUISystem sends it: the zones.json definition plus runtime facts. */
export interface ZoneEntry {
  def: ZoneDef;
  /** False when the zone was added to zones.json after the game started (needs a restart). */
  active: boolean;
  color: string | null;
  icon: string | null;
  /** The game zone this zone was created from (its settings, toolbar order and rezoning fallback). */
  baseName: string | null;
  /** Null until a city has loaded. */
  allowed: number | null;
  levelOne: number | null;
}

export const zonesJson$ = bindValue<string>(GROUP, "zones", "[]");
export const buildingsJson$ = bindValue<string>(GROUP, "buildings", '{"thumb":null,"rows":[]}');

export function parseZones(json: string): ZoneEntry[] {
  try {
    const zones = JSON.parse(json) as ZoneEntry[];
    zones.forEach((z) => (z.def.setbacks = z.def.setbacks ?? []));
    return zones;
  } catch {
    return [];
  }
}


/** Saves one zone's definition to zones.json and re-applies the rules (the removal pass follows). */
export const applyZone = (def: ZoneDef) => trigger(GROUP, "applyZone", JSON.stringify(def));

/** One line on the last rezoning (retire, prepare for uninstall), from the game side. */
export const notice$ = bindValue<string>(GROUP, "notice", "");

export const duplicateZone = (id: string) => trigger(GROUP, "duplicateZone", id);
/** Rezones this city's cells of the zone to game zones, then takes the zone out of the toolbar. */
export const retireZone = (id: string) => trigger(GROUP, "retireZone", id);
export const restoreZone = (id: string) => trigger(GROUP, "restoreZone", id);
/** Deletes a retired zone: hidden for good, but kept registered so other cities that use it still load. */
export const deleteZone = (id: string) => trigger(GROUP, "deleteZone", id);
/** Counts presses of the panel's keyboard shortcut (Options page); each change toggles the panel. */
export const togglePanel$ = bindValue<number>(GROUP, "togglePanel", 0);

/** Creates a zone in zones.json and in the running game; it can be painted a moment later. */
export const createZone = (name: string, use: string) => trigger(GROUP, "createZone", name, use);

/** Reports thumbnails that failed to load, so the log can say what the game gave for them. Batched. */
let missing: string[] = [];
let missingTimer: number | null = null;
export const reportMissingThumb = (name: string) => {
  missing.push(name);
  if (missingTimer != null) return;
  missingTimer = window.setTimeout(() => {
    trigger(GROUP, "missingThumbs", JSON.stringify(missing));
    missing = [];
    missingTimer = null;
  }, 2000);
};

/** Panel open state, shared by the toolbar button and the panel (UI only, not saved). */
export const panelOpen$ = bindLocalValue(false);
