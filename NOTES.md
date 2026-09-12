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
to another, with their own `Curve`, `PrefabRef` and — the part that looks hardest — `PathNode`
wiring. That is lane creation, not parameter editing. It is what 0.10.0's scramble crossing does;
see "Adding a crossing: how it is done" at the end of this file.

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

## Never let a saved entity point at a prefab the game does not know

The v0.6 per-junction override worked by cloning a crossing lane prefab with
`EntityManager.Instantiate` and pointing the lane's `PrefabRef` at the clone. The clone was never
registered with `PrefabSystem`, and `Instantiate` strips the `Prefab` tag, so it was not a prefab
in the sense the serializer means. A city saved in that state records prefab references it cannot
resolve on the next load:

    [SceneFlow] [WARN]  Unknown prefab ID: [Missing]:[Missing]
    [SceneFlow] [WARN]  Unknown prefab ID: [Missing]:[Missing]

— blank type, blank name, one per clone. In the session that followed, `ClimateSystem.PostDeserialize`
threw a `NullReferenceException` in `ApplyWeatherEffects`, `TerrainMaterialSystem.ApplyRenderSettings`
logged "Terrain Render Settings missing", and the process took a native crash. The session before
that, with the same city and the same mods, had none of those lines.

A *named* unknown prefab is survivable — four `MarkingStudio ...` ones had been in that city's log
for weeks without trouble. A blank one is not.

So: a mod may write component *data* onto entities the save owns, and may add components of its own
(the game drops unknown component types when the mod is gone). It must not make an entity reference
an entity that `PrefabSystem` has never heard of. If a per-instance variant of a prefab is ever
needed again, register it properly — or, as v0.7.0 does, find the per-instance field the game
already has and write that instead.

## `NodeLane.m_WidthOffset` is not yours to overwrite

It looks like a spare field. It is not: for a crossing, `LaneSystem.CreateNodePedestrianLane`
writes

    m_WidthOffset.x = declaredWidth - variantWidth

where `declaredWidth` is the lane named in `NetCompositionCrosswalk.m_Lane` and `variantWidth` is
whatever `CheckPrefab` swapped in for the city's theme. It is how the game holds a themed variant to
the size the road's composition asked for.

So a mod scaling crossings has to get two things right that are easy to miss:

- **The baseline to scale from is the declared width, not the prefab on the lane.** Scaling the
  variant's width gives the wrong answer wherever the two differ.
- **Restoring means writing `declaredWidth - variantWidth` back, not zero.** Zeroing looks like a
  restore and is actually a second change, one the player cannot see and that survives the mod being
  uninstalled.

Both are stateless if the catalogue remembers, for each themed variant, which placeholder declared
it — which it already has to walk (`PlaceholderObjectElement`) to find the variants at all. With
that map, `scale = 1` reproduces the game's own value exactly, so "off" and "restore" are the same
code path as "apply", and nothing has to be remembered between sessions.

The general rule: before writing a game-owned serialized field, work out what the game would have
put there. If that cannot be reconstructed, do not write the field at all — a crossing this mod
does not recognise is left exactly as laid rather than written with a guess.

## Prefab writes do not reach a city save

Worth knowing, because it changes what has to be defended against. Prefab entities carry the
`Prefab` tag and are excluded from `SerializerSystem`'s query; a save records a prefab **ID table**
plus the entities that reference it, not the prefabs' component data. So writing `NetLaneData` on a
prefab is a session-lifetime change, undone by restarting the game — annoying, not corrupting.

The things that *do* reach a save are the components on world entities: `NodeLane` here, and any
component a mod adds itself. That is where the care belongs.

## A junction's size already depends on how far in its crossings sit

`NetCompositionCrosswalk.m_Start.z` is how far *inside* the edge end a crossing is placed:
`LaneSystem` applies it as `position += tangent * m_Start.z`, and that tangent points from the edge
end toward the node centre. The same number is read a second time, by
`GeometrySystem.CalculateCornerOffset`:

    x = max(x, CheckCrosswalks(crosswalks))          // = max over crossings of max(m_Start.z, m_End.z)

