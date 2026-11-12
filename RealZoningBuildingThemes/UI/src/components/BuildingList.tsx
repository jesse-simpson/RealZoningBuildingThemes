import { useMemo, useState } from "react";
import classNames from "classnames";
import { Scrollable, Tooltip } from "cs2/ui";
import { useLocalization } from "cs2/l10n";
import {
  ACTION_LABEL, ALL_ICON, Bldg, CUSTOM_ALL, Evaluation, PLACEHOLDER, STATUS_LABEL, Sign, Status, THEMES, USE_FILTERS, USE_SHORT, ZONE_TYPES, ZoneDef, evaluate, passesValue,
  actionLabel, prettyName, rowAction, sourceKind, sourceName, statusLabel, themeDef, themeName, useFilterName, useShortName, zoneTypeName,
} from "model";
import { reportMissingThumb } from "bindings";
import { t } from "i18n";
import { TextInput, ToolButton, toolButtonTheme } from "vanilla";
import { Checkbox, IconToggle, Picker, RangeStepper, Segmented, ValueFilter } from "./controls";
import searchIcon from "../images/search.svg";
import customIcon from "../images/custom.svg";
import styles from "./BuildingList.module.scss";

const PAGE = 60;

/** A lot dimension within a [min, max] filter; null ends are open. */
const inRange = (v: number, [min, max]: [number | null, number | null]) => (min == null || v >= min) && (max == null || v <= max);
const STATUS_ORDER: Record<Status, number> = { manual: 0, rule: 1, excluded: 2, nomatch: 3 };

interface Props {
  zone: ZoneDef;
  buildings: Bldg[];
  installed: Set<string>;
  onChange: (z: ZoneDef) => void;
}

/**
 * The building list, always open beside the zone rules. View filters (use, theme, lot width and
 * depth, level, search, status) only change what is shown, never the zone; the row buttons and
 * bulk Include/Exclude change the zone's hand-picks (held until Apply).
 */
