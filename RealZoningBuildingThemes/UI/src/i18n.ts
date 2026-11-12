// Translation. Every panel string has a key; the English text lives in l10n/en-US.json (bundled
// as the fallback). The mod's C# side loads l10n/<locale>.json into the game's localization under
// "RZBT.<key>", so the panel follows the game's language, and translation mods can add more.

import { useLocalization } from "cs2/l10n";
import EN from "../l10n/en-US.json";

type Translate = (id: string, fallback?: string | null) => string | null;

let translate: Translate | null = null;
const english = EN as Record<string, string>;

/** Call once at the top of each root component, so t() follows the game's language. */
export function useI18n() {
  const loc = useLocalization();
  translate = loc.translate as Translate;
  return loc;
}

/** The string for a key in the game's language (English if missing), with {name} placeholders filled. */
export function t(key: string, vars?: Record<string, string | number>): string {
  let text = translate?.(`RZBT.${key}`, null) ?? english[key] ?? key;
  if (vars) {
    for (const name of Object.keys(vars)) {
      text = text.split(`{${name}}`).join(String(vars[name]));
    }
  }
  return text;
}

/** "1 building" against "5 buildings": picks key.One or key.Other by n, with {n} filled. */
export function tn(key: string, n: number, vars?: Record<string, string | number>): string {
  return t(n === 1 ? `${key}.One` : `${key}.Other`, { n, ...vars });
}
