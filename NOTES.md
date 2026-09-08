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
