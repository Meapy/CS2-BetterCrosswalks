# Changelog

1.0.0 is the first public release. Everything below it is the development history that led there,
kept because a good deal of it is the record of things that were got wrong first — and because the
two save migrations in the code exist to clean up after those versions.

## 1.1.0

**Lines down the sides of a crossing.** A new setting paints a solid line either side of the zebra
stripes, so a crossing reads as a bordered band rather than a row of loose bars. The lines sit on the
edge of the painted band and follow it, so a crossing you widen — with the slider or with the tool —
takes its borders with it.

Off out of the box. It changes how every crossing in the city looks, and unlike the width it is a
matter of taste rather than of the crossing working better.

Nothing here is drawn by this mod. The lines are the game's own road markings, laid by the game's own
marking system, so they are the paint the city's roads already use and they are created and destroyed
with their junction like every other marking. The whole feature is one entry written onto each
crossing lane prefab, and prefabs are not saved with a city, so a save gains nothing it would not
have had anyway.

They appear only where the game paints a zebra. A junction is given a crossing lane whether or not
anybody asked for a crossing on it, and the unasked-for ones draw nothing; so is the lane the game
substitutes where there is no pavement to step onto, which is what a bridge or an elevated road
usually has. Without both of those exclusions the mod put two lines across a road with nothing
between them. The line itself is a thin solid one rather than a stop line's bar.

A **Line style** dropdown picks between the markings found, for the case where the automatic choice
is not the one you want — a marking belonging to a theme your city is not using is refused by the
game without saying so.

**Crossings through the middle get lines too.** They could not at first: the game lays lines by
walking each junction's lane list, and these crossings are deliberately kept out of it, which is what
stopped them crashing the game. So the mod lays theirs itself, at the offset the game uses, and works
them out afresh every pass. That last part makes them better behaved than the game's own — drag a
middle crossing wider and its lines follow at once, with no junction rebuild.

**"Apply to existing crossings" is now "Apply to existing crossings and lines"** and hands every road
back to the game when the lines are on. The game's lines are laid when a junction is built and are
saved with the city, so a junction keeps the lines it was built with until something rebuilds it.
Press it once after updating to bring a city you already have up to date; expect a pause on a large
one.

**Crossings through the middle of a junction built from divided roads now reach the far corner.** A
road with a median is not crossed in one span — the median breaks the run, so the game lays two
crossings across that road's mouth rather than one. Everything downstream counted crossings and
believed each one was an arm, so a four-arm junction of divided roads was read as having eight
corners, four of them sitting on a median, and a crossing then ran from a real corner to the middle of
a road. The two halves of a road's mouth are now put back together before the corners are worked out.

One thing to expect from that: it changes how many crossings through the middle such a junction has,
and their numbering with it, so a width or a position set by hand on one of them there does not
survive the change. The old numbers described corners that were never there.

**"List crossings in the log" says considerably more.** It now also writes out every marking that
could serve as a crossing's border, with its thickness and what the game itself uses it for, and
every pedestrian lane prefab standing in the city — how many of its lanes are marked crossings, how
many are not, whether it has any paint at all, and whether this mod has bordered it. That is the list
to read if a line ever turns up somewhere it should not.

## 1.0.1

**The crossing tool is reachable again.** 1.0.0 went out without its toolbar button, and that button
is the only way to open the tool — so every per-junction feature was unusable: selecting a junction,
resizing one crossing, adding or removing crossings through the middle. The width settings were
unaffected.

`moduleRegistry.hasAppend(target)` reports whether something has *already been appended* to a target,
not whether the target exists. This mod registers early, so the check read false and the
registration was skipped. It depended on load order, which is why it survived testing and then did
not. Replaced with a plain `append` in a `try`/`catch`. See NOTES.md.

The listing also gains a fourth screenshot, of the tool, which could not be taken while the tool
could not be opened.

## 1.0.0

First public release: **Better Crosswalks**.

Pedestrian crossings you can make wider, shape one at a time, and run corner to corner through the
middle of a junction.

- **Width** as a percentage of whatever the crossing was drawn with, 25% to 400%, starting at 150%,
  with an optional floor and ceiling in metres. The painted band and the strip people walk on widen
  together — moving only the lane prefab's width widens one without the other, which is what made
  several early versions look like they were doing nothing.
- **A tool for one junction at a time.** Click a junction, click a crossing, and drag its ends to
  set it at an angle, its middle to slide it along the road, or its sides to resize it. Nothing done
  to one crossing touches its neighbours, and everything is saved with the city.
