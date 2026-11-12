import { ModRegistrar } from "cs2/modding";
import { ZonePanel } from "components/ZonePanel";
import { ZonePanelButton } from "components/ZonePanelButton";

/**
 * Real Zoning & Building Themes: the zone panel. The button sits with other mods' buttons in
 * the top-left corner of the game screen; the panel renders on the game screen.
 */
const register: ModRegistrar = (moduleRegistry) => {
  moduleRegistry.append("GameTopLeft", ZonePanelButton);
  moduleRegistry.append("Game", ZonePanel);
};

export default register;
