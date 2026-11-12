import { useEffect, useMemo, useRef, useState } from "react";
import classNames from "classnames";
import { useValue } from "cs2/api";
import { Button, Panel, Scrollable, Tooltip } from "cs2/ui";
import { InputActionConsumer } from "cs2/input";
import {
  applyZone, buildingsJson$, createZone, deleteZone, duplicateZone, notice$, panelOpen$, parseZones, restoreZone, retireZone, ZoneEntry, zonesJson$,
} from "bindings";
import { Bldg, CustomSource, USE_GROUPS, ZoneDef, countChanges, customSources, evaluate, installedThemes, parseBuildings, useGroupName, useName } from "model";
import { t, tn, useI18n } from "i18n";
import { TextInput } from "vanilla";
import { Picker } from "./controls";
import { RulesEditor } from "./RulesEditor";
import { BuildingList } from "./BuildingList";
import closeIcon from "../images/close.svg";
import styles from "./ZonePanel.module.scss";

const useOptions = (): [string, string][] => USE_GROUPS.flatMap((g) => g.items.map(([k]) => [k, `${useGroupName(g.label)}: ${useName(k)}`] as [string, string]));

/**
 * The zone panel: the zone list on the left, the selected zone's rules in the middle and its
 * building list (always open) on the right. Edits are held as a draft per zone until Apply, which
 * saves zones.json and re-applies the rules (standing buildings the zone no longer allows are
 * then replaced). New zones are created at once and can be painted straight away.
 */
export const ZonePanel = () => {
  useI18n();
  const open = useValue(panelOpen$);
  const zonesJson = useValue(zonesJson$);
  const buildingsJson = useValue(buildingsJson$);
  const zones = useMemo(() => parseZones(zonesJson), [zonesJson]);
  // Signature buildings never grow from zoning, so they are not offered.
  const buildings = useMemo(() => parseBuildings(buildingsJson).filter((b) => !b.signature), [buildingsJson]);
  const installed = useMemo(() => installedThemes(buildings), [buildings]);
  const sources = useMemo(() => customSources(buildings), [buildings]);

  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [drafts, setDrafts] = useState<Record<string, ZoneDef>>({});
  const knownIds = useRef<Set<string>>(new Set());

  const activeZones = useMemo(() => zones.filter((z) => !z.def.retired), [zones]);

  useEffect(() => {
    // Select a zone that has just been created; otherwise keep the selection on an active zone.
    const fresh = zones.find((z) => knownIds.current.size > 0 && !knownIds.current.has(z.def.id));
    knownIds.current = new Set(zones.map((z) => z.def.id));
    if (fresh) setSelectedId(fresh.def.id);
    else if (!activeZones.some((z) => z.def.id === selectedId)) setSelectedId(activeZones.length > 0 ? activeZones[0].def.id : null);
  }, [zones]);

  if (!open) return null;

  const entry = activeZones.find((z) => z.def.id === selectedId) ?? null;
  const draft = entry ? drafts[entry.def.id] ?? entry.def : null;
  const setDraft = (z: ZoneDef) => setDrafts({ ...drafts, [z.id]: z });
  const discard = (id: string) => {
    const next = { ...drafts };
    delete next[id];
    setDrafts(next);
  };

  const close = () => panelOpen$.update(false);

  // Esc (the game's Back action) closes the panel instead of opening the pause menu, as the game's
  // own panels do; the panel's onClose covers the case where it has the game's input focus.
  return (
    <Panel className={styles.panel} onClose={close}>
      <InputActionConsumer actions={{ Back: close }} ignoreFocusState />
      <div className={styles.titleBar}>
        <div className={styles.titleSpace} />
        <div className={styles.title}>{t("Panel.Title")}</div>
        <Tooltip tooltip={t("Panel.Close")}>
          <button className={styles.close} onClick={close}>
            <img src={closeIcon} />
          </button>
        </Tooltip>
      </div>

      <div className={styles.body}>
        <ZoneList zones={zones} drafts={drafts} selectedId={selectedId} onSelect={setSelectedId} />

        {entry && draft ? (
          <ZoneWorkspace
            key={entry.def.id}
            entry={entry}
            draft={draft}
            buildings={buildings}
            installed={installed}
            sources={sources}
            onChange={setDraft}
            onDiscard={() => discard(entry.def.id)}
            onApply={() => {
              applyZone(draft);
              discard(entry.def.id);
            }}
          />
        ) : (
          <div className={styles.emptyWorkspace}>{t("Panel.NoZones")}</div>
        )}
      </div>
    </Panel>
  );
};