export const BuildingList = ({ zone, buildings, installed, onChange }: Props) => {
  const { translate } = useLocalization();
  const [search, setSearch] = useState("");
  const [status, setStatus] = useState<"all" | Status>("all");
  const [useFilter, setUseFilter] = useState("all");
  const [zoneType, setZoneType] = useState("");
  const [brokenThumbs, setBrokenThumbs] = useState<Set<string>>(new Set());
  const [themeFilter, setThemeFilter] = useState<string[]>([]);
  const [width, setWidth] = useState<[number | null, number | null]>([null, null]);
  const [depth, setDepth] = useState<[number | null, number | null]>([null, null]);
  const [level, setLevel] = useState<[number, Sign]>([0, "eq"]);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [limit, setLimit] = useState(PAGE);

  // The game has no display names for most growables; fall back to a readable prefab name.
  const displayName = (b: Bldg) => translate(`Assets.NAME[${b.name}]`, null) ?? prettyName(b.name);

  const evaluated = useMemo(() => buildings.map((b) => ({ b, e: evaluate(zone, b) })), [buildings, zone]);

  const viewable = useMemo(() => {
    const uses = USE_FILTERS.find(([k]) => k === useFilter)?.[2] ?? [];
    const q = search.trim().toLowerCase();
    return evaluated.filter(({ b }) =>
      (uses.length === 0 || uses.includes(b.use)) &&
      (zoneType === "" || b.zoneType === zoneType) &&
      (themeFilter.length === 0 || (b.origin === "custom" ? themeFilter.includes(CUSTOM_ALL) : themeFilter.includes(b.theme))) &&
      inRange(b.lotW, width) &&
      inRange(b.lotD, depth) &&
      passesValue(b.level, level[0], level[1], 5) &&
      (q === "" || b.name.toLowerCase().includes(q) || displayName(b).toLowerCase().includes(q) || (b.origin === "custom" && sourceName(b.source).toLowerCase().includes(q))),
    );
  }, [evaluated, useFilter, zoneType, themeFilter, width, depth, level, search]);

  const counts = useMemo(() => {
    const c: Record<string, number> = { all: viewable.length, rule: 0, manual: 0, excluded: 0, nomatch: 0 };
    viewable.forEach(({ e }) => c[e.status]++);
    return c;
  }, [viewable]);

  const shown = useMemo(
    () => viewable.filter(({ e }) => status === "all" || e.status === status).sort((a, b) => STATUS_ORDER[a.e.status] - STATUS_ORDER[b.e.status] || a.b.name.localeCompare(b.b.name)),
    [viewable, status],
  );

  const resetPaging = () => {
    setLimit(PAGE);
    setSelected(new Set());
  };

  const act = (b: Bldg, e: Evaluation) => onChange(rowAction(zone, b, e.status));
  const bulk = (include: boolean) => {
    let z = zone;
    shown.forEach(({ b, e }) => {
      if (!selected.has(b.name)) return;
      if (include && !e.allowed) z = rowAction(z, b, e.status === "excluded" ? "excluded" : "nomatch");
      if (!include && e.allowed) z = rowAction(z, b, e.status === "manual" ? "manual" : "rule");
    });
    onChange(z);
    setSelected(new Set());
  };
  const allChecked = shown.length > 0 && shown.every(({ b }) => selected.has(b.name));
  const visibleThemes = [
    ...THEMES.filter((d) => installed.has(d.key)),
    ...(installed.has(CUSTOM_ALL) ? [{ key: CUSTOM_ALL, full: "", code: "CU", icon: customIcon, row: "custom" as const }] : []),
  ];
  const tb = toolButtonTheme();
  const thumbFailed = (b: Bldg) => {
    if (brokenThumbs.has(b.name)) return;
    setBrokenThumbs(new Set(brokenThumbs).add(b.name));
    reportMissingThumb(b.name);
  };
  const noneSelected = selected.size === 0;

  return (
    <div className={styles.list}>
      <div className={styles.toolbar}>
        <div className={styles.title}>{t("List.Title")}</div>
        <div className={styles.titleCounts}>{t("List.Counts", { allowed: counts.rule + counts.manual, manual: counts.manual, excluded: counts.excluded })}</div>
        <div className={styles.flex} />
        <div className={styles.search}>
          <img src={searchIcon} />
          <TextInput className={styles.searchInput} value={search} placeholder={t("List.Search")} onChange={(e) => { setSearch(e.target.value); resetPaging(); }} />
        </div>
      </div>

      <div className={styles.filters}>
        <div className={styles.filterRow}>
          <div className={styles.filterLabel}>{t("List.Use")}</div>
          <Segmented small value={useFilter} options={USE_FILTERS.map(([k]) => [k, useFilterName(k)] as [string, string])} onChange={(v) => { setUseFilter(v); resetPaging(); }} />
          <div className={styles.filterLabelInline}>{t("List.ZoneType")}</div>
          <div className={styles.zoneTypes}>
            {ZONE_TYPES.map(([k, , icon]) => (
              <ToolButton
                key={k || "any"}
                className={classNames(tb.button, styles.zoneTypeButton)}
                src={icon}
                selected={zoneType === k}
                tooltip={k === "" ? zoneTypeName(k) : t("List.ZoneTypeTip", { type: zoneTypeName(k) })}
                onSelect={() => { setZoneType(k); resetPaging(); }}
              />
            ))}
          </div>
        </div>
        <div className={styles.filterRow}>
          <div className={styles.filterLabel}>{t("List.Theme")}</div>
          <div className={styles.themeFilters}>
            <IconToggle on={themeFilter.length === 0} isAll src={ALL_ICON} label={t("List.AllThemes")} onClick={() => { setThemeFilter([]); resetPaging(); }} />
            {visibleThemes.map((th, i) => (
              <div key={th.key} className={classNames(styles.themeFilterItem, i > 0 && visibleThemes[i - 1].row !== th.row && styles.themeSep)}>
                <IconToggle
                  on={themeFilter.includes(th.key)}
                  src={th.icon}
                  label={th.full ? themeName(th.key) : ""}
                  onClick={() => { setThemeFilter(themeFilter.includes(th.key) ? themeFilter.filter((k) => k !== th.key) : [...themeFilter, th.key]); resetPaging(); }}
                />
              </div>
            ))}
          </div>
        </div>
        <div className={styles.filterRow}>
          <div className={styles.filterLabel}>{t("List.LotWidth")}</div>
          <RangeStepper min={width[0]} max={width[1]} limit={10} onChange={(a, b) => { setWidth([a, b]); resetPaging(); }} />
          <div className={styles.filterLabelInline}>{t("List.LotDepth")}</div>
          <RangeStepper min={depth[0]} max={depth[1]} limit={10} onChange={(a, b) => { setDepth([a, b]); resetPaging(); }} />
          <div className={styles.filterLabelInline}>{t("List.Level")}</div>
          <ValueFilter value={level[0]} sign={level[1]} max={5} onChange={(v, s) => { setLevel([v, s]); resetPaging(); }} />
        </div>
      </div>

      <div className={styles.bulk}>
        <Checkbox on={allChecked} onChange={(v) => setSelected(v ? new Set(shown.map(({ b }) => b.name)) : new Set())} />
        <span className={styles.bulkLabel}>{t("List.SelectAll", { n: shown.length })}</span>
        <span className={styles.bulkCount}>{noneSelected ? "" : t("List.Selected", { n: selected.size })}</span>
        <button className={classNames(styles.bulkButton, noneSelected && styles.bulkButtonOff)} onClick={() => !noneSelected && bulk(true)}>{t("List.Include")}</button>
        <button className={classNames(styles.bulkButton, noneSelected && styles.bulkButtonOff)} onClick={() => !noneSelected && bulk(false)}>{t("List.Exclude")}</button>
      </div>

      <div className={styles.header}>
        <span className={styles.colCheck} />
        <span className={styles.colThumb} />
        <span className={styles.colName}>{t("List.ColBuilding")}</span>
        <span className={styles.colTheme}>{t("List.ColTheme")}</span>
        <span className={styles.colUse}>{t("List.ColUse")}</span>
        <span className={styles.colLevel}>{t("List.ColLevel")}</span>
        <span className={styles.colLot}>{t("List.ColLot")}</span>
        <span className={styles.colHeight}>{t("List.ColHeight")}</span>
        <span className={styles.colStatus}>
          <Picker
            flush
            value={status}
            options={[
              ["all", t("List.StatusAll", { n: counts.all })],
              ["rule", t("List.StatusRule", { n: counts.rule })],
              ["manual", t("List.StatusManual", { n: counts.manual })],
              ["excluded", t("List.StatusExcluded", { n: counts.excluded })],
              ["nomatch", t("List.StatusNomatch", { n: counts.nomatch })],
            ]}
            onChange={(v) => { setStatus(v); resetPaging(); }}
          />
        </span>
        <span className={styles.colAction} />
      </div>

      <Scrollable className={styles.rows} vertical trackVisibility="scrollable">
        {buildings.length === 0 ? <div className={styles.empty}>{t("List.LoadCity")}</div> : null}
        {buildings.length > 0 && shown.length === 0 ? <div className={styles.empty}>{t("List.NoMatch")}</div> : null}
        {shown.slice(0, limit).map(({ b, e }, i) => {
          const td = themeDef(b.theme);
          return (
            <div key={b.name} className={classNames(styles.row, i % 2 === 1 && styles.rowAlt, !e.allowed && styles.rowDim, selected.has(b.name) && styles.rowSelected)}>
              <span className={styles.colCheck}>
                <Checkbox on={selected.has(b.name)} onChange={(v) => { const s = new Set(selected); v ? s.add(b.name) : s.delete(b.name); setSelected(s); }} />
              </span>
              <span className={styles.colThumb}>
                <img className={styles.thumb} src={brokenThumbs.has(b.name) ? PLACEHOLDER : b.thumb} onError={() => thumbFailed(b)} />
              </span>
              <span className={styles.colName}>
                <span className={styles.name}>{displayName(b)}</span>
                <span className={styles.why}>{e.why}</span>
              </span>
              <span className={styles.colTheme}>
                {b.origin === "custom" ? (
                  <Tooltip tooltip={t("List.SourceTip", { kind: sourceKind(b.source), name: sourceName(b.source) }) + (b.modId ? t("List.ModId", { id: b.modId }) : "")}>
                    <img className={styles.themeIcon} src={customIcon} />
                  </Tooltip>
                ) : td ? (
                  <Tooltip tooltip={themeName(td.key)}>
                    <img className={styles.themeIcon} src={td.icon} />
                  </Tooltip>
                ) : (
                  <span className={styles.themeCode}>{b.theme || "?"}</span>
                )}
              </span>
              <span className={styles.colUse}>{useShortName(b.use)}</span>
              <span className={styles.colLevel}>{`${b.level}`}</span>
              <span className={styles.colLot}>{`${b.lotW}×${b.lotD}`}</span>
              <span className={styles.colHeight}>{t("List.Height", { n: Math.round(b.height) })}</span>
              <span className={styles.colStatus}>
                <span className={classNames(styles.badge, styles[`badge_${e.status}`])}>{statusLabel(e.status)}</span>
              </span>
              <span className={styles.colAction}>
                <button className={styles.action} onClick={() => act(b, e)}>
                  {actionLabel(e.status)}
                </button>
              </span>
            </div>
          );
        })}
        {shown.length > limit ? (
          <button className={styles.more} onClick={() => setLimit(limit + PAGE * 2)}>
            {t("List.More", { n: shown.length - limit })}
          </button>
        ) : null}
      </Scrollable>
    </div>
  );
};