- **Crossings through the middle** of junctions where four or more roads meet, on by default,
  editable and removable like any other crossing.
- **"Remove this mod's data from the city"** puts everything back exactly as the game would have
  laid it, for uninstalling cleanly.

Renamed from *Crosswalk Width* for the release. The assembly, namespace and settings key stay
`CrosswalkWidth`: the settings key is what the game files saved settings under, and the component
type names are in every save that has used the mod.

Fixed on the way to the release:

- **The toolbar button did not appear at all**, which meant no crossing tool. The UI module guarded
  its own registration with `moduleRegistry.hasAppend("GameTopLeft")`, on the assumption that this
  reports whether the target exists. It reports whether something has *already been appended* to it,
  so a mod that registers early — as this one does — reads false and refuses to register. It
  depended on load order, so it worked until enough other mods were installed. The registration is
  now a plain `append` in a `try`/`catch`.

- Two labels in the tool panel rendered as `Same for all 7crossings here` and a heading broken
  across three lines. The game lays its interface out with flexbox, so a label written as JSX text
  with an `{expression}` in it arrives as several text nodes and becomes several flex items. Every
  label that contains a value is now one template literal.
- The readme, the store page and two settings descriptions all said that crossings already standing
  keep the old width until you reload or press "Apply to existing crossings". They have not since
  0.7.0 — a settings change sweeps the whole city on the next frame. The button is close to
  redundant and now says so.
- The readme explained the mod by the mechanism that was *abandoned*: rewriting the lane prefab's
  width. That widens the walking strip without widening the paint, which is what made several early
  versions look like they did nothing. Width is written per crossing, on the lane.
- The per-lane geometry dump moved from Info to Debug. It runs on every junction that gets
  diagonals, which on a city-wide first pass is hundreds of them several lines each.
- The mod logs its version at startup.

Two migrations run once on load and then never again, for cities built with pre-release versions:
one clears crossings that were given an `Owner` (the cause of the crashes fixed in 0.12.0) and hands
their junction back to the game, the other re-lays crossings that were saved before there was
anything to number them by. Neither can trigger on a save that has only ever seen 1.0.0.

## 0.13.4

The settings tab is down to two groups, Crossings and Maintenance, from four.

- **Removed "Start the crossing tool".** The toolbar button at the top left does this, and this one
  still described a Page Up / Page Down workflow that stopped working when the drag handles replaced
  it — the game only enables those keys for a tool whose panel offers elevation, so for a mod tool
  they never fire at all. Its explanation of how the tool works has moved onto "Tool step size",
  which is where someone looking in settings for it will now land.
- **Removed "Clear all per-junction widths".** "Put every crossing back to normal" covers it, and
  the tool has a per-junction reset of its own.
- The middle-crossings switch is no longer labelled experimental and no longer claims to be off by
  default. It has been on since 0.13.0.
- "Tool step size" no longer talks about Page Up and Page Down.
- The plumbing behind the two removed buttons went with them, and the purge comment no longer quotes
  200% as the default width.

## 0.13.3

- **Crossings are 150% wide out of the box, down from 200%.** Half as wide again is enough to see on
  a junction and to let people cross two abreast, while still sitting inside the paving the game
  laid — which double width does not, on the narrower roads. This only affects a fresh install and
  the "reset to defaults" button; a city that already has a saved setting keeps the number it has.
- The comment on the middle-crossings switch still said they were off by default, which stopped
  being true in 0.13.0.

## 0.13.2

- **The mod is now called Better Crosswalks.** Only the name shown to players changed — the settings
  menu, the Paradox Mods listing and the readme. The assembly, the namespace and the settings key
  are still `CrosswalkWidth` on purpose: the settings key is what the game files a player's saved
  settings under, and the component names are written into every city save that has used this mod.
- A cover image for the Paradox Mods listing, at `Properties/Thumbnail.png`. `thumbnail.py` beside it
  draws it, so it can be changed without an image editor. The subject is a junction with crossings
  through its middle; the ordinary crossings on the arms are drawn dimmer and shallower and the two
  middle ones brighter and wider, so the feature reads without painting anything a colour a crossing
  could not be.
- The publish configuration's description covers the tool and the middle crossings, which it had not
  caught up with.

## 0.13.1

Clicking a crossing now selects that crossing.

