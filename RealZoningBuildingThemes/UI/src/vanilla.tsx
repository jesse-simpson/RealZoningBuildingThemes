// The game's own UI components that cs2/ui does not export, reached through the module registry
// (the same approach Find It and other mods use). Each is looked up lazily, so a game update that
// moves one breaks only the control that uses it.

import { getModule } from "cs2/modding";
import { ReactNode } from "react";

const cache: Record<string, any> = {};
const get = (path: string, name: string) => (cache[path + name] ??= getModule(path, name));

export interface ToolButtonProps {
  src?: string;
  selected?: boolean;
  disabled?: boolean;
  tooltip?: ReactNode;
  className?: string;
  focusKey?: any;
  onSelect?: () => void;
  children?: ReactNode;
}

/** The square option button of the game's tool options panel (roads, zoning); Find It's filters use it. */
export const ToolButton = (props: ToolButtonProps) => {
  const Component = get("game-ui/game/components/tool-options/tool-button/tool-button.tsx", "ToolButton");
  return <Component focusKey={FOCUS_DISABLED()} {...props} />;
};

export const toolButtonTheme = (): Record<string, string> =>
  get("game-ui/game/components/tool-options/tool-button/tool-button.module.scss", "classes") ?? {};

export const toolOptionsTheme = (): Record<string, string> =>
  get("game-ui/game/components/tool-options/mouse-tool-options/mouse-tool-options.module.scss", "classes") ?? {};

export const FOCUS_DISABLED = () => get("game-ui/common/focus/focus-key.ts", "FOCUS_DISABLED");

export interface TextInputProps {
  value: string;
  placeholder?: string;
  className?: string;
  onChange: (e: { target: { value: string } }) => void;
}

/** The game's text field: it holds the game's keyboard shortcuts back while typing. */
export const TextInput = (props: TextInputProps) => {
  const Component = get("game-ui/common/input/text/text-input.tsx", "TextInput");
  return <Component type="text" {...props} />;
};
