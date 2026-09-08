# Crosswalk Width

A Cities: Skylines II code mod that makes pedestrian crossings wider.

Crossings in Cities: Skylines II are a thin painted band that a queue of citizens shuffles across
in single file. This puts a slider on how wide they are — more paint on the junction, and room for
people to cross side by side.

## What you get

| Setting | What it does |
| --- | --- |
| **Crossing width** | A percentage of the crossing's own width, 25%–400%. 100% is the base game. |
| **Minimum width** | A floor in metres, applied after the percentage. 0 turns it off. |
| **Maximum width** | A ceiling in metres, applied after the percentage. 0 turns it off. |
| **Apply to existing crossings** | Re-lays the whole city's crossings without reloading. |
| **List crossings in the log** | Writes every crossing lane found, before and after, to the game log. |

A percentage rather than an absolute figure because crossings are not uniform to begin with — a
side street's is narrow, a boulevard's is not — and one absolute value would flatten that. The
floor and ceiling cover the cases where a proportion is not what you want.

## How it works

A crossing is not part of the road's cross-section at all. It is a **lane laid across the
carriageway**:

- `NetPieceCrosswalk` on a road piece declares a span across the road and names a lane prefab,
  which becomes `NetCrosswalkData` on that piece.
- `AddCompositionCrosswalks` merges adjacent spans into one `NetCompositionCrosswalk` per
  composition.
- `LaneSystem` lays a pedestrian lane along that span at every node.

The painted zebra is that lane's mesh, and a lane is drawn at `NetLaneData.m_Width` — `LaneSystem`
defines `NodeLane.m_WidthOffset` as the *difference* between two lane prefabs' widths, which only
means anything if that field is the width the lane is drawn at. Because a crossing lane runs across
the road, its width is the depth of the band in the direction traffic travels.

So one field covers both halves of what you want: more paint on the ground, and a wider strip for
people to walk on. This mod rewrites that field on the crossing lane prefabs and lets the game's
own lane pipeline do everything else — no geometry of its own, no Harmony patches.

## Applying it

Crossings laid after you change the setting are correct immediately.

Crossings already standing were laid at the old width, and `LaneSystem` only revisits a node when
something marks it updated. Two ways to deal with that:

- **Reload the save.** Everything is laid again from scratch. Always works.
- **Press "Apply to existing crossings".** Hands every node and edge back to the lane pipeline in
  one go. Faster, and it pauses the game for a moment on a large city.

Unlike widening a road's cross-section, nothing about the road composition changes here — crossing
width lives on the lane prefab, not in `NetCompositionCrosswalk`, which carries only the span and
the lane reference. So this cannot leave a road half-rebuilt, and the road itself never gets wider.

## Building

Requires the official Cities: Skylines II modding toolchain, which sets `CSII_TOOLPATH` and the
other `CSII_*` environment variables the project imports.

```
dotnet build CS2-CrosswalkWidth.sln -c Release
```

The toolchain's `DeployWIP` target copies the output into your local mods folder on build, so a
Release build is enough to try it in game.

## Compatibility

Anything else that rewrites the same crossing lane prefabs will conflict — last writer wins.
Mods that add or remove crossings, or change where they sit, are fine: they work on the span and
the composition, which this mod does not touch.

Disabling the mod, or removing it, restores every authored width. That matters here because
`NetLaneData` is serialized into the save.