- **Crossings are picked by their painted band, not by their middle.** Picking used to be "whichever
  crossing's midpoint is nearest, within fourteen metres". A midpoint is one point on a band twenty
  metres long, so pointing near the end of a crossing was nearer the *neighbouring* crossing's
  middle and picked that one. The middle crossings turned that from inaccurate into arbitrary: two
  diagonals cross at the junction's centre, so all their midpoints sit within a metre or two of each
  other. The cursor's distance is now measured to the whole curve, less half of how wide that
  crossing is drawn — so the crossing you are standing on wins, and where two overlap, the one whose
  centre you are nearer.
- **The side handles resize again.** A press on any of the live crossing's five handles now acts on
  that crossing instead of deferring to whichever band the cursor is nearest. The two side handles
  sit clear of the paint, and on a scramble the *other* diagonal's paint covers them both — so each
  press just swapped which diagonal was live, while the handle stayed lit under the cursor.
- Middle crossings are drawn at their own width. The drawing read the width by list position rather
  than by the crossing's own key, so each middle crossing was drawn at whatever width the junction's
  crossing in that position was set to.
- A crossing the catalog does not know is drawn as nothing, and is now unselectable to match, rather
  than being an invisible thing the panel would report as selected.

## 0.13.0

Middle crossings are now part of the tool rather than something the tool has to ignore, and they are
on by default.

- **Middle crossings can be edited.** Select one with the crossing tool and it resizes, moves and
  swings like any other crossing. They were invisible to the tool before, because the tool numbers a
  junction's crossings by their place in its lane list and these are deliberately not in it. They are
  numbered by which diagonal they are instead — a number that survives the junction being laid again,
  which a position in a list does not.
- **"Remove this crossing."** Takes out the middle crossing being edited, at that junction only, and
  it stays out: the junction remembers which of its diagonals were removed by hand, so the next
  rebuild does not quietly put it back. Only middle crossings can go — the junction's own crossings
  come from the road it is built with, and the game would lay them again immediately.
- **Middle crossings are enabled by default.** The setting is still there to turn them off.

Fixes that came out of reviewing the above before shipping it:

- A width set on a middle crossing is no longer thrown away the moment anything else at that junction
  is edited. The pass that drops stale entries judged them against the length of the junction's lane
  list, and every middle crossing is numbered above it by design.
- Diagonals in a 0.12.0 save are laid again once on load. They were written before there was anything
  to number them by, so all of them read back as number one — meaning one width shared between every
  diagonal at a junction, with no way to tell them apart. They blink once and come back numbered.
- Diagonals keep their number when a junction loses one. It used to be their place in a list that
  skips the pairs it rejects, so losing one moved every number after it and a width set by hand
  jumped to the neighbour. It is now the corner the diagonal starts from.
- Turning off a junction's overrides no longer strands a middle crossing that had been dragged. The
  restore pass only walked the junction's own crossings, and the override it was about to delete was
  the only record of where the diagonal started out.
- Dragging a middle crossing wider now starts from its own width rather than whatever the junction's
  crossing in that position happens to be set to.
- Middle crossings are listed in a fixed order. The query behind them hands back whatever order the
  lanes happen to sit in, which changes as lanes come and go, and that could move a crossing out from
  under the selection.
- A middle crossing is no longer deleted when there is nowhere to record that it was removed — it
  would have been laid again seconds later, over and over.

## 0.12.0

**The crashes are understood. This is the fix, and it is not another guess — it is a line of the
game's own code.**

`TrafficLightInitializationSystem` walks a junction's list of lanes like this:

```
for (int j = laneGroup.m_LaneRange.x; j <= laneGroup.m_LaneRange.y; j++)
{
    Entity subLane = subLanes[j].m_SubLane;              // no bounds check
    LaneSignal laneSignal = m_LaneSignalData[subLane];   // no "does it have one" check
```

The range is not read from that list. It comes from the road's composition, worked out when the game
laid the junction. Both accesses are unchecked, and both run inside a compiled job where a bad access
is not an error message — it is the process disappearing.

Every middle crossing this mod laid was being inserted into that list. That moves everything after it,
so those numbers land on the wrong lane, or past the end. Removing one does the same in reverse. That
is why the game died on unpausing, on loading, and on clicking a junction — all three are moments
when a junction's signals are worked out again — and why nothing ever named this mod in a crash log.

- **Middle crossings are no longer attached to their junction.** They record which junction they
  belong to on themselves instead. A crossing that is not in that list cannot move anything the game
  is counting on, and nothing indexes it.