const ZoneIcon = ({ entry, large }: { entry: ZoneEntry; large?: boolean }) => (
  <div className={classNames(styles.zoneIcon, large && styles.zoneIconLarge)}>
    {entry.icon ? <img src={entry.icon} /> : null}
    <div className={styles.swatch} style={{ backgroundColor: entry.color ?? "transparent" }} />
  </div>
);

const ZoneList = ({ zones, drafts, selectedId, onSelect }: { zones: ZoneEntry[]; drafts: Record<string, ZoneDef>; selectedId: string | null; onSelect: (id: string) => void }) => {
  const active = zones.filter((z) => !z.def.retired);
  const retired = zones.filter((z) => z.def.retired);
  const [showRetired, setShowRetired] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null);
  const notice = useValue(notice$);
  const [creating, setCreating] = useState(false);
  const [name, setName] = useState("");
  const [use, setUse] = useState("rmed");

  const create = () => {
    createZone(name.trim() || t("Zones.NewName"), use);
    setCreating(false);
    setName("");
  };

  return (
    <div className={styles.zoneColumn}>
      <div className={styles.zoneColumnHeader}>
        <div className={styles.columnHeader}>{t("Zones.Header", { n: active.length })}</div>
        <button className={styles.newZone} onClick={() => setCreating(!creating)}>
          {creating ? t("Common.Cancel") : t("Zones.New")}
        </button>
      </div>
      {creating ? (
        <div className={styles.creator}>
          <div className={styles.creatorLabel}>{t("Zones.Name")}</div>
          <TextInput className={styles.textField} value={name} placeholder={t("Zones.NewName")} onChange={(e) => setName(e.target.value)} />
          <Tooltip tooltip={t("Zones.StartsAsTip")}>
            <div className={styles.creatorLabel}>{t("Zones.StartsAs")}</div>
          </Tooltip>
          <Picker plain className={styles.creatorPicker} value={use} options={useOptions()} onChange={setUse} />
          <button className={styles.createButton} onClick={create}>
            {t("Zones.Create")}
          </button>
        </div>
      ) : null}
      <Scrollable className={styles.zoneList} vertical trackVisibility="scrollable">
        {active.map((z) => (
          <button key={z.def.id} className={classNames(styles.zoneRow, z.def.id === selectedId && styles.zoneRowOn)} onClick={() => onSelect(z.def.id)}>
            <ZoneIcon entry={z} />
            <div className={styles.zoneRowText}>
              <div className={styles.zoneRowName}>{(drafts[z.def.id] ?? z.def).name}</div>
              <div className={styles.zoneRowMeta}>
                {(!z.active ? t("Zones.BeingAdded") : z.allowed == null ? t("Zones.LoadCity") : tn("Zones.Buildings", z.allowed)) + (drafts[z.def.id] ? t("Zones.Unsaved") : "")}
              </div>
            </div>
          </button>
        ))}
        {retired.length > 0 ? (
          <button className={styles.retiredHeader} onClick={() => setShowRetired(!showRetired)}>
            {t("Zones.Retired", { n: retired.length })}
            <img className={styles.customArrow} src={showRetired ? "Media/Glyphs/ThickStrokeArrowUp.svg" : "Media/Glyphs/ThickStrokeArrowDown.svg"} />
          </button>
        ) : null}
        {showRetired
          ? retired.map((z) => (
              <div key={z.def.id} className={styles.retiredRow}>
                <div className={styles.retiredName}>{z.def.name}</div>
                {confirmDelete === z.def.id ? (
                  <div className={styles.confirm}>
                    <div className={styles.confirmText}>
                      {t("Zones.DeleteConfirm")}
                    </div>
                    <div className={styles.confirmButtons}>
                      <button className={styles.smallButton} onClick={() => setConfirmDelete(null)}>{t("Common.Cancel")}</button>
                      <button className={classNames(styles.smallButton, styles.dangerButton)} onClick={() => { deleteZone(z.def.id); setConfirmDelete(null); }}>{t("Zones.Delete")}</button>
                    </div>
                  </div>
                ) : (
                  <div className={styles.confirmButtons}>
                    <button className={styles.smallButton} onClick={() => restoreZone(z.def.id)}>{t("Zones.Restore")}</button>
                    <button className={styles.smallButton} onClick={() => setConfirmDelete(z.def.id)}>{t("Zones.Delete")}</button>
                  </div>
                )}
              </div>
            ))
          : null}
      </Scrollable>
      {notice ? (
        <div className={styles.zoneFooter}>
          <div className={styles.notice}>{noticeText(notice)}</div>
        </div>
      ) : null}
    </div>
  );
};