and folded into the corner offset that decides how far back each edge stops short of the node.
Everything anchored to that geometry — the carriageway, the give-way line, the signal posts —
follows.

So one field says both "the crossing is this far in" and "the junction has to be this much bigger to
hold it", and raising it moves the road back rather than moving the crossing into the traffic. That
is the lever for making room for a widened crossing, and it is the game's own, which beats inventing
one: how completely each road makes way is scaled by how directly the two edges face each other, and
that rule is already tuned.

Two limits worth knowing. The buffer is on the *composition* prefab, shared by every junction using
that road configuration, so this can only ever follow a global setting. And a plain mid-block
crossing takes a different branch in `LaneSystem` that never applies `z` at all, while
`CheckCrosswalks` still reads it — so those nodes are enlarged without the crossing moving.

## A curve a mod rewrites needs a record of what was written, not just what it was

`ApplyShift` moves a crossing's ends, and `LaneSystem` owns that curve — it rewrites it whenever it
re-lays the junction. So the override stores the curve the game laid *and* the shift last written
into it, and the pass decides what to do by comparing:

    at the recorded base          -> just re-laid; apply the shift
    at base + the applied shift   -> already done; leave it
    at neither                    -> the junction changed shape; take this as the new base

Storing only the base is not enough, and this cost a real bug. A drag calls the setter every frame
with a new value while the curve still holds the previous one — so "is it at base + shift?" tested
against the *new* shift says no, re-bases on the already-shifted curve, and adds the shift again.
The displacement becomes the sum of every frame's cursor offset: a one-second drag five metres out
moves the crossing a few hundred metres, and the clamp on the shift does not bound it because the
base is what moved.

The general shape: when a mod and the game both write the same field, "what did I write last time"
is a different question from "what was it before I touched it", and idempotence needs both.

## Whatever holds the record must outlive whatever it describes

The same override that holds the shift holds the base curve. Which means every path that deletes an
override has to put the crossing back *first* — clearing one junction, zeroing one crossing, the
"clear all per-junction widths" button. Delete first and the crossing is stranded wherever it was
dragged, with nothing left that knows where it belongs: not the mod, not the game, and not the
"remove this mod's data" button, which reads the same record.

## Adding a crossing: how it is done

Built in 0.10.0, as the scramble crossing. It is lane creation, not parameter editing, and every
piece below was traced before anything was written.

**The entity.** `NetLaneArchetypeData.m_NodeLaneArchetype` on the crossing lane prefab, which
already includes `Created` and `Updated`. `Owner` is not in it — `LaneSystem` adds that separately —
so a mod has to as well. Then set `PrefabRef`, `Lane`, `Curve`, `NodeLane` and `PedestrianLane`
exactly as `LaneSystem.CreateNodePedestrianLane` does. `SubLane` looks after itself:
`LaneReferencesSystem` adds any lane with an `Owner` to that owner's buffer, generically.

**The path nodes.** `Lane.m_MiddleNode` is a `PathNode` on the owning node with an index no other
lane there is using; `LaneSystem` hands them out from zero as it lays a junction, so scanning every
`Lane` at the node for path nodes whose owner is that node and going one above the highest is
enough.

`m_StartNode` and `m_EndNode` were the part that looked hardest and turned out to be the easiest, by
not constructing them at all. **Read them off the crossings already standing at the junction.** An
existing crossing's `Lane.m_StartNode` already names the sidewalk lane it joins, by owner, lane index
and segment index; copying it puts the new lane's end on a graph node that is already in the
pedestrian network. What is being added is an edge between two existing vertices, which is the
smallest possible change to the graph and the only version of this that is obviously safe in a save.

The corresponding endpoint position comes from the same crossing's `Curve.m_Bezier.a` or `.d`, so
the drawn crossing and the walked one agree.

