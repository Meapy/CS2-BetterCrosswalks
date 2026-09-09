import { ModRegistrar } from "cs2/modding";

import { CrosswalkToolButton } from "mods/crosswalk-tool";

/**
 * Adds the crossing-width button to the row of mod buttons at the top left of the screen.
 *
 * "GameTopLeft" is one of the append targets the toolchain declares in types/modding.d.ts
 * (AppendHookTargets), so it is a supported extension point rather than a module path discovered
 * by hand — nothing to re-pin after a game update, and no risk of throwing if a panel moves.
 *
 * hasAppend is checked first so that a toolchain which ever drops the target logs a line instead of
 * failing silently, which is the failure mode that costs the most time to diagnose.
 */
const register: ModRegistrar = (moduleRegistry) => {
  if (typeof moduleRegistry.hasAppend === "function" && !moduleRegistry.hasAppend("GameTopLeft")) {
    console.warn(
      "[CrosswalkWidth] The GameTopLeft append target is not available; the toolbar button " +
        "will not appear. The tool can still be started from Options."
    );
    return;
  }

  moduleRegistry.append("GameTopLeft", CrosswalkToolButton);

  console.log("[CrosswalkWidth] Toolbar button registered.");
};

export default register;