interface WorkspaceProps {
  entry: ZoneEntry;
  draft: ZoneDef;
  buildings: Bldg[];
  installed: Set<string>;
  sources: CustomSource[];
  onChange: (z: ZoneDef) => void;
  onDiscard: () => void;
  onApply: () => void;
}

const ZoneWorkspace = ({ entry, draft, buildings, installed, sources, onChange, onDiscard, onApply }: WorkspaceProps) => {
  // Live counts for the draft, from the same rules the game side applies.
  // Live counts for the draft, and how it differs from the applied zone, from the same rules the
  // game side applies.
  const stats = useMemo(() => {
    const s = { allowed: 0, levelOne: 0, added: 0, removed: 0 };
    const unchanged = draft === entry.def;
    buildings.forEach((b) => {
      const now = evaluate(draft, b).allowed;
      if (now) {
        s.allowed++;
        if (b.level === 1) s.levelOne++;
      }
      if (!unchanged) {
        const before = evaluate(entry.def, b).allowed;
        if (now && !before) s.added++;
        if (!now && before) s.removed++;
      }
    });
    return s;
  }, [draft, entry.def, buildings]);

  const changes = countChanges(entry.def, draft);
  const [confirmRetire, setConfirmRetire] = useState(false);

  return (
    <div className={styles.workspace}>
      <div className={styles.workspaceMain}>
        <div className={styles.rulesColumn}>
          <div className={styles.zoneHeader}>
            <ZoneIcon entry={entry} large />
            <div className={styles.zoneHeaderText}>
              <TextInput className={styles.nameField} value={draft.name} placeholder={t("Zone.NamePlaceholder")} onChange={(e) => onChange({ ...draft, name: e.target.value })} />
              <div className={styles.zoneCounts}>
                {buildings.length === 0
                  ? t("Zone.CountsNoCity")
                  : t("Zone.Counts", { allowed: stats.allowed, levelOne: stats.levelOne })}
              </div>
              <div className={styles.zoneActions}>
                <button className={styles.smallButton} onClick={() => duplicateZone(entry.def.id)}>{t("Zone.Duplicate")}</button>
                <button className={styles.smallButton} onClick={() => setConfirmRetire(!confirmRetire)}>{t("Zone.Retire")}</button>
              </div>
            </div>
          </div>
          {confirmRetire ? (
            <div className={classNames(styles.confirm, styles.confirmWide)}>
              <div className={styles.confirmText}>
                {t("Zone.RetireConfirm", { name: entry.def.name, base: entry.baseName ?? t("Zone.BaseFallback") })}
              </div>
              <div className={styles.confirmButtons}>
                <button className={styles.smallButton} onClick={() => setConfirmRetire(false)}>{t("Common.Cancel")}</button>
                <button className={classNames(styles.smallButton, styles.dangerButton)} onClick={() => { retireZone(entry.def.id); setConfirmRetire(false); }}>{t("Zone.Retire")}</button>
              </div>
            </div>
          ) : null}
          <Scrollable className={styles.rulesScroll} vertical trackVisibility="scrollable">
            <RulesEditor zone={draft} installed={installed} sources={sources} onChange={onChange} />
          </Scrollable>
        </div>
        <BuildingList zone={draft} buildings={buildings} installed={installed} onChange={onChange} />
      </div>

      {changes > 0 ? (
        <div className={styles.applyBar}>
          <div className={styles.applyDot} />
          <div className={styles.applyText}>
            {tn("Apply.Changes", changes) +
              (buildings.length > 0
                ? t("Apply.Diff", { added: stats.added, removed: stats.removed, before: stats.allowed - stats.added + stats.removed, after: stats.allowed })
                : "")}
          </div>
          <button className={styles.discard} onClick={onDiscard}>{t("Apply.Discard")}</button>
          <Tooltip tooltip={t("Apply.Tip")}>
            <button className={styles.apply} onClick={onApply}>{t("Apply.Apply")}</button>
          </Tooltip>
        </div>
      ) : null}
    </div>
  );
};

/**
 * The game side sends rezoning notices as JSON {key, vars}; labels inside vars may be keys too
 * ("Notice.Retired" with its own name), so they are translated here.
 */
function noticeText(json: string): string {
  try {
    const n = JSON.parse(json) as { key: string; vars?: Record<string, string | number>; label?: { key: string; vars?: Record<string, string | number> } };
    const vars = { ...(n.vars ?? {}) };
    if (n.label) vars.label = t(n.label.key, n.label.vars);
    return t(n.key, vars);
  } catch {
    return json;
  }
}