**Signals.** `TrafficLightInitializationSystem` reads a node's `SubLane` buffer and treats *any*
lane there with both `LaneSignal` and `PedestrianLane` as a pedestrian group, so an added lane is
picked up with no special handling — but only when the node carries `Updated`, and tagging the node
`Updated` from a mod is a trap (see below). Copying `LaneSignal` from a crossing already at the
junction gives the new lane a real group mask immediately, and the initialisation system corrects it
the next time the junction is updated for its own reasons.

**Never tag the node `Updated` to force that.** `PrepareCleanUpSystem` snapshots the tagged entities
at the *start* of a frame (it runs at the end of `MainLoop`, before `Modification1`) and
`CleanUpSystem` removes them at the end of it. A tag added during the modification phases therefore
misses the snapshot and is still there in the next frame's `Modification4`, where `LaneSystem` will
re-lay the node — deleting the lanes just added, which are then added again, which tags the node
again. That is an unbreakable loop.

**Phase ordering makes the rest free.** `LaneSystem` is in `Modification4`;
`LaneReferencesSystem`, `LaneOverlapSystem` and `TrafficLightInitializationSystem` are all in
`Modification4B`. A lane created in `Modification4` after `LaneSystem` is therefore in its junction's
`SubLane` buffer, has its overlaps worked out and has a signal group before the frame is over.

**The lane will be deleted, repeatedly, and that is fine.** `LaneSystem` rebuilds a node's lanes
from its composition and deletes whatever is left over, and a mod's lane is always left over. So the
lane is not the state — a tag on the junction is, and the lanes are laid again whenever none are
standing. That also makes them follow the geometry for free: the corners move, and the next pass
runs the diagonals between wherever they ended up.

**Delete with `Deleted`, never `DestroyEntity`.** The game's clean-up takes a lane out of every
buffer that refers to it before destroying it. Destroying it directly leaves the junction's `SubLane`
buffer pointing at nothing.

**And never create or delete a lane from a UI trigger.** This is the same trap wearing a different
hat, and it cost a rewrite. A `TriggerBinding` fires during `UIUpdate`, which is past every
`Modification` phase for that frame. A lane created there misses `LaneReferencesSystem`, so it never
enters its junction's `SubLane` buffer — and since `Created` is stripped at the end of the frame, it
never will. It is then invisible to everything that walks `SubLane`: no signal group, no overlaps,
and no way for the mod to find it again to remove it. A lane *deleted* there is worse: `Deleted` is
added after `LaneReferencesSystem` has run, so `CleanUpSystem` destroys the entity at the end of the
frame with the junction still pointing at it, and the next thing to re-lay that node indexes a
`ComponentLookup` with a dead entity. If the player saves first, the dangling reference goes into the
save, because `SubLane` is `IEmptySerializable` and rebuilt from what is there.

So a button press records what is wanted and nothing else; the system's own `OnUpdate`, which is in
`Modification4`, is the only place that touches entities. The settings-menu buttons in this mod
already worked this way — static request flags read in `Modification1` — which is where the pattern
came from.

## Never tidy up on the player's behalf when the thing you would tidy is suspect

Shipped in 0.10.8: with the feature switched off, clear any lanes it had left in the city, gently, a
few per update. It reads as obviously good — the save cleans itself, the player does nothing.

It made the city unopenable. Load, wait three seconds, the clearing starts, the game dies. Every
load, with no window in which to reach the setting and stop it. The previous failure cost a session;
this one cost the save.

The rule that comes out of it: **automatic cleanup of something already implicated in a crash turns
one bad save into a permanent one.** If a mod's own data might be what is killing the game, removing
it has to be a thing the player asks for, at a moment they pick — not something that happens to them
on load. The manual buttons were already there; the automatic path added nothing except a way to
lock someone out of their city.

## Do nothing during a load, and nothing for a while after one

A save loads by laying the whole city's lanes. A mod system that creates lanes and runs every frame
joins in while that is happening — the log showed diagonals being laid *during* the load, then
cleared and laid again the instant `OnGameLoadingComplete` fired. Three rounds of creating and
deleting lanes across a city, the last landing as the game finished building its pedestrian network,
and the game died on load.