- **They carry no traffic light of their own.** A signal is only driven for lanes in that list, so one
  here would be set once and never change — a crossing stuck on red forever. They now behave as
  crossings at an unsignalled junction do, which is a state the game makes for itself.
- **Crossings left in a save by an earlier version are migrated on load**, one junction at a time,
  and each junction is handed back to the game to rebuild so its lane numbering is worked out again
  against what is actually there.
- The mod now clears its own crossings when a junction is rebuilt or bulldozed. The game used to do
  that for it, as a side effect of them being attached — which is exactly the attachment that had to
  go.

Turn **Middle crossings** on under Width. If a save still contains crossings from an earlier version,
load it, leave it paused for a few seconds and watch the log migrate them before doing anything else.

## 0.11.1

- **Fixed: a save with middle crossings in it could not be opened at all.** 0.10.8 had the mod clear
  them away automatically whenever the feature was switched off. The log shows what that meant: load
  the city, the mod starts deleting them a few seconds later, and the game goes down — every time,
  with no way to get in and stop it. One bad save became a city you cannot open. That was my change
  and it was the wrong instinct: tidying up without being asked is exactly what you must not do when
  the thing being tidied is implicated in a crash.

  Switched off now means the system does nothing at all — it neither lays middle crossings nor
  removes ones already there. Removal is something you ask for, when you choose, from the two
  buttons that already existed: **Put every crossing back to normal**, and **Remove this mod's data
  from the city**. Do it paused, with the crossing tool closed.

## 0.11.0

- **Fixed, properly this time: the crash when the simulation runs.** The number identifying a
  crossing within its junction is not a plain counter, which is what both earlier attempts assumed.
  It is two numbers packed together: the top half says which *block* of lanes at that junction the
  crossing belongs to — the game gives car lanes, footpaths and connections each their own block of
  256 — and the bottom half is its place inside that block. A junction's crossings read 1537, 1539,
  1541, 1543: block 6, places 1, 3, 5 and 7.

  0.10.6 read the bottom half on its own and picked the lowest free place, which gave index 72 —
  block 0, place 72. Block 0 is the *car lane* block. Every middle crossing was declaring itself part
  of a piece of the junction it had nothing to do with, and that version shipped as a fix for the
  crash while making it more likely.

  A middle crossing now takes a free place inside the same block the junction's own crossings use.

- Middle crossings stay behind a switch under Width, and are off until you turn them on. The fault
  is understood and fixed, but a switch that turns the feature off and clears it out of a city is
  worth keeping. Turning it on is all that is needed; nothing else has to be redone.

## 0.10.8

- **Fixed: crashing on load, which 0.10.7 caused.** That version cleared and re-laid every middle
  crossing the moment a save reported finished loading. The log shows what that meant: diagonals
  were being laid *during* the load, then cleared and laid again a second later, three rounds of
  creating and deleting lanes across the city with the last landing exactly as the game finished
  building its pedestrian network. Crashing on load is the worst way for this to fail — it locks you
  out of the city rather than costing you a session — and it was my change that did it.

  Nothing now happens during a load, or for three seconds after one. Nothing about middle crossings
  is urgent, and a few seconds of quiet costs nothing.

- **Middle crossings are now off by default**, under Width in the settings, marked experimental.
  This is the only part of the mod that *creates* crossings rather than resizing the ones the game
  laid, and a crossing it creates joins the pedestrian network the simulation runs on. It has now
  crashed the game four times while being got right. Everything else — width, the floor and ceiling,
  the per-junction tool, moving and resizing individual crossings — is untouched by this and has
  never been implicated.

- **Turning the setting off clears any middle crossings already in the city**, a few at a time rather
  than all in one frame, so a save that has them can be opened and cleaned without the burst of
  deletions that caused the load crash. Leave the setting off, load the save, wait a few seconds, and
  the city is clean.

## 0.10.7

- **Middle crossings are now laid fresh every time a city loads.** They were saved with the city and
  only ever replaced when a junction had none left standing — so a diagonal written by an older build
  of this mod survived a load untouched and kept whatever that build gave it.

  That is why 0.10.6's fix did not reach your city. It corrected how a crossing's index into the
  pedestrian network is chosen, but every diagonal already in the save kept the index it was born
  with, and nothing short of turning each junction off and on by hand would have replaced it. The
  one junction you removed and redrew got the corrected version; every other one in the city was
  still carrying the old fault, and unpausing runs the pathfinder over all of them.

  From here on, loading a save is enough: every diagonal in the city is cleared and laid again by the
  version actually running. It also means no more redoing junctions by hand to pick up a fix.

