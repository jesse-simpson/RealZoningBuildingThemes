// Small controls in the game's style. Coherent (the game's UI engine) has no native dropdowns or
// styled checkboxes and supports flexbox only, so these are plain elements styled from the game's
// theme variables (see controls.module.scss).

import { ReactNode, useEffect, useRef, useState } from "react";
import classNames from "classnames";
import { Tooltip } from "cs2/ui";
import { Sign } from "model";
import { ToolButton, toolButtonTheme, toolOptionsTheme } from "vanilla";
import opEq from "../images/op_eq.svg";
import opNe from "../images/op_ne.svg";
import opGt from "../images/op_gt.svg";
import opLt from "../images/op_lt.svg";
import { t } from "i18n";
import styles from "./controls.module.scss";

export const Chip = ({ on, onClick, children }: { on: boolean; onClick: () => void; children: ReactNode }) => (
  <button className={classNames(styles.chip, on && styles.chipOn)} onClick={onClick}>
    {children}
  </button>
);

/** Square icon button for the theme rows (Find It's layout). */
export const IconToggle = ({ on, src, label, isAll, onClick }: { on: boolean; src: string; label?: string; isAll?: boolean; onClick: () => void }) => {
  const button = (
    <button className={classNames(styles.iconToggle, isAll && styles.iconToggleAll, on && styles.iconToggleOn)} onClick={onClick}>
      <img src={src} />
    </button>
  );
  return label ? <Tooltip tooltip={label}>{button}</Tooltip> : button;
};

/** One-row switch, as the game's option rows use. */
export function Segmented<T extends string>({ value, options, onChange, small }: { value: T; options: [T, string, string?][]; onChange: (v: T) => void; small?: boolean }) {
  return (
    <div className={classNames(styles.segmented, small && styles.segmentedSmall)}>
      {options.map(([v, label, tip]) => {
        const button = (
          <button key={v} className={classNames(styles.segment, v === value && styles.segmentOn)} onClick={() => onChange(v)}>
            {label}
          </button>
        );
        return tip ? (
          <Tooltip key={v} tooltip={tip}>
            {button}
          </Tooltip>
        ) : (
          button
        );
      })}
    </div>
  );
}

export const Switch = ({ on, onChange }: { on: boolean; onChange: (v: boolean) => void }) => (
  <button className={classNames(styles.switch, on && styles.switchOn)} onClick={() => onChange(!on)}>
    <div className={styles.switchKnob} />
  </button>
);

export const Checkbox = ({ on, onChange }: { on: boolean; onChange: (v: boolean) => void }) => (
  <button className={classNames(styles.checkbox, on && styles.checkboxOn)} onClick={() => onChange(!on)}>
    {on ? <img src="Media/Glyphs/Checkmark.svg" /> : null}
  </button>
);

/** − value + stepper, used where typing numbers would fight the game's keyboard shortcuts. */
export const Stepper = ({ value, step, min, max, format, onChange }: { value: number; step: number; min: number; max: number; format: (v: number) => string; onChange: (v: number) => void }) => (
  <div className={styles.stepper}>
    <button className={styles.stepperButton} onClick={() => onChange(Math.max(min, Math.round((value - step) * 100) / 100))}>
      −
    </button>
    <div className={styles.stepperValue}>{format(value)}</div>
    <button className={styles.stepperButton} onClick={() => onChange(Math.min(max, Math.round((value + step) * 100) / 100))}>
      +
    </button>
  </div>
);

/** Two-thumb range slider; the ends mean "no limit". */
export const RangeSlider = ({ min, max, low, high, onChange }: { min: number; max: number; low: number; high: number; onChange: (low: number, high: number) => void }) => {
  const track = useRef<HTMLDivElement>(null);
  const [drag, setDrag] = useState<"low" | "high" | null>(null);

  useEffect(() => {
    if (!drag) return;
    const move = (e: MouseEvent) => {
      const el = track.current;
      if (!el) return;
      const r = el.getBoundingClientRect();
      const v = Math.round(min + ((e.clientX - r.left) / r.width) * (max - min));
      const clamped = Math.max(min, Math.min(max, v));
      if (drag === "low") onChange(Math.min(clamped, high), high);
      else onChange(low, Math.max(clamped, low));
    };
    const up = () => setDrag(null);
    window.addEventListener("mousemove", move);
    window.addEventListener("mouseup", up);
    return () => {
      window.removeEventListener("mousemove", move);
      window.removeEventListener("mouseup", up);
    };
  }, [drag, low, high, min, max, onChange]);

  const pct = (v: number) => `${((v - min) / (max - min)) * 100}%`;
  return (
    <div className={styles.slider} ref={track}>
      <div className={styles.sliderTrack} />
      <div className={styles.sliderFill} style={{ left: pct(low), width: `${((high - low) / (max - min)) * 100}%` }} />
      <div className={styles.sliderThumb} style={{ left: pct(low) }} onMouseDown={() => setDrag("low")} />
      <div className={styles.sliderThumb} style={{ left: pct(high) }} onMouseDown={() => setDrag("high")} />
    </div>
  );
};

