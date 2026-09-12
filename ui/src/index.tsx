import { ModRegistrar } from "cs2/modding";

import { CrosswalkToolButton } from "mods/crosswalk-tool";

/**
 * Adds the crossing button to the row of mod buttons at the top left of the screen.
 *
 * "GameTopLeft" is one of the append targets the toolchain declares in types/modding.d.ts
 * (AppendHookTargets), so it is a supported extension point rather than a module path discovered by
 * hand — nothing to re-pin after a game update.
 *
 * There used to be a `hasAppend("GameTopLeft")` check in front of this, meant to log a line rather
 * than fail silently if the target ever went away. It did the opposite: it stopped the button
 * appearing at all. `hasAppend` does not report whether a target *exists* — it reports whether
 * something has already been appended to it. This mod registers early, before any other mod has
 * appended anything, so it read "false" and refused to register; TownRoadLane appended its own
 * GameTopLeft button four milliseconds later without trouble. Worse, it depended on load order, so
 * it worked until enough other mods were installed and then quietly stopped.
 *
 * The lesson is the general one: a guard that has to be right about an API you have not read is not
 * a safety net, it is a second thing that can fail. The try/catch below needs no such assumption.
 */
const register: ModRegistrar = (moduleRegistry) => {
  try {
    moduleRegistry.append("GameTopLeft", CrosswalkToolButton);
    console.log("[CrosswalkWidth] Toolbar button registered.");
  } catch (error) {
    console.error(
      "[CrosswalkWidth] Could not add the toolbar button, so the crossing tool cannot be " +
        "opened. The width settings still work.",
      error
    );
  }
};

export default register;