## 0.10.6

- **Fixed: the game crashing when the simulation was unpaused after spawning a middle crossing.**
  This was the mod's fault, and the cause is worth stating exactly.

  Every crossing at a junction carries an index that identifies it in the pedestrian path network,
  and two crossings sharing one are the same place as far as the pathfinder is concerned. The mod
  picked its index by taking the highest one already in use and adding one — which looked safe and
  was not, for two reasons. The number it read back has a second value packed into its upper half,
  so "the highest in use" came out in the thousands rather than the tens. And the game keeps only
  the bottom eight bits of that index, so a diagonal at 1027 became crossing 3 to everything
  downstream — a number a real crossing at that junction was already using.

  Nothing shows while the game is paused. The crossing is laid, the paint draws, the log reads
  correctly. Unpause, the pathfinder reaches a junction where two crossings claim one place, and the
  game is gone without even a stack trace to say why.

  The mod now collects the indices in use in the range that actually matters and takes the lowest
  free one, claiming each as it goes. If a junction has somehow used all 256, no diagonals are laid
  there and the log says so — a missing crossing being a far better outcome than a colliding one.

- The per-junction dump now includes each crossing's lane index, and shows the mod's own diagonals
  rather than only the road crossings; they are laid moments before the game adds them to the
  junction's list, so the previous version was reading them too early to see them.

## 0.10.5

- **A middle crossing's length is now measured along the curve, not corner to corner.** The renderer
  divides that length by the zebra mesh's own length to decide how many times to repeat it, so a
  length that does not match the curve lays the stripes at the wrong spacing. Once 0.10.2 started
  raising the middle of a diagonal, the two stopped being the same number.
- **The log now writes out everything the renderer reads for every crossing at a junction**, this
  mod's diagonals alongside the game's own crossings: length against chord, how far the middle rises,
  width offsets, lane flags, and whether each has a cut range or a signal. Three rounds of this
  feature's faults have looked like texture problems in a screenshot and turned out to be numbers, so
  a crossing that draws correctly can now be read side by side with one that does not.

## 0.10.4

- **A middle crossing is no longer built on a lane prefab the game cannot vouch for.** A crossing
  already standing is not proof that its prefab can carry another one: a custom crossing asset that
  failed to import leaves a prefab behind that the game no longer knows, and the crossings laid
  before it broke keep drawing from what is already in the render batches while a lane created
  against it now points at something that is not there. Before a diagonal is built, its pattern
  crossing's prefab has to still resolve to a real asset, still carry a lane archetype, and still be
  one this mod recognises. If neither crossing at a corner qualifies, no diagonal is laid there and
  the log says so once.
- **A corner is held within a few metres of the crossing end it is wired to.** The corner is the
  midpoint of two crossing ends, which is what keeps the scramble symmetric, but the connection into
  the pedestrian network has to be one of the two — a place in the graph cannot be averaged. On an
  ordinary junction the gap is a stride; on a very wide one it is now pulled back rather than left to
  grow.

## 0.10.3

- **Fixed: the X was lopsided and off centre.** Each corner was being placed at one of the two
  crossing ends that meet there — whichever happened to sit further out. Which of the two roads won
  was decided by a few centimetres and changed from corner to corner, so the four corners were
  pulled towards different roads and the diagonals crossed well off the middle of the junction. A
  corner is now the point halfway between the two ends, which has no such choice in it: opposite
  corners stay opposite, and the diagonals meet in the middle.
- Each diagonal now copies the whole of a neighbouring crossing's lane settings rather than starting
  from blank ones. Those settings carry more than the width — they also tell the renderer how the
  painted band behaves towards each end — and copying them from a crossing that is already drawing
  correctly at that junction is more reliable than filling them in from first principles.
- The log now records each junction's corner heights, its middle height, and how far a diagonal's
  middle had to be raised. A crossing sinking into the road looks like a texture fault and is
  actually a geometry one, and those three figures are what tell them apart.

## 0.10.2

- **Fixed: middle crossings sinking into the road.** On junctions that are not perfectly flat, a
  diagonal's paint tapered away to nothing halfway across and reappeared on the other side.

  A road is crowned — its centre sits higher than its kerbs — and a junction is two crowns meeting,
  so the highest ground in it is the middle. A diagonal was drawn as a straight line between two
  opposite corners, and those corners are at kerb level, so the line passed *under* the crown and
  the painted band was buried in the asphalt. The game's own crossings never show this because each
  spans a single road's mouth, where the same straight line sags below the crown by a couple of
  centimetres over a few metres.

  A diagonal's middle is now raised to sit on the junction's own surface height, with a few
  centimetres to spare. It is only ever raised, never lowered: a band hovering a fraction over the
  road is barely visible from above, while one a fraction under it is not visible at all.