/** A button that opens a small list below it (Coherent has no native select). */
export function Picker<T extends string>({ value, options, onChange, className, plain, flush }: { value: T; options: [T, string][]; onChange: (v: T) => void; className?: string; plain?: boolean; flush?: boolean }) {
  const [open, setOpen] = useState(false);
  const label = options.find(([v]) => v === value)?.[1] ?? value;
  return (
    <div className={classNames(styles.picker, className)}>
      <button className={classNames(styles.pickerButton, plain && styles.pickerButtonPlain, flush && styles.pickerButtonFlush)} onClick={() => setOpen(!open)}>
        {label}
        <img className={styles.pickerArrow} src="Media/Glyphs/ThickStrokeArrowDown.svg" />
      </button>
      {open ? (
        <div className={styles.pickerList}>
          {options.map(([v, l]) => (
            <button
              key={v}
              className={classNames(styles.pickerItem, v === value && styles.pickerItemOn)}
              onClick={() => {
                onChange(v);
                setOpen(false);
              }}
            >
              {l}
            </button>
          ))}
        </div>
      ) : null}
    </div>
  );
}

// ---- Find It's value filter, built from the same game components Find It uses: a comparison
// button (= ≠ > <), then down arrow, value, up arrow. The comparison button always shows; it only
// takes effect once a value is set.

const SIGN_ORDER: Sign[] = ["eq", "ne", "gt", "lt"];
const SIGN_ICON: Record<Sign, string> = { eq: opEq, ne: opNe, gt: opGt, lt: opLt };

export const ValueFilter = ({ value, sign, max, onChange }: { value: number; sign: Sign; max: number; onChange: (value: number, sign: Sign) => void }) => {
  const opts = toolOptionsTheme();
  const tb = toolButtonTheme();
  const shown = value === 0 ? t("Common.Any") : value === max && max >= 10 ? `${max}+` : String(value);
  return (
    <div className={styles.valueControls}>
      <ToolButton
        className={classNames(tb.button, styles.signButton, value !== 0 && styles.signOn)}
        src={SIGN_ICON[sign]}
        tooltip={t(`Sign.${sign}`)}
        onSelect={() => onChange(value, SIGN_ORDER[(SIGN_ORDER.indexOf(sign) + 1) % SIGN_ORDER.length])}
      />
      <ToolButton className={classNames(tb.button, opts.startButton)} src="Media/Glyphs/ThickStrokeArrowDown.svg" onSelect={() => onChange(Math.max(0, value - 1), sign)} />
      <div className={classNames(opts.numberField, styles.valueField)}>{shown}</div>
      <ToolButton className={classNames(tb.button, opts.endButton)} src="Media/Glyphs/ThickStrokeArrowUp.svg" onSelect={() => onChange(Math.min(max, value + 1), sign)} />
    </div>
  );
};

/** Find It's down/value/up arrows on their own: 0 shows "Any". Used for the lot ranges. */
export const ArrowStepper = ({ value, max, onChange }: { value: number; max: number; onChange: (v: number) => void }) => {
  const opts = toolOptionsTheme();
  const tb = toolButtonTheme();
  return (
    <div className={styles.valueControls}>
      <ToolButton className={classNames(tb.button, opts.startButton)} src="Media/Glyphs/ThickStrokeArrowDown.svg" onSelect={() => onChange(Math.max(0, value - 1))} />
      <div className={classNames(opts.numberField, styles.valueField)}>{value === 0 ? t("Common.Any") : String(value)}</div>
      <ToolButton className={classNames(tb.button, opts.endButton)} src="Media/Glyphs/ThickStrokeArrowUp.svg" onSelect={() => onChange(Math.min(max, value + 1))} />
    </div>
  );
};

/**
 * A min "to" max range of whole numbers, each end an ArrowStepper; 0 means no limit on that end.
 * Raising the low end past the high end (or the reverse) moves the other end with it. Shared by
 * the zone's lot rules and the building list's lot filters, so both work the same way.
 */
export const RangeStepper = ({ min, max, limit, onChange }: { min: number | null | undefined; max: number | null | undefined; limit: number; onChange: (min: number | null, max: number | null) => void }) => {
  const lo = min ?? 0;
  const hi = max ?? 0;
  return (
    <div className={styles.rangeStepper}>
      <ArrowStepper value={lo} max={limit} onChange={(v) => onChange(v === 0 ? null : v, hi !== 0 && v > hi ? v : max ?? null)} />
      <span className={styles.rangeTo}>{t("Rules.To")}</span>
      <ArrowStepper value={hi} max={limit} onChange={(v) => onChange(lo !== 0 && v !== 0 && v < lo ? v : min ?? null, v === 0 ? null : v)} />
    </div>
  );
};