Crashing on load is categorically worse than crashing in play: it locks the player out of the city
instead of costing them a session. So this system is inert until `OnGameLoadingComplete` reports
`GameMode.Game`, and then waits ~180 frames before touching anything. Nothing it does is urgent.

And when it does clear the city's lanes, it clears a handful per update rather than the lot in one
`EntityManager.AddComponent(query, ...)`. A whole city's worth of deletions in a single frame is
exactly the change that turned a crash on unpause into a crash on load.

## A lane the mod saved is a lane an old version wrote

The diagonals are saved with the city, and the rule for laying them again is "when none are standing
at this junction". Both of those are reasonable on their own and together they mean **a fix never
reaches a save**: the faulty lanes are still standing, so nothing replaces them, and they come back
untouched every load carrying whatever the build that wrote them decided.

This is how the byte-index fix below appeared not to work. It was correct, and every diagonal laid
after it was fine — but the city was full of diagonals from before it, and the pathfinder walks all
of them. The one junction the player happened to remove and redraw was the only one that got the
corrected code.

So on `OnGameLoadingComplete` in `GameMode.Game`, every `CrosswalkAdded` lane in the city is marked
`Deleted` and every junction is laid again. Hold a few frames before laying: the marked lanes are not
gone until the end of the frame and are still in their junctions' `SubLane` buffers until the game
takes them out.

The general rule: **if a mod persists something it generates, decide how a later version replaces
it.** "Regenerate when missing" is not enough, because the broken thing is never missing.

## A junction's lane list is indexed by number, and those numbers are not in it

**This was the cause of every crash in the scramble feature, and it is not in the mod's code.**

`TrafficLightInitializationSystem.InitializeTrafficLights`:

```csharp
for (int j = laneGroup.m_LaneRange.x; j <= laneGroup.m_LaneRange.y; j++)
{
    Entity subLane = subLanes[j].m_SubLane;               // no bounds check
    LaneSignal laneSignal = m_LaneSignalData[subLane];    // no HasComponent check
```

`m_LaneRange` for a vehicle group is `new int2(masterLane.m_MinIndex - 1, masterLane.m_MaxIndex)`,
and those come from `prefabCompositionLaneData.m_Index` — **the road's composition**, decided when
LaneSystem laid the junction, not positions in the buffer as it currently stands. Both accesses are
unguarded, in a Burst job.

`LaneReferencesSystem.m_LanesQuery` is `All = {Lane, Owner}`: give a lane an `Owner` and the game
puts it in that owner's `SubLane` buffer. So a mod lane with an owner shifts every entry after it,
and those composition numbers then hit the wrong lane or run off the end. Adding one does it;
removing one does it in reverse. It fires whenever a junction's signals are recomputed — on load, on
unpause, on any edit — which is why the crash appeared in three unrelated-looking situations and why
no crash log ever named the mod.

**A lane this mod creates must therefore have no `Owner`.** It records its junction on a component of
its own. What that costs is everything the game does for a lane by reaching it through its junction:

- LaneSystem no longer sweeps it when re-laying a node, so the mod must clear its own lanes when a
  junction carries `Updated` — and when a junction is *deleted*, which `Updated` does not cover.
- The traffic light system never sees it, so it gets no `LaneSignal`. Do not add one: it would be set
  once and never driven again. `Crosswalk` with no signal is what the game makes at an unsignalled
  junction.
- Do **not** reach for `PedestrianLaneFlags.Unsafe` to express "no signal".
  `BatchInstanceSystem` turns that flag into `RequireSafe` and skips every sub-mesh carrying it —
  which is how an unmarked crossing draws no zebra. It would delete the paint.

## Changing a serialized component's shape breaks every save that has it

`CrosswalkAdded` shipped as `IEmptySerializable` — writes nothing, reads nothing. Giving it a payload
looks harmless and is not: CS2's entity stream has no per-component framing, so every component's
bytes are found by everything before it having read exactly what it wrote. A save containing the
old, empty component would have the new reader consume bytes belonging to the next component along,
and everything after it in the chunk.