## 0.10.1

- **Fixed: too many diagonals, in the wrong places.** A four-road junction was getting three
  crossings through the middle instead of two, and on some junctions they ran between the wrong
  corners and missed the middle entirely.

  The cause was how corners were found. Two crossings meeting at the same corner end at the edges of
  their own roads, and the first version grouped ends into corners by how close together they were.
  On a six-lane boulevard those two points are further apart than the whole width of a side street,
  so no radius works for both: the log showed a four-crossing junction being read as **seven**
  corners, and seven corners pair up into three diagonals. Another read as five, which pairs corners
  that are not opposite at all — that is the one where the diagonals sat off to one side.

  Corners are now counted rather than measured. The crossings are put in order around the junction,
  and every neighbouring pair of them has exactly one corner between it. Four crossings give four
  corners on a junction of any size, six give six, and the pairing has a proper ring to work with.

- Two guards were added behind that, in case a junction turns up shaped in a way this has not seen:
  a pair of corners too close together in angle is a corner being cut rather than a junction being
  crossed, and a line that misses the middle by too much is running around the outside. Both are
  deliberately loose — lopsided and five-road junctions pass, and they were checked against
  simulated four-, five- and six-road junctions, symmetric, lopsided and skewed.
- The log now says how many crossings a junction had as well as how many corners came out of them,
  which is what made this diagnosable in the first place.

## 0.10.0

- **New: "Spawn middle crosswalk"** in the tool panel. Pick a junction where four or more roads
  meet, and the button lays crossings corner to corner through the middle of it — a scramble
  crossing, the Shibuya kind, where the whole junction stops at once and people cut straight across
  instead of walking two sides of a square. Press it again to take them out.
- The button only appears on junctions that have a middle to cross. A T-junction or a bend has
  nothing to run a diagonal through, so it is not offered one.
- The diagonals are worked out from the crossings already standing there: their ends are grouped
  into the corners of the junction, the corners are put in order around it, and each is joined to
  the one across from it. Four roads give two diagonals, six give three. Each diagonal starts and
  ends exactly where an existing crossing ends, so it joins the pedestrian network at points that
  are already part of it.
- They take the same paint, the same width and the same traffic light phase as the crossings around
  them. Widening the junction's crossings widens the diagonals with them.
- They also follow the junction. Reshape a road and the corners move; the diagonals are laid again
  between wherever the corners ended up, on their own, without pressing anything.
- **"Reset junction crosswalks" now takes the diagonals out too**, along with the widths and
  positions set by hand. It is the button for undoing everything done by hand at one junction.
- **"Put every crossing back to normal" and "Remove this mod's data from the city" both clear
  every diagonal in the city.** Press the second one before uninstalling and the city keeps nothing
  of this mod's — no widths, no positions, and no crossings it laid.

Known limits: a diagonal takes the junction's width rather than having one of its own, and the tool
does not select it, so the rings do not appear on it. Use the junction width to size them.

## 0.9.1

- **New: "Reset junction crosswalks"** in the tool panel, under a *Back to global* heading alongside
  the existing per-crossing reset. It puts every crossing at the selected junction back to the
  global width and to the position the game lays it at, and forgets everything set there by hand —
  size and position together, in one press.

## 0.9.0

- **Removed: "make junctions make room".** It did not work and could not be made to. The lever was
  `NetCompositionCrosswalk.m_Start.z`, which is how far *inside* the junction mouth a crossing sits
  and which `GeometrySystem` also reads to size the junction. Raising it moved each crossing inward
  by the full amount while the junction grew by far less, so crossings ended up painted across the
  middle of the intersection — worse than the overlap it was meant to fix. Lowering it instead just
  shrinks the junction back over the crossing. There is no per-junction alternative:
  `NodeGeometry.m_Offset`, the obvious candidate, turns out to be a height.
- It also had a bad idea in it: clearance followed the widest crossing set anywhere, so one junction
  set to 300% by hand moved the crossings of every junction in the city. That is how the 8.4m push
  in the screenshots happened.
