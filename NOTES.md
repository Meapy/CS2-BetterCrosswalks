# Notes

What was expensive to work out, and what is inferred rather than measured. The changelog says what
changed; this says why it was hard.

All file references are to the decompiled game source.

---

## A crossing is a lane, not a piece of the road

This is the thing to internalise, and it is what keeps this mod small.

Anything that is part of a road's cross-section — a lane, a verge, the kerbside paving — hangs off
the chain `NetGeometrySection` → `NetSectionPrefab` → `NetSectionPiece` → `NetPiecePrefab`, and the
road's width is the running sum of those piece widths. Changing one means changing the road itself,
rebuilding cached compositions, and re-laying the city.

A crossing is none of that. The chain is:

1. `NetPieceCrosswalk` (a `ComponentBase` on a `NetPiecePrefab`) declares `m_Start`/`m_End` across
   the road and names a `NetLanePrefab`. It becomes `NetCrosswalkData` on the piece entity.
2. `NetCompositionSystem.AddCompositionCrosswalks` walks the composition's pieces in order and
   merges adjacent crossing spans into one `NetCompositionCrosswalk` — hence the run of `flag`,
   `flag2`, `flag3`, `flag4` locals: it is stitching a crossing that spans several pieces into one
   entry, and setting `LaneFlags.CrossRoad` if the span covers a road lane.
3. `LaneSystem` lays a pedestrian lane along that span at every node, interpolating the span across
   the node's geometry:

   ```csharp
   float num14 = netCompositionCrosswalk.m_Start.x / math.max(1f, netCompositionData2.m_Width) + 0.5f;
   sourcePosition.m_Position = math.lerp(left2.m_Left.d, left2.m_Right.d, num14);
   ```

`NetCompositionCrosswalk` carries `m_Lane`, `m_Start`, `m_End` and `m_Flags` — and no width. So the
width is not in the composition at all, which is why this mod needs no composition rebuild and
cannot leave a road half-updated.

## Lane width is `NetLaneData.m_Width`, and for a crossing that is the band depth

`LaneSystem.CreateNodeConnectionLane`:

```csharp
component3.m_WidthOffset.x = componentData.m_Width - netLaneData.m_Width;
```

`NodeLane.m_WidthOffset` is defined as the *difference between two lane prefabs' widths*, and is
flagged `NodeLaneFlags.StartWidthOffset` / `EndWidthOffset` when non-zero so the lane flares at that
end. A difference of widths is only a meaningful correction if `NetLaneData.m_Width` is the width
the lane is actually drawn at.

A lane's width is perpendicular to its curve. A crossing lane's curve runs *across* the road, so its
width is the depth of the painted band in the direction traffic travels — which is exactly the
dimension a player means by "wider crosswalk", and simultaneously the strip pedestrians walk on.

**Inferred, not measured.** The render side was not traced end to end: lane meshes are instanced
through `LaneProperty.CurveScale` (`colossal_CurveScale`), and how that float4 is filled from the
lane's width was not followed into `BatchDataHelpers`. If the paint does not change width in game
but the walking behaviour does, that is where to look, and the answer would be that the zebra mesh
is authored at a fixed cross-section rather than scaled.

## Finding the crossing lanes

`NetPieceCrosswalk.GetPrefabComponents` adds `NetCrosswalkData` to the piece prefab entity, so
`GetEntityQuery(ComponentType.ReadOnly<NetCrosswalkData>())` is exactly "every piece that declares
a crossing", with no name matching. Piece names live inside the game's `.cok` archives and cannot
be read from disk, so a structural query was the only option that also covers modded roads.

`PedestrianLaneData.m_NotWalkLanePrefab` is picked up too. `LaneSystem` swaps a crossing lane for
that variant where a crossing meets something a citizen may not step onto; it is drawn in the same
place, so leaving it at the authored width would butt a narrow band against a wide one.

## Per junction means a different prefab, not a different value

The global width works by rewriting `NetLaneData.m_Width` on the game's own crossing lane prefabs,
which every junction shares — which is exactly why it applies everywhere at once. One junction
cannot hold a different value in the same component, so it has to point at a different prefab.