A version field cannot save you, because the version is itself read from a stream that has none. The
fix is a **new component type** for the new data. The old one keeps its shape forever.

## A node lane's index is two numbers: a block and a place in it

This is what caused the crashes, and it took three wrong answers to get to.

`LaneSystem` walks a junction handing out lane indices from a running counter, `prevLaneIndex`, and
advances it **256 at a time** between one kind of lane and the next:

```csharp
int prevLaneIndex = 0;                              // line 820
...
prevLaneIndex += 256;                               // lines 1029, 1054, 1341
m_SegmentIndex = (byte)(prevLaneIndex >> 8);        // line 1273
...
laneData.m_Index = (byte)(m_LaneData[subLane].m_MiddleNode.GetLaneIndex() & 0xFF);   // line 5266
```

So the sixteen bits are **two numbers**. The high byte is which block of lanes at this junction the
lane belongs to — car lanes, pedestrian lanes, connection lanes each get their own — and the low
byte is its place within that block. In a real city a junction's crossings read 1537, 1539, 1541,
1543: block 6, places 1, 3, 5, 7.

Three attempts, two of them wrong:

1. **"One above the highest anywhere."** Landed just past the last block used. Roughly right by
   accident, never checked, and it could as easily have landed in the next block along.
2. **"The lowest free low byte."** Worse. Reading the low byte alone and picking the lowest free one
   gave index 72 — block 0, place 72. Block 0 is the *car lane* block. The crossing declared itself
   part of a piece of the junction it has nothing to do with. This version was shipped as a crash
   fix and made the crash more likely.
3. **A free place inside the block the junction's own crossings use.** Read the block off an existing
   crossing (`GetLaneIndex() & 0xFF00`), collect the used places within that block only, take a free
   one above the highest, compose `block | place`.

Keep the two halves apart in the caller as well. The allocator returns the composed value; only the
low byte is a position in the used-places array. Conflating them made the loop bail on the first
diagonal at every junction not in block zero — laying nothing, silently, which is how a fix can look
like a fix and do nothing at all.

The lesson twice over: **check what the game does with a field before choosing a value for it.**
"Over-estimating is the safe direction" and "the low byte is what matters" were both guesses dressed
up as reasoning, and each shipped as a fix for the crash the previous one caused.

## Corners are counted, not measured

A crossing spans one road's mouth, so its two ends sit either side of that road — not at a corner of
the junction. Two crossings meeting at the same corner end a few metres apart, at the edges of their
own roads.

The obvious move is to group the ends into corners by proximity. **It does not work, at any radius.**
On a six-lane boulevard the two ends at one corner are further apart than the entire width of a side
street, so a radius wide enough for the boulevard swallows the side street whole and a radius tight
enough for the side street leaves the boulevard with eight corners instead of four. Scaling the
radius to the shortest crossing at the junction does not rescue it either — that was shipped in
0.10.0 and the log showed a four-crossing junction being read as **seven** corners, and another as
five. Seven corners pair up into three diagonals where there should be two; five pairs corners that
are not opposite at all, so the diagonals sit off to one side of the junction and miss the middle.

The fix is to stop measuring. Sort the crossings by the angle of their midpoint around
`Node.m_Position`, and every neighbouring pair of crossings has exactly one corner between them:
crossing i's near end meets crossing i+1's near end. Four crossings give four corners on a junction
of any size. "Near end" is whichever of a crossing's two ends is closer to the *other* crossing's
midpoint, and of the two ends that meet there, the one further from the node centre is the one
nearer the corner itself — and its `PathNode` is one an existing crossing already ends on.

Ordering the corners by angle and joining each to the one half way round the ring then gives two
diagonals for four corners and three for six — the shape a scramble crossing actually has, rather
than every corner joined to every other. Use `count / 2` exactly: rounding the half up gives one
corner two diagonals and another none.

