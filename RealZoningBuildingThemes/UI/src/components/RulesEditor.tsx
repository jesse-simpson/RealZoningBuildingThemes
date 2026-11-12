import { useEffect, useMemo, useState } from "react";
import classNames from "classnames";
import { Tooltip } from "cs2/ui";
import {
  ALL_ICON, CUSTOM_ALL, CustomSource, THEME_ROWS, THEMES, USE_GROUPS, ZoneDef, customKey, customOn, selectedThemes, sourceName, themeName, themeRowName, useGroupName, useName, withThemes,
} from "model";
import { TextInput } from "vanilla";
import { t } from "i18n";
import { Checkbox, Chip, IconToggle, RangeSlider, RangeStepper, Segmented, Stepper, Switch } from "./controls";
import customIcon from "../images/custom.svg";
import controls from "./controls.module.scss";
import styles from "./ZonePanel.module.scss";

const SETBACK_CLASSES = ["attached", "narrow", "detached"];

const HEIGHT_MAX = 60; // the slider's top end means "no maximum"

interface Props {
  zone: ZoneDef;
  installed: Set<string>;
  sources: CustomSource[];
  onChange: (z: ZoneDef) => void;
}

/** The Zone rules column, in the mockup's order of prominence. */
export const RulesEditor = ({ zone, installed, sources, onChange }: Props) => {
  // Packs and DLC a player lacks are hidden; the base game themes always show.
  const themes = useMemo(() => THEMES.filter((t) => t.row === "theme" || installed.has(t.key)), [installed]);
  const hasCustom = sources.length > 0;
  const keys = useMemo(() => [...themes.map((t) => t.key), ...(hasCustom ? [CUSTOM_ALL] : [])], [themes, hasCustom]);
  const on = selectedThemes(zone, keys);

  const toggleUse = (u: string) =>
    onChange({ ...zone, uses: zone.uses.includes(u) ? zone.uses.filter((x) => x !== u) : [...zone.uses, u] });

  const setThemes = (next: Set<string>) => onChange(withThemes(zone, next, keys));
  const toggleTheme = (k: string) => {
    const next = new Set(on);
    if (next.has(k)) next.delete(k);
    else next.add(k);
    setThemes(next);
  };
  const toggleRow = (rowKeys: string[]) => {
    const allOn = rowKeys.every((k) => on.has(k));
    const next = new Set(on);
    rowKeys.forEach((k) => (allOn ? next.delete(k) : next.add(k)));
    setThemes(next);
  };

  // Custom buildings: all at once, or source by source.
  const customCount = sources.filter((src) => customOn(on, src.source)).length;
  const clearCustom = (next: Set<string>) => [...next].forEach((k) => k.startsWith("custom:") && next.delete(k));
  const toggleAllCustom = () => {
    const next = new Set(on);
    const wasAll = on.has(CUSTOM_ALL);
    clearCustom(next);
    if (!wasAll) next.add(CUSTOM_ALL);
    setThemes(next);
  };
  const toggleSource = (source: string) => {
    const next = new Set(on);
    const wasOn = customOn(on, source);
    clearCustom(next);
    sources.forEach((src) => {
      const keep = src.source === source ? !wasOn : customOn(on, src.source);
      if (keep) next.add(customKey(src.source));
    });
    if (sources.every((src) => next.has(customKey(src.source)))) {
      clearCustom(next);
      next.add(CUSTOM_ALL);
    }
    setThemes(next);
  };

  const low = zone.minHeight ?? 0;
  const high = zone.maxHeight ?? HEIGHT_MAX;
  // Exact mode is its own state: a range with no ends set must not switch it off.
  const hasRange = zone.minSetback != null || zone.maxSetback != null;
  const [exact, setExact] = useState(hasRange);
  useEffect(() => setExact(hasRange), [zone.id]);
  const showExact = exact || hasRange;
  const toggleClass = (c: string) =>
    onChange({ ...zone, minSetback: null, maxSetback: null, setbacks: zone.setbacks.includes(c) ? zone.setbacks.filter((x) => x !== c) : [...zone.setbacks, c] });

  return (
    <div className={styles.rules}>
      <Section title={t("Rules.Use")}>
        {USE_GROUPS.map((g) => (
          <div key={g.label} className={styles.useGroup}>
            <div className={styles.useLabel}>{useGroupName(g.label)}</div>
            <div className={styles.wrapRow}>
              {g.items.map(([k]) => (
                <Chip key={k} on={zone.uses.includes(k)} onClick={() => toggleUse(k)}>
                  {useName(k)}
                </Chip>
              ))}
            </div>
          </div>
        ))}
        {zone.uses.length === 0 ? <div className={styles.hint}>{t("Rules.NoUse")}</div> : null}
      </Section>

      <Section title={t("Rules.Themes")} aside={zone.themes.length === 0 ? t("Rules.AnyTheme") : t("Rules.ThemesCount", { on: keys.filter((k) => on.has(k)).length + (on.has(CUSTOM_ALL) ? 0 : customCount), all: keys.length })}>
        {THEME_ROWS.map(({ row }) => {
          const defs = themes.filter((d) => d.row === row);
          if (defs.length === 0) return null;
          const rowKeys = defs.map((d) => d.key);
          return (
            <div key={row} className={styles.themeRow}>
              <div className={styles.themeRowLabel}>{themeRowName(row)}</div>
              <div className={styles.wrapRow}>
                <IconToggle on={rowKeys.every((k) => on.has(k))} isAll src={ALL_ICON} label={t("Rules.AllInRow")} onClick={() => toggleRow(rowKeys)} />
                {defs.map((d) => (
                  <IconToggle key={d.key} on={on.has(d.key)} src={d.icon} label={themeName(d.key)} onClick={() => toggleTheme(d.key)} />
                ))}
              </div>
            </div>
          );
        })}
        {hasCustom ? <CustomRow sources={sources} on={on} count={customCount} onAll={toggleAllCustom} onSource={toggleSource} /> : null}
      </Section>

      <Section title={t("Rules.Shape")}>
        <div className={styles.heightRow}>
          <div className={styles.optionRowTight}>
            <div className={styles.optionLabel}>{t("Rules.Height")}</div>
            <div className={styles.heightValue}>{heightText(zone.minHeight, zone.maxHeight)}</div>
          </div>
          <RangeSlider
            min={0}
            max={HEIGHT_MAX}
            low={low}
            high={high}
            onChange={(l, h) => onChange({ ...zone, minHeight: l <= 0 ? null : l, maxHeight: h >= HEIGHT_MAX ? null : h })}
          />
        </div>
        <LotRange label={t("Rules.LotWidth")} tip={t("Rules.LotWidthTip")} min={zone.minLotWidth} max={zone.maxLotWidth} onChange={(a, b) => onChange({ ...zone, minLotWidth: a, maxLotWidth: b })} />
        <LotRange label={t("Rules.LotDepth")} tip={t("Rules.LotDepthTip")} min={zone.minLotDepth} max={zone.maxLotDepth} onChange={(a, b) => onChange({ ...zone, minLotDepth: a, maxLotDepth: b })} />
        <div className={styles.optionRow}>
          <Tooltip tooltip={t("Rules.SetbackTip")}>
            <div className={styles.optionLabel}>{t("Rules.Setback")}</div>
          </Tooltip>
          <div className={classNames(controls.segmented, controls.segmentedSmall)}>
            {SETBACK_CLASSES.map((c) => (
              <Tooltip key={c} tooltip={t(`Setback.${c}Tip`)}>
                <button className={classNames(controls.segment, !showExact && zone.setbacks.includes(c) && controls.segmentOn)} onClick={() => { setExact(false); toggleClass(c); }}>
                  {t(`Setback.${c}`)}
                </button>
              </Tooltip>
            ))}
            <button
              className={classNames(controls.segment, !showExact && zone.setbacks.length === 0 && controls.segmentOn)}
              onClick={() => { setExact(false); onChange({ ...zone, setbacks: [], minSetback: null, maxSetback: null }); }}
            >
              {t("Common.Any")}
            </button>
            <Tooltip tooltip={t("Rules.ExactTip")}>
              <button
                className={classNames(controls.segment, showExact && controls.segmentOn)}
                onClick={() => { setExact(true); onChange({ ...zone, setbacks: [], minSetback: zone.minSetback ?? 0.5, maxSetback: zone.maxSetback ?? null }); }}
              >
                {t("Rules.Exact")}
              </button>
            </Tooltip>
          </div>
        </div>
        {showExact ? (
          <div className={styles.optionRow}>
            <div className={styles.optionLabelDim}>{t("Rules.From")}</div>
            <div className={styles.row}>
              <Stepper value={zone.minSetback ?? -2.5} step={0.5} min={-2.5} max={20} format={(v) => (v <= -2.5 ? t("Rules.NoMin") : t("Rules.Metres", { n: v }))} onChange={(v) => onChange({ ...zone, minSetback: v <= -2.5 ? null : v })} />
              <span className={styles.rangeDash}>{t("Rules.ToUnder")}</span>
              <Stepper value={zone.maxSetback ?? 20} step={0.5} min={-2} max={20} format={(v) => (v >= 20 ? t("Rules.NoMax") : t("Rules.Metres", { n: v }))} onChange={(v) => onChange({ ...zone, maxSetback: v >= 20 ? null : v })} />
            </div>
          </div>
        ) : null}
        <div className={styles.optionRow}>
          <Tooltip tooltip={t("Rules.ParkingTip")}>
            <div className={styles.optionLabel}>{t("Rules.Parking")}</div>
          </Tooltip>
          <Segmented
            small
            value={zone.parking}
            options={[
              ["none", t("Parking.None"), t("Parking.NoneTip")],
              ["onlot", t("Parking.Yes"), t("Parking.YesTip")],
              ["any", t("Common.Any"), t("Parking.AnyTip")],
            ]}
            onChange={(v) => onChange({ ...zone, parking: v })}
          />
        </div>
      </Section>

      <div className={styles.optionRow}>
        <div className={styles.optionLabel}>{t("Rules.PreferSame")}</div>
        <Switch on={zone.preferSameDesign} onChange={(v) => onChange({ ...zone, preferSameDesign: v })} />
      </div>
    </div>
  );
};