`CrosswalkVariantLibrary` clones the authored lane with `EntityManager.Instantiate` and changes the
width on the clone. `CrosswalkOverrideSystem` then rewrites `PrefabRef` on that junction's crossing
sub-lanes to the clone.

Three things make this work, and each was a trap:

1. **It has to run after `LaneSystem`.** `LaneSystem` writes each crossing lane's `PrefabRef` from
   the composition every time it re-lays a node (`Game.Common.SystemOrder:155` puts it at
   Modification4). Running before it means being overwritten inside the same frame. Running after
   it, every frame, also means a node re-laid for any other reason silently gets its override back.
2. **Creating a variant is a structural change.** `Instantiate` invalidates every `DynamicBuffer`
   held at the time, so the system walks the `SubLane` buffers in a read-only first pass, and only
   then creates clones and rewrites `PrefabRef` (which is not structural) before batching the tag
   changes at the end.
3. **`BatchesUpdated`, not just `Updated`.** A lane's drawn mesh comes from its prefab; the render
   batch has to be told to rebuild for it.

Teardown order matters too: junction lanes go back to the authored prefabs *first*, then the
authored widths are restored, then the clones are destroyed. Any other order leaves lanes pointing
at entities that have gone.

**Unverified.** A clone keeps the original's `PrefabData`, which indexes `PrefabSystem`'s list of
managed prefabs, so anything resolving a managed `PrefabBase` from a cloned lane gets the original
back. Nothing in the render path was found doing that, but it was not exhaustively traced.

## A mod tool cannot override `UpdateActions`

`ToolBaseSystem.ResetActions` sets `applyAction.shouldBeEnabled = false` when a tool stops, and the
base `UpdateActions` is empty — so a tool that wants clicks has to switch them back on. The game's
own tools do it by overriding `UpdateActions`, which is `private protected`: accessible only to
derived types **in the same assembly**. A mod cannot override it. `CrosswalkPickerToolSystem` sets
the flags in `OnStartRunning` after calling base instead.

Page Up and Page Down arrive through `ElevationUp` / `ElevationDown`, which are ordinary `public
virtual` members. That is the one piece of input a mod tool gets without declaring a binding, which
is why the tool uses it for width while there is no toolbar button.

## A mod tool only gets the input the game hands it

The first version of the tool did nothing in game, and the log proved the tool itself was running:
`per-junction tool active` was written, so activation worked and the raycast was not the suspect.

The cause was the input. `ElevationUp` / `ElevationDown` are `public virtual` on `ToolBaseSystem`,
which makes them look like free input for a mod tool — but the game only enables the elevation
actions for a tool whose options panel offers elevation. An unknown mod tool never gets them, so
the handlers were never called and nothing visibly happened.

What a mod tool *can* rely on is `applyAction` and `cancelAction` — left and right click — which
`ToolBaseSystem` sets up for every tool. Anything beyond that has to come from the mod's own UI or
its own declared keybinding. So selection moved to left click, and widening moved into the toolbar
panel.

Worth remembering as a general rule: if a tool appears inert, log inside `OnStartRunning` and on
first hover before suspecting the raycast. Those two lines split the problem in half.

## Overlay primitives, and the one that was wrong

`OverlayRenderSystem.Buffer.DrawCircle(Color, float3, float)` draws a **filled disc**, not a ring,
and it takes a diameter in metres. Drawing a handle with it at junction scale covers a city block.

The overloads that take `(outlineColor, fillColor, outlineWidth, StyleFlags, ...)` are the ones
worth using: a transparent fill gives a ring, and `StyleFlags.Projected` lays the shape on the
ground rather than standing it up.

For anything that follows a lane, `DrawCurve(outline, fill, outlineWidth, styleFlags, Bezier4x3,
width)` is better than any arrangement of circles: the crossing's own `Game.Net.Curve.m_Bezier`
drawn at the crossing's own width sits exactly on the paint, and thickens in place as the width
changes. That is both the clearest readout and the most obvious grab target.

## currentColor does not survive an img tag

The toolbar glyph rendered black on the first attempt. The SVG used `fill="currentColor"` and the
button set `color: #ffffff`, which works for an inlined SVG and not for one loaded through
`<img src=...>` — there the SVG has its own context and `currentColor` falls back to black. Icons
loaded as assets need their colours written in.

