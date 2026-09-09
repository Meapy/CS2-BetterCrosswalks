# Changelog

## 0.6.0 — unreleased

- **Fixed: the painted crossing never changed width.** People walked the full widened area, but the
  zebra on the ground stayed the size it was authored. A laid crossing's width is
  `NetLaneData.m_Width + NodeLane.m_WidthOffset` and the renderer only scales the mesh by the
  *offset* — `BatchDataHelpers.BuildCurveScale` is `1 + m_WidthOffset / m_Width`. Widening the lane
  prefab moved both terms of that fraction together, so the curve scale stayed at 1 and the paint
  never moved, while `CreatureUtils.GetLaneOffset` (which reads width **plus** offset) spread the
  walkers out as asked. That is the exact split the screenshots showed.
- Width is now applied the way the game applies it: the lane keeps pointing at its authored prefab
  and the mod writes `NodeLane.m_WidthOffset = (scale - 1) x authored width`. That is the one
  solution that satisfies both formulas at once — walking width and painted width both come out at
  `scale` times authored.
- Consequences of the above, all good ones:
  - No more cloned lane prefabs. `CrosswalkVariantLibrary` is gone.
  - The mod no longer writes to the shared `NetLaneData` at all. On load it now writes the authored
    width *back*, from the managed `PedestrianLane.m_Width` on the asset, which repairs a save that
    an earlier version left an inflated figure in.
  - No more re-laying the city to apply a width. "Apply to existing crossings" now visits each
    crossing once instead of destroying and rebuilding every lane at every junction.
- **Fixed: the width flickered on and off every other frame.** Tagging an edited lane `Updated`
  handed it back to the net pipeline, which re-laid the node and undid the write, which tagged it
  again. Only `BatchesUpdated` is set now — the width is already in place, and all that is left is
  for the renderer to rebuild that lane's batch.
- Crossings are recognised by `PedestrianLaneFlags.Crosswalk` on the laid lane rather than by
  matching prefabs against a catalogue, so a road family with an unusual crossing lane is picked up
  without having to be discovered first.
- Work is bounded: a full sweep on load, on a settings change and on "apply to existing"; otherwise
  only crossings the game has just laid and junctions the tool has just edited.

## 0.5.0 — unreleased

- The overlay now highlights each crossing along its own curve, at its own width, using the game's
  DrawCurve. The previous version drew filled discs at the junction centre, which covered a city
  block and said nothing about which crossing was which. The live crossing gets a small ring at
  each end as its grab handles.
- The toolbar button is now a white crossing on the same light blue as the other mod buttons. The
  glyph was rendering black because the SVG used currentColor, which does not inherit through an
  img tag.
- Tidied the panel: crossing number in the heading, larger value, buttons that match the row.

## 0.4.0 — unreleased

- **Fixed: per-junction widths never reached the paint.** LaneSystem resolves a road's declared
  crossing lane through `CheckPrefab`, which swaps in a themed variant from the placeholder's
  `PlaceholderObjectElement` buffer — a North American city lays "NA Crosswalk Lane 2" where the
  road declared "Crosswalk Lane 2". Matching laid lanes against only the declared prefab found
  nothing at all. The catalogue now records every variant, each with its own authored width.
- Each crossing at a junction can be set on its own. Selecting a junction draws a ring on every
  crossing; point at one to make it live, then drag out from it or use − and +.
- New: "Same for all N crossings here", for when one junction should be uniform.
- Per-crossing widths are saved with the city as a versioned buffer on the node, keyed by the
  crossing's position in the junction. The junction-wide value remains as the fallback.

## 0.3.0 — unreleased

- Drag to resize. With a junction selected, press and drag out from its centre; a ring shows the
  current width and follows the cursor. The drag is relative, so the width does not jump when it
  starts and the gesture works the same at any zoom.
- The toolbar button moved to the top left, and the icon is now a zebra crossing framed by its
  kerbs rather than four bars that read as soundwaves.
- Crossing lanes are now also discovered from `NetCompositionCrosswalk` — what the game actually
  built — as well as `NetCrosswalkData`, which is only what a road declares.
- `CrosswalkOverrideSystem` reports what it did: how many junctions and sub-lanes it walked, how
  many lanes it re-pointed, and the name of any pedestrian lane it did not recognise as a crossing.

## 0.2.0 — unreleased

- A toolbar button, in the row of mod buttons at the top right. Click it to start the crossing
  tool; a small panel beside it shows the selected junction's width with − and + buttons and a
  "back to global" button.
- The tool now works by clicking a junction to select it. The previous version relied on Page Up
  and Page Down, which the game only enables for a tool whose options panel offers elevation — for
  a mod tool they never fire, which is why it appeared to do nothing.
- Right click drops the selection; right click again leaves the tool.
- The tool logs what it is doing: when it starts, the first junction it points at, and every
  selection and width change.
- `CrosswalkWidth.csproj` now overrides the toolchain's `DeployWIP` target, which otherwise deletes
  the UI bundle from the deploy folder every time the managed project is built.

## 0.0.0 — unreleased

- Per-junction crossing widths. A tool (Options → Crosswalk Width → Start the crossing tool) lets
  you point at one junction and change only its crossings: Page Up and Page Down resize, left click
  puts it back on the global width, right click leaves the tool.
- Per-junction widths are saved with the city, as a versioned component on the node.
- New settings: tool step size, and "Clear all per-junction widths".
- Removing the mod puts every junction back on the game's own lane prefabs before restoring the
  authored widths, so nothing is left pointing at a prefab this mod created.

## 0.0.0 — unreleased

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
