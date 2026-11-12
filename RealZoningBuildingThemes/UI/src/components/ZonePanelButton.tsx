import { useEffect, useRef } from "react";
import { useValue } from "cs2/api";
import { Button, Tooltip } from "cs2/ui";
import { panelOpen$, togglePanel$ } from "bindings";
import { t, useI18n } from "i18n";
import buttonIcon from "../images/zoning-outline.svg";

/** The top-left toolbar button that opens the zone panel: the zoning square in white outline, as most mods' buttons are. */
export const ZonePanelButton = () => {
  useI18n();
  const open = useValue(panelOpen$);

  // The keyboard shortcut (Shift+Z by default) toggles the panel; the count changes once per press.
  const presses = useValue(togglePanel$);
  const seen = useRef(presses);
  useEffect(() => {
    if (presses === seen.current) return;
    seen.current = presses;
    panelOpen$.update(!panelOpen$.value);
  }, [presses]);

  return (
    <Tooltip tooltip={t("Panel.Title")}>
      <Button
        variant="floating"
        src={buttonIcon}
        selected={open}
        onSelect={() => panelOpen$.update(!open)}
      />
    </Tooltip>
  );
};