const heightText = (min?: number | null, max?: number | null) =>
  min != null && max != null ? t("Height.Range", { min, max }) : min != null ? t("Height.Min", { min }) : max != null ? t("Height.Max", { max }) : t("Height.Any");

export const Section = ({ title, aside, children }: { title: string; aside?: string; children: React.ReactNode }) => (
  <div className={styles.section}>
    <div className={styles.sectionHeader}>
      <span>{title}</span>
      {aside ? <span className={styles.sectionAside}>{aside}</span> : null}
    </div>
    <div className={styles.sectionBody}>{children}</div>
  </div>
);

/**
 * Custom buildings in the theme section: one button for all of them, and a searchable list of
 * their sources (custom zones, creator asset packs, single assets) to pick from.
 */
const CustomRow = ({ sources, on, count, onAll, onSource }: { sources: CustomSource[]; on: Set<string>; count: number; onAll: () => void; onSource: (s: string) => void }) => {
  const [open, setOpen] = useState(false);
  const [search, setSearch] = useState("");
  const total = sources.reduce((n, s) => n + s.count, 0);
  const q = search.trim().toLowerCase();
  const shown = q === "" ? sources : sources.filter((s) => sourceName(s.source).toLowerCase().includes(q) || s.modId.includes(q));
  return (
    <div>
      <div className={styles.themeRow}>
        <div className={styles.themeRowLabel}>{themeRowName("custom")}</div>
        <div className={styles.wrapRow}>
          <IconToggle on={on.has(CUSTOM_ALL)} src={customIcon} onClick={onAll} />
          <button className={styles.customToggle} onClick={() => setOpen(!open)}>
            {t("Custom.Sources", { count: count === sources.length ? t("Custom.All") : count, total: sources.length, buildings: total })}
            <img className={styles.customArrow} src={open ? "Media/Glyphs/ThickStrokeArrowUp.svg" : "Media/Glyphs/ThickStrokeArrowDown.svg"} />
          </button>
        </div>
      </div>
      {open ? (
        <div className={styles.customList}>
          <TextInput className={styles.customSearch} value={search} placeholder={t("Custom.Search")} onChange={(e) => setSearch(e.target.value)} />
          {shown.map((s) => (
            <div key={s.source} className={styles.customItem} onClick={() => onSource(s.source)}>
              <Checkbox on={customOn(on, s.source)} onChange={() => undefined /* the row's click toggles it */} />
              <div className={styles.customItemName}>{sourceName(s.source)}</div>
            </div>
          ))}
          {shown.length === 0 ? <div className={styles.hint}>{t("Custom.NoMatch")}</div> : null}
        </div>
      ) : null}
    </div>
  );
};

const LOT_MAX = 10;

/** A lot size range in cells (Find It's arrows at each end); "Any" means no limit on that end. */
const LotRange = ({ label, tip, min, max, onChange }: { label: string; tip: string; min?: number | null; max?: number | null; onChange: (min: number | null, max: number | null) => void }) => (
  <div className={styles.optionRow}>
    <Tooltip tooltip={tip}>
      <div className={styles.optionLabel}>{label}</div>
    </Tooltip>
    <RangeStepper min={min} max={max} limit={LOT_MAX} onChange={onChange} />
  </div>
);