Two guards sit behind that for junction shapes not yet seen: reject a pair less than about 100
degrees apart (a corner being cut, not a junction crossed), and reject a line that misses the node
centre by more than 0.7 of the corners' mean distance from it (running around the outside). Both
have to stay loose — a tidy four-way's opposite corners are 180 degrees apart, but a five-way's are
nearer 115 and a lopsided four-way's around 130, and a threshold tight enough to look clever starts
refusing junctions that are perfectly fine.

## A junction is not flat, and a diagonal is long enough to notice

A road is crowned: its centre sits higher than its kerbs. A junction is two crowns meeting, so the
highest ground in it is the middle.

The game's own crossings are drawn as `NetUtils.StraightCurve` between the two kerbs of one road's
mouth, with no height correction at all — over a few metres the sag below the crown is a couple of
centimetres and nothing shows. A diagonal runs corner to corner across the whole junction, from kerb
level to kerb level, and the same straight line passes under the crown by enough to bury the painted
band: it tapers to nothing halfway across and comes back out the other side.

`NodeGeometry.m_Position` is the fix — despite the name it is a **height**, filled by
`GeometrySystem.InitializeNodeGeometryJob` from the node's own y (flattened where the junction has
been levelled). Raise the curve's two middle control points until the curve passes through it at the
midpoint. A cubic at its midpoint is `(a + 3b + 3c + d) / 8`, so moving b and c by four thirds of the
shortfall moves the midpoint by exactly the shortfall — the same four thirds `LaneSystem` uses in
`ModifyCurveHeight` for the lanes that run around the inside of a node.

Raise only, never lower, and add a few centimetres of clearance. The real surface between the middle
and each corner is two crowned roads and the fillet between them, which no cubic describes exactly,
so a curve pinned only at the midpoint still runs a shade under in the quarters. And if the height
read back is ever wrong, a band hovering slightly over the road is barely visible while one slightly
under it is not visible at all.

## Added lanes stay out of the per-crossing numbering, and are numbered in their own range

Per-crossing overrides are keyed by a crossing's position in the junction's `SubLane` order. A lane
that comes and goes with every re-lay must not be in that order, or turning a scramble on would
shift every override after it and move a junction's hand-set widths onto different crossings. So
`BuildOrder` skips anything carrying the mod's own marker.

They still need a key of their own, or they cannot be edited at all. That key is
`kAddedIndexBase + m_Ordinal` — a range deliberately above anything a lane list can reach. Two
consequences that are not obvious:

- Anything that judges an override key by the length of the lane list will delete every one of
  these, because every one of them is above it by design. `PruneOverrides` skips the added range
  outright; it has nothing to prune there, since these keys are not positions in anything.
- The key has to survive the lane being laid again, which happens constantly. An entity does not
  survive it and neither does a position in a list, so the number is the corner the diagonal starts
  from. See below.

## A number that survives a re-lay is not a position in a list

Middle crossings are deleted and laid again every time their junction changes shape. Whatever a
hand-set width is stored under has to mean the same thing on the other side of that.

The first attempt used the diagonal's place in `m_Pairs`. That list is compacted — `ChoosePairs`
never adds a pair that sweeps too narrowly or misses the middle — so a junction that stops producing
one pair renumbers every pair after it, and a width set on the third diagonal moves to the second.

The number is the ring corner the diagonal starts from (`m_Pairs[i].x`) instead. `BuildCorners`
builds the ring the same way every time from the crossings standing there, so a corner keeps its
number for as long as the junction keeps its shape — and when the shape does change, the numbers
change honestly rather than sliding by one.

The same reasoning says what to do about the version of the save that had no number at all: 0.12.0
wrote these lanes before there was anything to number them by, so they all read back as zero. They
cannot be numbered in place, because the number comes out of laying the junction. `MigrateUnnumbered`
deletes them one junction at a time and lets them be laid again. That is safe here in a way the
owned-lane migration is not: these lanes are in no junction's lane list, so deleting them moves
nothing, and the junction is never tagged `Updated`.