## The lane a road declares is not the lane that gets laid

This was the bug behind "the number changes but the crossing does not". The diagnostic said it in
one line:

```
pedestrian lane not known as a crossing: NA Crosswalk Lane 2
2 overridden junctions, 58 sub-lanes, none of them a known crossing lane (58 unrecognised)
```

`LaneSystem.CheckPrefab` resolves a lane prefab through its `PlaceholderObjectElement` buffer,
picking a variant whose `ObjectRequirementElement` group matches the city's theme. So a road that
declares "Crosswalk Lane 2" has "NA Crosswalk Lane 2" laid in a North American city, and matching a
laid lane against the declared prefab matches nothing whatsoever.

The catalogue now walks `PlaceholderObjectElement` on every crossing lane it captures and records
each variant with its own authored width — the variants are not all drawn the same, so a single
shared baseline would be wrong.

Worth generalising: any lane, object or net prefab a mod resolves by identity may be a placeholder.
Compare against the variants, not just the declared prefab.

## Two sources for "which lane is a crossing"

`NetCrosswalkData` on a piece prefab is what a road *declares*. `NetCompositionCrosswalk` on a
composition is what `AddCompositionCrosswalks` actually produced, and it is the lane `LaneSystem`
lays. They are normally the same entity, but only the second is authoritative, so the catalogue
now collects from both.

This matters because the per-junction override matches a node's sub-lanes by prefab identity. If a
road family substitutes its crossing lane anywhere between the piece and the laid lane, matching
against the declared lane silently finds nothing — the junction's stored width changes, the log
says so, and the paint never moves.

`CrosswalkOverrideSystem` now logs the shape of that failure directly: how many junctions and
sub-lanes it walked, how many lanes it re-pointed, and the name of any *pedestrian* lane prefab it
did not recognise. Two lines in the log separate "nothing matched" from "everything matched and the
problem is in rendering", which is the split that was missing.

## Diagonal crossings are a different feature entirely

Worth writing down, because it looks like it should be a parameter and is not.

`NetCompositionCrosswalk` holds `m_Start` and `m_End`, and `LaneSystem` turns them into a lane by
interpolating **across one road arm's cross-section**:

```csharp
float num14 = netCompositionCrosswalk.m_Start.x / math.max(1f, netCompositionData2.m_Width) + 0.5f;
sourcePosition.m_Position = math.lerp(left2.m_Left.d, left2.m_Right.d, num14);
```

Both endpoints are positions along the same arm. There is no way to express "from this corner to
the opposite corner" in that data — a crossing is perpendicular to its arm by construction, and the
`x` values only slide the ends along it.

What *is* available cheaply is `m_Start.z` / `m_End.z`, applied as
`sourcePosition.m_Position += sourcePosition.m_Tangent * netCompositionCrosswalk.m_Start.z` — an
offset along the road, which moves a crossing toward or away from the junction. That is a real knob
and would fit the existing catalogue exactly, since `NetCrosswalkData` on the piece is where those
figures come from.

A true diagonal means creating pedestrian lanes that do not exist: new node lanes from one corner
to another, with their own `Curve`, `PrefabRef` and — the hard part — `PathNode` wiring, or
pedestrians will not use them and pathfinding may reject them. That is lane creation, not parameter
editing, and it is a larger piece of work than everything in this repo so far.

## `NetLaneData` is serialized

`NetLaneData : ISerializable` — the width this mod writes goes into the save. Hence the discipline:
the authored figure is kept outside the component, every write is `base * factor` rather than
read-modify-write, and `OnDestroy` restores it. Without that, a player who removes the mod keeps a
scaled crossing width forever and each session would compound the last.

## Shared prefabs are reported, not filtered

A crossing lane prefab shared with the ordinary kerbside walking lanes would mean widening
crossings also widens those. Rather than guess, the catalogue checks whether each crossing lane also
appears in some piece's `NetPieceLane` buffer and says so in the log dump. If that flag ever shows up, the
fix is a decision, not a default.

## The one frame where a tag survives exactly once