- **New: "Put every crossing back to normal"**, under Existing crossings. Returns the whole city to
  exactly where and how the game lays it — width back to 100%, every junction set by hand forgotten,
  every crossing moved by hand put back, and the roads handed to the game to be shaped again. The
  mod stays on, so you can start again from something that looks untouched.

What is left is the part that works: crossing width, and a tool for setting one crossing's width and
position by hand. A widened crossing still grows in both directions from where the game puts it, so
half the extra width reaches into the junction — that is a cosmetic trade-off of widening, not
something the game gives a way to avoid.

## 0.8.2

- **Junctions now make room while you are editing, not only on load.** The clearance a junction is
  given follows the widest crossing set anywhere, rather than the global percentage alone — so
  widening one crossing by hand opens the junctions up for it, and narrowing it again lets them
  close back down. It waits for the mouse button to come up: reshaping takes a moment and doing it
  on every frame of a drag would make the drag unusable.
- Resetting one crossing now clears its position as well as its width.

### The limit worth knowing

A junction's size is decided by its road type, not by the junction. `NetCompositionCrosswalk` — the
only thing that tells `GeometrySystem` how much room a crossing needs — lives on the composition
prefab, which every junction of that road type shares, and there is no per-node equivalent
(`NodeGeometry.m_Offset`, the obvious candidate, is a height). So making room for one crossing makes
the same room at every junction of that road type. That is why the setting is worded as a global
one, and why a very wide crossing at a single junction enlarges more of the city than you might
expect.

## 0.8.1

Everything 0.8.0 added, actually working.

- **Fixed: junctions never actually reshaped.** The offsets were written correctly and no junction
  was ever rebuilt to use them, so nothing moved. Compositions are created *while a save loads*, so
  the push landed on the "a new road type appeared" path, which deliberately does not reshape the
  city; by the time the load finished the compositions already held the pushed value, the next pass
  found nothing to change, and the reshape never happened. The debt is now remembered and settled
  once the city exists.
- **Fixed: 11,551 warnings on load, then a NullReferenceException out of the game's own logger.**
  The check for "is this a real lane prefab" tested for the `Unity.Entities.Prefab` tag. Prefab
  entities do not carry it — `PrefabSystem` builds them with a plain `CreateEntity` — so every
  crossing in the city looked broken and got its own warning until the logger fell over. The test is
  now whether `PrefabSystem` can name the prefab, and the result is one summary line rather than one
  line per lane.
- **Fixed: resizing by dragging became unreachable.** The width gesture was a zone — "anywhere off
  to the side" — that began further out than the distance at which a press is taken to mean "select
  the neighbouring crossing", so a press meant to widen switched crossings instead. Every gesture is
  now an explicit ring you can see and grab: two ends, the middle, and one on each side of the band
  for width. The width drag is also measured along the road now, so sliding along the crossing no
  longer resizes it.
- **New: "How much room" slider**, 0–400%, default 200%. How far crossings move as a share of the
  width they gained. 100% is the arithmetic answer and falls short in practice, because the game
  scales how far each road gives way by how directly the junction's arms face each other. Raise it
  if crossings still sit on the give-way lines.

## 0.8.0

- **New: junctions make room for their crossings.** A widened crossing used to end up painted over
  the give-way lines and under the signal posts. The mod now moves each crossing further into the
  junction along the road, by half the width it gained, and the game reshapes the junction around
  it — because that is already the rule: `GeometrySystem.CalculateCornerOffset` asks
  `CheckCrosswalks` for `max(m_Start.z, m_End.z)` and folds it into how far back each road stops
  from the node. The carriageway, the stop line and the signal posts all follow from there. New
  toggle, **Make junctions make room**, on by default; turning it on reshapes every junction in the
  city once, so expect a pause.
- **New: crossings can be dragged into place.** With a junction selected, the live crossing now has
  three handles: either end swings that end up or down the road, the middle slides the whole
  crossing towards or away from the junction, and anywhere off to the side still pulls it wider.
  Swinging one end alone gives a crossing set at an angle instead of square across. Moves are per
  crossing, saved with the city, and limited to 12 m in either direction.
- Positions are stored with the curve the game laid them from, so a crossing settles where it is
  put: re-laying the junction, saving, reloading and editing the road nearby all leave it alone
  rather than nudging it a little further each time.
- The junction-room adjustment follows the width the crossing actually gets, not the raw
  percentage, so a minimum width of 10 m at 100% makes room and a maximum of 5 m at 400% does not
  inflate every junction for nothing.