## Record the removal before deleting the lane

The junction stays tagged when one of its diagonals is removed by hand, so the next reconcile finds
a diagonal missing and lays it. The only thing that stops it is the removal mask on
`CrosswalkScramble`. Delete the lane without writing that mask — because the junction lost the
component, or the ordinal is out of range — and the player gets a crossing that will not go away,
deleted and re-laid every few seconds. `RemoveOneNow` writes the record first and bails if it
cannot.

## Pick a crossing by its band, never by its midpoint

Hit-testing crossings by "nearest midpoint within N metres" is wrong even before diagonals exist. A
crossing is a band, often twenty metres long; its midpoint is one point on it. Point near the end of
one and you are nearer the neighbouring crossing's midpoint than your own, so the neighbour gets
picked. A generous radius makes it worse, not better.

The diagonals made it unmissable: they cross at the junction's centre by construction, so every
diagonal's midpoint is within a metre or two of every other midpoint at that junction. Which one a
click landed on was effectively arbitrary.

Measure to the curve instead, and subtract half the drawn width: that is the distance to the painted
edge, negative inside the paint. It picks the crossing you are standing on, and where two overlap it
picks the one whose centre you are nearer *relative to that crossing's own width*. Sample the bezier
as a short polyline — sixteen pieces is far more than enough at this scale, and it cannot fail to
converge the way a solver can on a curve that barely bends. Ignore height: the cursor is a terrain
hit and a diagonal is deliberately lifted over the crown of the junction, so including height reports
every diagonal as further away than it looks.

## A handle you can see is a handle you can hit — including the ones off the paint

Four of the tool's five handles sit on the painted band. The two side handles do not: they sit
`halfWidth + clearance` off the centreline, which is past the distance at which the cursor counts as
being on that crossing at all.

So a press on a side handle cannot be made conditional on "and the cursor is nearest this crossing".
It reads as a reasonable safety check and it is not: on a scramble the *other* diagonal runs through
the middle of this one, and at 90° its centreline passes exactly through both side handles. Every
press then swapped which diagonal was live — while the handle stayed lit under the cursor, because
the hot-handle test never consulted the nearest-crossing test.

The rule is a precedence rule, and it has to be unconditional: a handle drawn for the live crossing
belongs to the live crossing. Anything else re-introduces the zone-based ambiguity the handles were
introduced to remove. This has now been got wrong twice, in opposite directions.

## Picking and drawing must answer the same question

A crossing whose prefab the catalog does not know has an authored width of zero, so it is drawn as
nothing. If the pick test floors the width at some minimum, that invisible crossing stays selectable
and the panel reports "crossing 3 of 5" for something the player cannot see, with handles hanging off
a band of no width. One predicate, used by both.

## `hasAppend` does not mean "is this target available"

`ModuleRegistry.hasAppend(target)` reports whether something has **already been appended** to that
target, not whether the target exists. `types/modding.d.ts` gives only the signature —
`hasAppend(target: AppendHookTargets): boolean` — so the name is the only clue, and the obvious
reading of it is wrong.

Guarding the registration with it is therefore worse than not guarding at all:

```tsx
if (!moduleRegistry.hasAppend("GameTopLeft")) { return; }   // never registers if it registers first
moduleRegistry.append("GameTopLeft", CrosswalkToolButton);
```

This mod registers early, before any other mod has appended anything, so the check read false and
the button was never added. `UI.log` had it plainly: this mod declaring GameTopLeft unavailable at
11:40:49,320, and TownRoadLane appending its own GameTopLeft button at 11:40:49,324. It also
depended on load order, which is why it worked for weeks and then stopped — the failure arrived with
an unrelated mod install, not with a change here.

Append inside a `try`/`catch` instead. That needs no assumption about an API nobody has read, and it
still logs rather than failing silently — which was the whole point of the guard.

The general form: a guard that has to be right about an undocumented API is not a safety net, it is
a second thing that can fail, and it fails closed.