`PrepareCleanUpSystem` is registered `UpdateAfter(MainLoop)` so it runs **last** in MainLoop;
`CleanUpSystem` strips the tags it collected in the `Cleanup` phase at the end of the same frame.
The Modification phases run *inside* MainLoop, ahead of that. So a tag added during Modification1
is seen by the net systems later in the same frame and is gone by the next one — processed exactly
once. That is why the mod's system is registered
`UpdateBefore<CrosswalkWidthSystem>(SystemUpdatePhase.Modification1)`.

It matters less here than it did for road compositions (`CalculateCompositionPieceOffsets`
accumulates into `m_Offset.x` rather than assigning, so a composition processed twice comes out
twice as wide), but the same reasoning picked the phase.

## Two traps in the road cross-section, for whenever this mod grows

Nothing here touches a road's cross-section today. If that ever changes, these two cost real
debugging time to find:

- **A `NetPieceArea` is not bounded by its piece.** The buildable and snapping strips are routinely
  authored several times wider than the paving. Scaling them by the same *ratio* as the piece turns
  a small gain in paving into several metres of flat blended ground — a blank apron beside the road.
  If anything here ever grows an area, grow it by the delta in metres, not the ratio.
- **Road compositions are cached forever.** `CompositionSelectSystem.CreateComposition` appends to
  the prefab's `NetGeometryComposition` buffer and nothing invalidates it. Any future change to a
  road's cross-section has to deal with that; a change to a lane prefab does not.

## A crossing's width is two numbers, and the paint only reads the second

This is the one that cost the most. Widening the lane prefab made citizens use the whole widened
band while the painted zebra stayed exactly as authored, which looked like a rendering bug and was
not one.

A laid lane's real width is

    NetLaneData.m_Width  +  NodeLane.m_WidthOffset

and both halves of the game read it that way, but not in the same form:

- `Game.Creatures.CreatureUtils.GetLaneOffset` spreads walkers across
  `prefabLaneData.m_Width + math.lerp(nodeLane.m_WidthOffset.x, nodeLane.m_WidthOffset.y, t)` — the
  **sum**, so a wider prefab alone is enough.
- `Game.Rendering.BatchDataHelpers.BuildCurveScale(nodeLane, netLaneData)` returns
  `1 + nodeLane.m_WidthOffset / netLaneData.m_Width` — a **ratio**, fed to the lane's transform
  matrix in `BatchDataSystem` as the mesh's lateral scale. A wider prefab moves numerator and
  denominator together and leaves the scale at 1.

So there is exactly one way to make a crossing `scale` times its authored width in both senses:
leave `PrefabRef` on the authored lane and write

    m_WidthOffset = (scale - 1) * authoredWidth

Everything else is inconsistent. Scaling the prefab gives the right walking width and no paint;
scaling the prefab *and* offsetting to force the ratio gives the right paint and a walking width of
`scale²` times authored.

`LaneSystem` never widens a prefab either — `CreateNodePedestrianLane` sets
`m_WidthOffset = declaredWidth - resolvedWidth`, which is the same mechanism used to flare a
crossing where a road with wide sidewalks meets one with narrow ones.

Two follow-ons:

- `NodeLane` only serializes an end whose flag is set (`NodeLaneFlags.StartWidthOffset` /
  `EndWidthOffset`), so writing the value without the flag loses it silently on the next load.
- The base offset the game computed is overwritten rather than added to. For crossings it is
  normally zero, because the themed variant a placeholder resolves to shares its width. A road
  family where that is not true would lose a small flare.

## `Updated` on a lane is a loop, `BatchesUpdated` is not

Tagging a lane `Updated` after changing it hands it back to the net pipeline, which re-lays the
owning node, which rewrites what was just changed — and the mod, seeing it wrong again, changes it
back. The log showed this as a count alternating `4, 0, 4, 0, ...` across 915 lines: applied,
reverted, applied, reverted. A component change that only needs the mesh redrawn wants
`BatchesUpdated` on its own.

## The authored width is on the managed asset, not in the component

`NetLaneData.m_Width` is live data that a mod can write to and that a save can carry.
`Game.Prefabs.PedestrianLane.m_Width` on the managed `PrefabBase` is what the asset author set, and
nothing writes to it. Reaching it via `PrefabSystem.TryGetPrefab<PrefabBase>` and
`PrefabBase.TryGet<PedestrianLane>` is how the mod can still recover the true figure after a session
that left an inflated one behind.