- Every path that forgets a crossing's settings now puts the crossing back first — "Clear all
  per-junction widths", clearing one junction, and zeroing a single crossing included. The record
  of where a moved crossing belongs lives in the override itself, so dropping it first would have
  stranded the crossing with nothing able to return it.
- A crossing whose height changes under it (terrain raised beneath a junction) is re-based rather
  than pinned at the old height.

### Known limits

- The trimmed ends of the painted band (`CutRange`) are still the ones the game computed for the
  original width, so a crossing that meets the kerb at an angle can overhang slightly once widened.
- The junction-room adjustment is per road type, not per junction, so one crossing widened well
  past the global setting by hand can still overlap.
- Adding a brand new crossing — one running corner to corner through the middle — is not in this
  release. See the note in NOTES.md for what it needs.

## 0.7.1

Save safety, after 0.6 damaged one.

- **Fixed: the mod destroyed the game's own crossing width baseline.** `NodeLane.m_WidthOffset` is
  not zero for crossings — `LaneSystem` writes `declaredWidth - variantWidth` into it to hold a
  themed lane ("NA Crosswalk Lane 2") to the width the road's composition actually declared. 0.7.0
  overwrote that with an absolute value and restored it to *zero*, which is not a restore: it left
  every crossing on a themed road at the wrong size, permanently, including after the mod was
  removed. The width is now computed from the width the game lays (`scale x laidWidth -
  variantWidth`), so a scale of 1 reproduces the game's own number exactly and turning the mod off
  is a real restore. It also means the percentage is now measured from the right width.
- A crossing whose lane prefab the mod does not recognise is left completely alone rather than
  written with a guess, and named once in the log.
- **New: "Remove this mod's data from the city"** in the settings, under Uninstalling. Puts every
  crossing back, forgets every per-junction width, and switches the mod off so it cannot put them
  back. Save afterwards and the city carries nothing of the mod's.
- Turning the mod off now really does mean off: per-junction widths are remembered but not applied.
- Per-crossing overrides are pruned when a junction's crossings change, so an entry for a crossing
  that no longer exists cannot sit in the save forever or reappear on a different crossing later.
- An empty override buffer is removed rather than left in the save.
- The width pass can no longer take a frame down: it is guarded per lane and as a whole, and stops
  for the session after a failure rather than repeating it over the city.
- The junction order cache is refreshed even after the pass has stopped, and revalidated on read,
  so the tool can never draw from lanes the game has destroyed.
- The lane-prefab check now says honestly what it can do. It repairs a bad reference made in the
  current session; it cannot repair one loaded from a save, because the game stamps an unresolved
  reference with a negative prefab index and there is nothing left to resolve back to.

## 0.7.0

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

## 0.6.0

- The overlay now highlights each crossing along its own curve, at its own width, using the game's
  DrawCurve. The previous version drew filled discs at the junction centre, which covered a city
  block and said nothing about which crossing was which. The live crossing gets a small ring at
  each end as its grab handles.
- The toolbar button is now a white crossing on the same light blue as the other mod buttons. The
  glyph was rendering black because the SVG used currentColor, which does not inherit through an
  img tag.
- Tidied the panel: crossing number in the heading, larger value, buttons that match the row.

## 0.5.0

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

## 0.4.0

- Drag to resize. With a junction selected, press and drag out from its centre; a ring shows the
  current width and follows the cursor. The drag is relative, so the width does not jump when it
  starts and the gesture works the same at any zoom.
- The toolbar button moved to the top left, and the icon is now a zebra crossing framed by its
  kerbs rather than four bars that read as soundwaves.
- Crossing lanes are now also discovered from `NetCompositionCrosswalk` — what the game actually
  built — as well as `NetCrosswalkData`, which is only what a road declares.
- `CrosswalkOverrideSystem` reports what it did: how many junctions and sub-lanes it walked, how
  many lanes it re-pointed, and the name of any pedestrian lane it did not recognise as a crossing.

## 0.3.0

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

## 0.2.0

- Per-junction crossing widths. A tool (Options → Crosswalk Width → Start the crossing tool) lets
  you point at one junction and change only its crossings: Page Up and Page Down resize, left click
  puts it back on the global width, right click leaves the tool.
- Per-junction widths are saved with the city, as a versioned component on the node.
- New settings: tool step size, and "Clear all per-junction widths".
- Removing the mod puts every junction back on the game's own lane prefabs before restoring the
  authored widths, so nothing is left pointing at a prefab this mod created.

## 0.1.0

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
