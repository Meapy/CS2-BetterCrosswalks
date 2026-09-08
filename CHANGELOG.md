# Changelog

## 1.0.0 — unreleased

First version.

- Crossing width as a percentage of the width the crossing was drawn with (25%–400%), with an
  optional minimum and maximum in metres.
- Widens the painted band and the strip pedestrians walk on together, because in this game they are
  the same value: the width of the lane the crossing is laid along.
- Covers the "not walk" crossing variant the game swaps in at some connections, so a narrow band is
  never butted against a wide one.
- "Apply to existing crossings" re-lays the city's crossings without reloading the save.
- "List crossings in the log" reports every crossing lane found, its width before and after, and
  which road pieces declare it.
- Disabling the mod, and unloading it, restore every authored width — which matters because
  `NetLaneData` is serialized into the save.
