# Better Crosswalks

A Cities: Skylines II code mod for pedestrian crossings: make them wider, shape them one at a time,
and add crossings corner to corner through the middle of a junction.

Crossings in Cities: Skylines II are a thin painted band that a queue of citizens shuffles across in
single file. This widens the band and the strip people walk on together, so a crowd spreads out
across it instead of filing over.

![A junction with crossings through its middle](CrosswalkWidth/Properties/Screenshots/01-scramble-junction.jpg)

## What it does

**Width, everywhere.** One slider, as a percentage of whatever width the crossing was drawn with
rather than an absolute figure — crossings are not uniform to begin with, a side street's is narrow
and a boulevard's is not, and one absolute value would flatten that. 150% out of the box, 25%–400%
by hand. A floor and a ceiling in metres are there for the cases where a proportion is not what you
want: "no crossing narrower than 4m" is a sensible rule, "every crossing exactly 4m" usually is not.

**One junction at a time.** The toolbar button opens a tool. Click a junction, then a crossing on
it, and five rings appear on that crossing:

| Ring | Drag it to |
| --- | --- |
| Either end | Swing that end up or down the road, for a crossing set at an angle rather than square across |
| The middle | Slide the whole crossing towards or away from the junction |
| Either side | Pull the crossing wider or narrower |

Nothing you do to one crossing touches its neighbours. The panel also has **−** and **+**, *Same for
all N crossings here*, and a reset for the crossing or the whole junction. Everything set here is
saved with the city.

**Crossings through the middle.** Junctions where four or more roads meet can take crossings corner
to corner through the middle — a scramble crossing, the kind Shibuya is known for. On by default,
and the tool edits and removes them like any other crossing.

## How it works

A crossing is not part of the road's cross-section. It is a **lane laid across the carriageway**:

- `NetPieceCrosswalk` on a road piece declares a span across the road and names a lane prefab, which
  becomes `NetCrosswalkData` on that piece.
- `AddCompositionCrosswalks` merges adjacent spans into one `NetCompositionCrosswalk` per
  composition.
- `LaneSystem` lays a pedestrian lane along that span at every node.

A crossing lane runs across the road, so its width is the depth of the painted band in the direction
traffic travels. That width is two numbers added together:

```
NetLaneData.m_Width  +  NodeLane.m_WidthOffset
```

and the two are read differently. `CreatureUtils.GetLaneOffset` takes the **sum**, which is what
spreads the walkers out. `BatchDataHelpers.BuildCurveScale` returns the **ratio**
`1 + m_WidthOffset / m_Width` and uses it as the mesh's lateral scale, which is what paints the
zebra. Move only the prefab's `m_Width` and both terms shift together: the crossing behaves wider
without ever looking it. **That is the mistake this mod made for several versions.**

So width is applied **per crossing**, by writing `NodeLane.m_WidthOffset` on the lane the game laid.
Note that field is not zero to begin with — `LaneSystem` writes `declaredWidth − variantWidth` into
it to hold a theme variant at the width the composition declared — so the value written is
`scale × laidWidth − variantWidth`, with `laidWidth` taken from the declaring placeholder. At 100%
that reproduces the game's own number exactly, which is what makes "off", "restore" and "remove my
data" the same code path as "apply".

No cloned prefabs, no shared prefab widths rewritten, no geometry of the mod's own, and no Harmony
patches. The road itself never gets wider.

Crossings through the middle are the one exception: those are lanes this mod creates. Nothing about
them is invented — the entity comes from `NetLaneArchetypeData.m_NodeLaneArchetype`, and its path
nodes, prefab, flags, signal and `NodeLane` are copied off crossings already standing at that
junction, so the diagonal is an edge between two vertices already in the pedestrian graph and takes
the same paint, width rules and green phase. They deliberately carry **no `Owner`**; see
[NOTES.md](NOTES.md) for why that one detail is the difference between working and taking the game
down.

## Applying it

Change the setting and the whole city follows on the next frame — crossings already standing
included. There is nothing to press and nothing to reload.

That is worth saying because it used to be false, and because the obvious mechanism is the wrong
one. Width lives on lanes that are already laid, so applying it is a sweep that writes one number
per crossing; it does **not** hand nodes back to `LaneSystem` to be laid again. The old route did,
and it destroyed and rebuilt every lane at every junction in the city to change one number on each.

*Apply to existing crossings* therefore does nothing in normal use. It re-requests that sweep, which
is worth having if a city ever ends up in a state where a crossing was missed.

## Removing it

**Maintenance → Remove this mod's data from the city**, then save. Every crossing goes back to the
width its asset author chose, every per-junction width is forgotten, every crossing this mod added
is taken out, and the mod switches itself off so nothing puts them back before you save. The save is
then exactly as it would have been if the mod had never run.

*Put every crossing back to normal* does the same to the city but leaves the mod switched on, so you
can carry on from a city that looks untouched.

## Compatibility

Requires Cities: Skylines II **1.6**.

Mods that add or remove crossings, or change where they sit, are fine: they work on the span and the
composition, neither of which this mod touches. Anything else that writes `NodeLane.m_WidthOffset`
on crossing lanes will conflict — last writer wins.

## Building

Requires the official Cities: Skylines II modding toolchain, which sets `CSII_TOOLPATH` and the
other `CSII_*` environment variables the project imports.

```
dotnet build CS2-CrosswalkWidth.sln -c Release
```

The UI half is built separately with npm — see [ui/README.md](ui/README.md) for first-time setup.
**Both halves have to be built**, and a stale UI bundle beside a fresh DLL loads without complaint
and silently behaves like the older version. Both deploy into the same folder, and the project
overrides the toolchain's `DeployWIP` target so that building one does not delete the other.

`Properties/thumbnail.py` draws the store cover; `pip install cairosvg` first.

## A note on the name

The assembly, the namespace and the settings key are all still `CrosswalkWidth`, and they stay that
way on purpose. The settings key is what the game files a player's saved settings under, and the
component type names are written into every city save that has used this mod — renaming them would
reset one and break the other. Only the name players see is *Better Crosswalks*.

## Notes

[NOTES.md](NOTES.md) is the record of what was expensive to learn: the unguarded indexing in
`TrafficLightInitializationSystem` behind six crashes, why a lane index is two numbers rather than a
counter, why a mod must never tag a node `Updated`, and the approaches that were tried and removed
so they are not tried again.
