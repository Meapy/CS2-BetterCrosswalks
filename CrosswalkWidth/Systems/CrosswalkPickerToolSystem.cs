using Colossal.Mathematics;
using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace CrosswalkWidth.Systems
{
    /// <summary>
    /// Click a junction, then resize its crossings one at a time.
    ///
    /// Selecting a junction draws a ring on every crossing it has. The ring nearest the cursor is
    /// the live one; press on it and drag outward to widen that crossing alone, or use the panel's
    /// − and + buttons. Nothing here touches the other crossings at the same junction.
    ///
    /// Crossings are hit-tested against their own midpoints rather than by raycasting the lanes.
    /// The tool already has a ground position from the terrain raycast it needs for dragging, and
    /// a nearest-midpoint test needs no assumptions about whether lane raycasting is enabled or
    /// what it returns.
    ///
    /// A tool only gets the input the game hands it. Left and right click come from applyAction and
    /// cancelAction, which ToolBaseSystem sets up for every tool. Elevation (Page Up / Page Down)
    /// reaches a tool through ElevationUp and ElevationDown, but the game only enables those actions
    /// for a tool whose options panel offers elevation, so for a mod tool they never fire — an
    /// earlier version relied on them and appeared to do nothing at all. The handlers are still
    /// here because they cost nothing where the game does enable them.
    /// </summary>
    public partial class CrosswalkPickerToolSystem : ToolBaseSystem
    {
        public const string kToolID = "Crosswalk Width Tool";

        /// <summary>Thickness of the highlight outlines, in metres.</summary>
        private const float kOutlineWidth = 0.35f;

        /// <summary>Diameter of the grab handles at the ends of the live crossing, in metres.</summary>
        private const float kHandleDiameter = 2.5f;

        /// <summary>Handles are rings, not discs: the fill is fully transparent.</summary>
        private static readonly Color kHandleClear = new Color(0f, 0f, 0f, 0f);

        /// <summary>How far outside the painted band a click still counts as being on it, in metres.</summary>
        private const float kPickMargin = 2.5f;

        /// <summary>Points sampled along a crossing's curve when measuring how far the cursor is from it.</summary>
        private const int kCurveSamples = 16;

        /// <summary>How near a handle the cursor has to be to take hold of it, in metres.</summary>
        private const float kHandleGrab = 5f;

        /// <summary>How far beyond the painted edge the width handles sit, in metres.</summary>
        private const float kSideHandleClear = 2.5f;

        private CrosswalkOverrideSystem m_OverrideSystem;
        private CrosswalkScrambleSystem m_ScrambleSystem;
        private OverlayRenderSystem m_OverlayRenderSystem;

        private readonly List<CrosswalkOverrideSystem.CrossingInfo> m_Crossings =
            new List<CrosswalkOverrideSystem.CrossingInfo>();

        private Entity m_Highlighted;
        private Entity m_Selected;
        private int m_SelectedCrossing = -1;
        private int m_HoveredCrossing = -1;
        private int m_PendingSteps;
        private bool m_LoggedFirstHover;

        /// <summary>What a drag on the live crossing is doing.</summary>
        private enum DragMode
        {
            None,

            /// <summary>Pulling the band wider or narrower.</summary>
            Width,

            /// <summary>Sliding the whole crossing along the road.</summary>
            Move,

            /// <summary>Swinging the end the crossing starts from.</summary>
            MoveStart,

            /// <summary>Swinging the end the crossing points to.</summary>
            MoveEnd
        }

        private DragMode m_Dragging;
        private float m_DragStartRadius;
        private float m_DragStartScale;

        /// <summary>Direction traffic travels at the crossing, fixed when the drag starts.</summary>
        private float2 m_DragRoad;

        /// <summary>Where along the road the cursor was when the drag started.</summary>
        private float m_DragStartAlong;

        /// <summary>The crossing's shift when the drag started, so the drag is relative.</summary>
        private float2 m_DragStartShift;

        /// <summary>Which handle the cursor is over, for drawing. Mirrors the drag modes.</summary>
        private DragMode m_Hovered;

        public override string toolID => kToolID;

        public Entity selectedNode => m_Selected;

        /// <summary>True while the mouse button is down on a handle.</summary>
        public bool isDragging => m_Dragging != DragMode.None;

        /// <summary>How many crossings the selected junction has.</summary>
        public int crossingCount => m_Crossings.Count;

        /// <summary>Which of them is being edited, counting from 1 for display. 0 if none.</summary>
        public int selectedCrossingNumber => m_SelectedCrossing >= 0 ? m_SelectedCrossing + 1 : 0;

        public override PrefabBase GetPrefab()
        {
            return null;
        }

        public override bool TrySetPrefab(PrefabBase prefab)
        {
            return false;
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            m_OverrideSystem = World.GetOrCreateSystemManaged<CrosswalkOverrideSystem>();
            m_ScrambleSystem = World.GetOrCreateSystemManaged<CrosswalkScrambleSystem>();
            m_OverlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();

            m_PendingSteps = 0;
            m_Selected = Entity.Null;
            m_SelectedCrossing = -1;
            m_HoveredCrossing = -1;
            m_Dragging = DragMode.None;
            m_Hovered = DragMode.None;
            m_LoggedFirstHover = false;
            m_Crossings.Clear();
            requireNet = Layer.Road;

            // ToolBaseSystem.ResetActions switches these off when a tool stops, and the base
            // UpdateActions does nothing, so a tool that wants clicks has to ask for them. The
            // override that would normally do this is private protected and out of reach from a mod
            // assembly, hence doing it here instead.
            applyAction.shouldBeEnabled = true;
            cancelAction.shouldBeEnabled = true;

            Mod.Log.Info($"{Mod.ModName}: crossing tool running");
        }

        protected override void OnStopRunning()
        {
            ClearHighlight();

            m_Selected = Entity.Null;
            m_SelectedCrossing = -1;
            m_Dragging = DragMode.None;
            m_Hovered = DragMode.None;
            m_Crossings.Clear();

            Mod.Log.Info($"{Mod.ModName}: crossing tool stopped");

            base.OnStopRunning();
        }

        public override void InitializeRaycast()
        {
            base.InitializeRaycast();

            // Terrain as well as net: a drag has to keep tracking the ground once the cursor moves
            // off the road, which is precisely the gesture for making a crossing wider.
            m_ToolRaycastSystem.typeMask = TypeMask.Net | TypeMask.Terrain;
            m_ToolRaycastSystem.netLayerMask = Layer.Road;
        }

        public override void ElevationUp()
        {
            m_PendingSteps++;
        }

        public override void ElevationDown()
        {
            m_PendingSteps--;
        }

        public override void ElevationScroll()
        {
            // Deliberately nothing: scrolling stays camera zoom while this tool is active.
        }

        /// <summary>
        /// The number the crossing being edited stores its width and position under.
        ///
        /// Not the same as its place in this list. The junction's own crossings are numbered by
        /// their place in its lane list, and the middle crossings — which are not in that list at
        /// all — are numbered by which diagonal they are. Using the position for both was fine while
        /// the two matched; it stopped being fine the moment middle crossings joined the list.
        /// </summary>
        private int SelectedKey()
        {
            return m_SelectedCrossing >= 0 && m_SelectedCrossing < m_Crossings.Count
                ? m_Crossings[m_SelectedCrossing].m_Index
                : -1;
        }

        /// <summary>Widens or narrows the crossing being edited. Called from the toolbar panel.</summary>
        public void AdjustSelected(int steps)
        {
            if (m_Selected == Entity.Null || m_SelectedCrossing < 0 || steps == 0)
            {
                return;
            }

            CrosswalkWidthSetting settings = Mod.Settings;
            float step = (settings != null ? settings.ToolStepPercentage : 25) / 100f;
            float scale = m_OverrideSystem.GetScale(m_Selected, SelectedKey()) + step * steps;

            m_OverrideSystem.SetOverride(m_Selected, SelectedKey(), math.max(0.25f, scale));

            Mod.Log.Info(
                $"{Mod.ModName}: junction {m_Selected.Index} crossing {selectedCrossingNumber} "
                + $"now at {SelectedPercent()}%");
        }

        /// <summary>Puts the crossing being edited back on the global width.</summary>
        public void ResetSelected()
        {
            if (m_Selected == Entity.Null || m_SelectedCrossing < 0)
            {
                return;
            }

            m_OverrideSystem.SetOverride(m_Selected, SelectedKey(), 0f);
            m_OverrideSystem.SetShift(m_Selected, SelectedKey(), default(float2));
            m_OverrideSystem.SetHidePaint(m_Selected, SelectedKey(), false);
            m_OverrideSystem.RequestFullPass();

            Mod.Log.Info(
                $"{Mod.ModName}: junction {m_Selected.Index} crossing {selectedCrossingNumber} "
                + "back on the global width");
        }

        /// <summary>
        /// Puts the whole junction back on the global width, in its original position.
        ///
        /// One call does it: clearing a junction's own width also restores every crossing there
        /// that has been moved and drops the per-crossing entries, because the record of where a
        /// moved crossing belongs lives in those entries and has to be used before it is thrown
        /// away. Crossings through the middle go too — they are something added by hand, and this
        /// is the button for undoing what was done by hand here.
        /// </summary>
        public void ResetJunction()
        {
            if (m_Selected == Entity.Null)
            {
                return;
            }

            m_OverrideSystem.SetOverride(m_Selected, -1, 0f);

            if (m_ScrambleSystem != null)
            {
                m_ScrambleSystem.SetScramble(m_Selected, false);
            }

            m_OverrideSystem.RequestFullPass();

            Mod.Log.Info(
                $"{Mod.ModName}: junction {m_Selected.Index} back to global — every crossing here "
                + "returned to its width and position");
        }

        /// <summary>True if the selected junction has enough roads meeting at it for a scramble.</summary>
        public bool canScramble => m_ScrambleSystem != null && m_ScrambleSystem.CanScramble(m_Selected);

        /// <summary>True if the selected junction already has crossings through the middle.</summary>
        public bool hasScramble => m_ScrambleSystem != null && m_ScrambleSystem.HasScramble(m_Selected);

        /// <summary>
        /// Adds crossings corner to corner through the middle of the selected junction, or takes
        /// them away again if they are already there.
        /// </summary>
        public void ToggleScramble()
        {
            if (m_Selected == Entity.Null || m_ScrambleSystem == null)
            {
                return;
            }

            m_ScrambleSystem.SetScramble(m_Selected, !m_ScrambleSystem.HasScramble(m_Selected));
        }

        /// <summary>True if the crossing being edited is one this mod laid, and so can be removed.</summary>
        public bool selectedIsAdded =>
            m_SelectedCrossing >= 0
            && m_SelectedCrossing < m_Crossings.Count
            && m_Crossings[m_SelectedCrossing].m_IsAdded;

        /// <summary>
        /// Takes the crossing being edited out of the junction.
        ///
        /// Only a middle crossing can go. The junction's own crossings come from the road it is
        /// built with — the game lays them and would lay them again immediately — so there is
        /// nothing this could do about one except make it flicker.
        /// </summary>
        public void RemoveSelected()
        {
            if (!selectedIsAdded || m_ScrambleSystem == null)
            {
                return;
            }

            Entity lane = m_Crossings[m_SelectedCrossing].m_Lane;

            if (m_ScrambleSystem.RemoveOne(lane))
            {
                m_SelectedCrossing = -1;
                m_OverrideSystem.RequestNode(m_Selected);
            }
        }

        /// <summary>Gives every crossing at this junction the width of the one being edited.</summary>
        public void ApplyToWholeJunction()
        {
            if (m_Selected == Entity.Null || m_SelectedCrossing < 0)
            {
                return;
            }

            float scale = m_OverrideSystem.GetScale(m_Selected, SelectedKey());

            for (int i = 0; i < m_Crossings.Count; i++)
            {
                m_OverrideSystem.SetOverride(m_Selected, m_Crossings[i].m_Index, scale);
            }

            Mod.Log.Info(
                $"{Mod.ModName}: junction {m_Selected.Index} all {m_Crossings.Count} crossings "
                + $"set to {(int)math.round(scale * 100f)}%");
        }

        /// <summary>True if the crossing being edited is currently not drawn.</summary>
        public bool selectedPaintHidden =>
            m_Selected != Entity.Null
            && m_SelectedCrossing >= 0
            && m_OverrideSystem.GetHidePaint(m_Selected, SelectedKey());

        /// <summary>
        /// Stops drawing the crossing being edited, or starts again. Called from the toolbar panel.
        ///
        /// Only the paint. The crossing goes on being a crossing — people use it, its signals run,
        /// and the rings stay on it so it can still be found, resized and moved.
        /// </summary>
        public void ToggleHidePaint()
        {
            if (m_Selected == Entity.Null || m_SelectedCrossing < 0)
            {
                return;
            }

            bool hide = !m_OverrideSystem.GetHidePaint(m_Selected, SelectedKey());

            m_OverrideSystem.SetHidePaint(m_Selected, SelectedKey(), hide);

            Mod.Log.Info(
                $"{Mod.ModName}: junction {m_Selected.Index} crossing {selectedCrossingNumber} "
                + (hide ? "no longer drawn" : "drawn again"));
        }

        /// <summary>The width in force at the crossing being edited, as a percentage.</summary>
        public int SelectedPercent()
        {
            if (m_Selected == Entity.Null || m_SelectedCrossing < 0)
            {
                return 0;
            }

            return (int)math.round(m_OverrideSystem.GetScale(m_Selected, SelectedKey()) * 100f);
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            if (cancelAction.WasPressedThisFrame())
            {
                if (m_Selected != Entity.Null)
                {
                    // First right click drops the selection, second leaves the tool. Leaving
                    // outright would make a mis-click cost the whole session with the tool.
                    m_Selected = Entity.Null;
                    m_SelectedCrossing = -1;
                    m_Crossings.Clear();
                    return inputDeps;
                }

                ClearHighlight();
                m_ToolSystem.activeTool = m_DefaultToolSystem;
                return inputDeps;
            }

            bool hasHit = GetRaycastResult(out Entity hitEntity, out RaycastHit hit);
            Entity node = hasHit ? ResolveNode(hitEntity, hit) : Entity.Null;

            // The crossing list is rebuilt every frame because LaneSystem may have re-laid the node
            // since the last one, which invalidates every lane entity in it.
            if (m_Selected != Entity.Null)
            {
                m_OverrideSystem.CollectCrossings(m_Selected, m_Crossings);
            }
            else
            {
                m_Crossings.Clear();
            }

            m_HoveredCrossing = hasHit ? NearestCrossing(hit.m_HitPosition, kPickMargin) : -1;

            m_Hovered = m_Dragging != DragMode.None
                ? m_Dragging
                : (hasHit ? ModeAt(hit.m_HitPosition) : DragMode.None);

            if (m_Dragging != DragMode.None)
            {
                if (applyAction.IsPressed())
                {
                    if (hasHit)
                    {
                        UpdateDrag(hit);
                    }
                }
                else
                {
                    Mod.Log.Info($"{Mod.ModName}: {m_Dragging} drag finished");
                    m_Dragging = DragMode.None;

                    // The junction clearance follows the widest crossing anywhere, and an
                    // incremental pass can only ever raise that figure — it has not looked at the
                    // rest of the city. A full sweep after the drag lets it come back down again,
                    // so narrowing a crossing lets the junctions close up rather than leaving them
                    // permanently sized for the widest thing ever set.
                    m_OverrideSystem.RequestFullPass();
                }

                DrawHandles(hit);
                return inputDeps;
            }

            Highlight(node);

            if (applyAction.WasPressedThisFrame())
            {
                bool haveLive = m_Selected != Entity.Null
                    && m_SelectedCrossing >= 0
                    && m_SelectedCrossing < m_Crossings.Count;

                // A handle wins over everything else. The end handles of a long crossing sit well
                // away from its middle, so the crossing whose middle is nearest the cursor is often
                // not the one whose handle is under it — and without this, reaching for an end
                // handle silently switches to the neighbouring crossing instead of grabbing the
                // thing you were pointing at.
                bool onHandle = hasHit && haveLive && m_Hovered != DragMode.None;

                if (onHandle || (haveLive && m_HoveredCrossing == m_SelectedCrossing))
                {
                    // Pressing on the crossing that is already live starts a drag; pressing on a
                    // different one makes it live first, so a single press never both switches
                    // crossing and changes it.
                    BeginDrag(hit);
                }
                else if (m_HoveredCrossing >= 0 && m_Selected != Entity.Null)
                {
                    m_SelectedCrossing = m_HoveredCrossing;
                    Mod.Log.Info(
                        $"{Mod.ModName}: junction {m_Selected.Index} crossing "
                        + $"{selectedCrossingNumber} of {m_Crossings.Count} at {SelectedPercent()}%");
                }
                else if (node != Entity.Null)
                {
                    m_Selected = node;
                    m_OverrideSystem.CollectCrossings(m_Selected, m_Crossings);
                    // No limit here: the junction was just clicked and one of its crossings has to
                    // be the live one, even if the click landed on bare road inside the junction.
                    m_SelectedCrossing = m_Crossings.Count > 0
                        ? NearestCrossing(hit.m_HitPosition, float.MaxValue)
                        : -1;

                    Mod.Log.Info(
                        $"{Mod.ModName}: selected junction {node.Index} with {m_Crossings.Count} crossings");
                }
            }

            if (node != Entity.Null && !m_LoggedFirstHover)
            {
                m_LoggedFirstHover = true;
                Mod.Log.Info($"{Mod.ModName}: pointing at junction {node.Index}");
            }

            if (m_PendingSteps != 0)
            {
                AdjustSelected(m_PendingSteps);
                m_PendingSteps = 0;
            }

            DrawHandles(hit);

            return inputDeps;
        }

        /// <summary>
        /// The crossing the cursor is on: the one whose painted band it is nearest, or inside.
        ///
        /// This used to be the crossing whose **midpoint** was nearest, within fourteen metres. A
        /// midpoint is one point on a band that can be twenty metres long, so pointing anywhere near
        /// the end of a crossing was nearer the neighbouring crossing's middle than its own, and
        /// picked that one instead. The middle crossings made it plainly wrong rather than merely
        /// inaccurate: two diagonals of a scramble cross at the junction's centre, so their midpoints
        /// are within a metre or two of each other and of every other midpoint there. Which one a
        /// click landed on was, in effect, arbitrary.
        ///
        /// So the measurement is to the whole band, not to a point on it: the distance from the
        /// cursor to the crossing's curve, less half of how wide that crossing is drawn. That is the
        /// distance to the painted edge, and it goes negative inside the paint — which is what makes
        /// the crossing the cursor is standing on win outright, and, where two overlap, the one whose
        /// centre it is nearer relative to that crossing's own width.
        ///
        /// <paramref name="reach"/> is how far outside the paint still counts. Hovering wants that
        /// short, so that pointing at bare road selects nothing; picking a crossing to start with
        /// when a junction is first clicked passes no limit, because something has to be chosen.
        /// </summary>
        private int NearestCrossing(float3 point, float reach)
        {
            int best = -1;
            float bestScore = reach;

            for (int i = 0; i < m_Crossings.Count; i++)
            {
                CrosswalkOverrideSystem.CrossingInfo crossing = m_Crossings[i];

                float width = DrawnWidth(crossing);

                if (!IsDrawn(width))
                {
                    continue;   // nothing is painted there, so there is nothing to point at
                }

                float score = PlanDistanceToCurve(crossing.m_Curve, point) - width * 0.5f;

                if (score < bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }

            return best;
        }

        /// <summary>
        /// How far a point lies from a curve on the ground plane, in metres.
        ///
        /// The curve is walked as a chain of straight pieces rather than solved for, which is exact
        /// enough at this scale — a crossing bends by very little over its length — and cannot fail
        /// to converge on the one that barely bends at all.
        ///
        /// Height is left out on purpose. The cursor position is a terrain hit, while a diagonal is
        /// deliberately lifted over the crown of the junction, so including height would report every
        /// diagonal as further away than it looks and hand the pick to a flat crossing beside it.
        /// </summary>
        private static float PlanDistanceToCurve(Bezier4x3 curve, float3 point)
        {
            float best = float.MaxValue;
            float3 previous = Evaluate(curve, 0f);

            for (int i = 1; i <= kCurveSamples; i++)
            {
                float3 next = Evaluate(curve, i / (float)kCurveSamples);
                float distance = PlanDistanceToSegment(previous, next, point);

                if (distance < best)
                {
                    best = distance;
                }

                previous = next;
            }

            return best;
        }

        /// <summary>How far a point lies from a straight piece, on the ground plane.</summary>
        private static float PlanDistanceToSegment(float3 a, float3 b, float3 point)
        {
            float dx = b.x - a.x;
            float dz = b.z - a.z;
            float lengthSq = dx * dx + dz * dz;

            if (lengthSq < 0.0001f)
            {
                return math.sqrt(DistanceSq(a, point));
            }

            float along = ((point.x - a.x) * dx + (point.z - a.z) * dz) / lengthSq;
            along = math.clamp(along, 0f, 1f);

            float offX = point.x - (a.x + dx * along);
            float offZ = point.z - (a.z + dz * along);

            return math.sqrt(offX * offX + offZ * offZ);
        }

        /// <summary>A point along a curve.</summary>
        private static float3 Evaluate(Bezier4x3 curve, float t)
        {
            float u = 1f - t;
            float wa = u * u * u;
            float wb = 3f * u * u * t;
            float wc = 3f * u * t * t;
            float wd = t * t * t;

            return new float3(
                curve.a.x * wa + curve.b.x * wb + curve.c.x * wc + curve.d.x * wd,
                curve.a.y * wa + curve.b.y * wb + curve.c.y * wc + curve.d.y * wd,
                curve.a.z * wa + curve.b.z * wb + curve.c.z * wc + curve.d.z * wd);
        }

        /// <summary>
        /// How wide one crossing is drawn, in metres.
        ///
        /// By its own key, never by its place in the list. Those two stopped matching when the middle
        /// crossings joined the list, and reading the scale by position drew every middle crossing at
        /// whatever width the junction's crossing in that position happened to be set to.
        /// </summary>
        private float DrawnWidth(CrosswalkOverrideSystem.CrossingInfo crossing)
        {
            return crossing.m_AuthoredWidth * m_OverrideSystem.GetScale(m_Selected, crossing.m_Index);
        }

        /// <summary>
        /// Whether a crossing of this width is drawn at all.
        ///
        /// Picking and drawing ask the same question, because a crossing that is not painted must
        /// not be selectable: the panel would report a crossing the player cannot see, with handles
        /// hanging off a band of no width. A crossing reaches zero here only when the catalog does
        /// not know its prefab, which is the same reason it is not drawn.
        /// </summary>
        private static bool IsDrawn(float width)
        {
            return width > 0.01f;
        }

        /// <summary>
        /// The five places on a crossing worth grabbing: its two ends, its middle, and a point just
        /// off each side of the painted band.
        ///
        /// Every gesture is one of these, and every one of them is drawn. The first version worked
        /// the other way round — it decided from zones, with "anywhere off to the side" meaning
        /// resize — and that quietly made resizing unreachable: the zone began further out than the
        /// distance at which a press is taken to mean "select the neighbouring crossing instead",
        /// so a press meant to widen switched crossings. A handle you can see and a handle you can
        /// hit are the same thing here.
        ///
        /// That is why a press on **any** of these five, the side ones included, acts on the live
        /// crossing rather than deferring to whichever crossing the cursor is nearest. The side
        /// handles are the only ones that sit off the paint, so they are the only ones where the two
        /// can disagree — and on a scramble they always disagree, because the other diagonal runs
        /// through the middle of this one and its paint covers both side handles. Deferring there
        /// made resizing a diagonal impossible: each press simply swapped which diagonal was live,
        /// while the handle stayed lit under the cursor the whole time.
        /// </summary>
        private bool HandleSpots(
            CrosswalkOverrideSystem.CrossingInfo crossing,
            out float3 start,
            out float3 end,
            out float3 middle,
            out float3 sideA,
            out float3 sideB)
        {
            start = crossing.m_Curve.a;
            end = crossing.m_Curve.d;
            middle = crossing.m_Midpoint;
            sideA = middle;
            sideB = middle;

            if (!Axes(crossing, out float2 _unused, out float2 road, out float _half))
            {
                return false;
            }

            // Just clear of the painted edge, so the side handles read as "the edge of the band"
            // rather than as two more dots floating in the road.
            float width = math.max(1f, crossing.m_AuthoredWidth
                * m_OverrideSystem.GetScale(m_Selected, SelectedKey()));

            float reach = width * 0.5f + kSideHandleClear;

            sideA.x += road.x * reach;
            sideA.z += road.y * reach;

            sideB.x -= road.x * reach;
            sideB.z -= road.y * reach;

            return true;
        }

        /// <summary>Which gesture a press at this point means: the nearest handle, if one is near.</summary>
        private DragMode ModeAt(float3 point)
        {
            if (m_SelectedCrossing < 0 || m_SelectedCrossing >= m_Crossings.Count)
            {
                return DragMode.None;
            }

            CrosswalkOverrideSystem.CrossingInfo crossing = m_Crossings[m_SelectedCrossing];

            if (!HandleSpots(crossing, out float3 start, out float3 end, out float3 middle,
                out float3 sideA, out float3 sideB))
            {
                return DragMode.None;
            }

            DragMode best = DragMode.None;
            float bestDistance = kHandleGrab * kHandleGrab;

            Closest(point, start, DragMode.MoveStart, ref best, ref bestDistance);
            Closest(point, end, DragMode.MoveEnd, ref best, ref bestDistance);
            Closest(point, middle, DragMode.Move, ref best, ref bestDistance);
            Closest(point, sideA, DragMode.Width, ref best, ref bestDistance);
            Closest(point, sideB, DragMode.Width, ref best, ref bestDistance);

            return best;
        }

        private static void Closest(float3 point, float3 handle, DragMode mode, ref DragMode best, ref float bestDistance)
        {
            float distance = DistanceSq(handle, point);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = mode;
            }
        }

        /// <summary>
        /// The crossing's two axes in the ground plane: across the road, and along it.
        ///
        /// The along-the-road direction is a quarter turn from the crossing's own — the same
        /// derivation CrosswalkOverrideSystem uses when it applies a shift, so a drag of one metre
        /// here is a shift of one metre there and in the same direction.
        /// </summary>
        private static bool Axes(
            CrosswalkOverrideSystem.CrossingInfo crossing,
            out float2 across,
            out float2 road,
            out float halfLength)
        {
            float dx = crossing.m_Curve.d.x - crossing.m_Curve.a.x;
            float dz = crossing.m_Curve.d.z - crossing.m_Curve.a.z;
            float length = math.sqrt(dx * dx + dz * dz);

            if (length < 0.5f)
            {
                across = default(float2);
                road = default(float2);
                halfLength = 0f;
                return false;
            }

            across = new float2(dx / length, dz / length);
            road = new float2(across.y, -across.x);
            halfLength = length * 0.5f;

            return true;
        }

        /// <summary>
        /// Starts a drag on the crossing being edited.
        ///
        /// Relative rather than absolute in every mode: what is recorded is where the cursor was
        /// when the button went down and what the crossing was then, so nothing jumps at the moment
        /// the drag starts and the gesture works the same at any zoom.
        /// </summary>
        private void BeginDrag(RaycastHit hit)
        {
            if (m_SelectedCrossing < 0 || m_SelectedCrossing >= m_Crossings.Count)
            {
                return;
            }

            DragMode mode = ModeAt(hit.m_HitPosition);

            if (mode == DragMode.None)
            {
                return;
            }

            CrosswalkOverrideSystem.CrossingInfo crossing = m_Crossings[m_SelectedCrossing];

            if (!Axes(crossing, out float2 unusedAcross, out float2 road, out float unusedHalfLength))
            {
                return;
            }

            if (mode == DragMode.Width)
            {
                // Measured along the road, which is the direction the band's edges move in. The
                // radial distance the first version used grew when the cursor slid *along* the
                // crossing as well, so a drag that never went near the edge still resized it.
                float reach = math.abs(SidewaysOf(hit.m_HitPosition, crossing.m_Midpoint, road));

                if (reach < 0.5f)
                {
                    return;   // on the centreline; a ratio from here means nothing
                }

                m_Dragging = DragMode.Width;
                m_DragRoad = road;
                m_DragStartRadius = reach;
                m_DragStartScale = m_OverrideSystem.GetScale(m_Selected, SelectedKey());

                Mod.Log.Info(
                    $"{Mod.ModName}: resizing junction {m_Selected.Index} crossing "
                    + $"{selectedCrossingNumber} from {SelectedPercent()}%");

                return;
            }

            m_Dragging = mode;
            m_DragRoad = road;
            m_DragStartAlong = hit.m_HitPosition.x * road.x + hit.m_HitPosition.z * road.y;
            m_DragStartShift = m_OverrideSystem.GetShift(m_Selected, SelectedKey());

            Mod.Log.Info(
                $"{Mod.ModName}: moving junction {m_Selected.Index} crossing "
                + $"{selectedCrossingNumber} ({mode})");
        }

        private void UpdateDrag(RaycastHit hit)
        {
            if (m_SelectedCrossing < 0 || m_SelectedCrossing >= m_Crossings.Count)
            {
                return;
            }

            if (m_Dragging == DragMode.Width)
            {
                if (m_DragStartRadius < 0.5f)
                {
                    return;
                }

                float reach = math.abs(SidewaysOf(
                    hit.m_HitPosition,
                    m_Crossings[m_SelectedCrossing].m_Midpoint,
                    m_DragRoad));

                if (reach < 0.5f)
                {
                    return;
                }

                float scale = m_DragStartScale * (reach / m_DragStartRadius);
                m_OverrideSystem.SetOverride(m_Selected, SelectedKey(), math.clamp(scale, 0.25f, 8f));

                return;
            }

            float along = hit.m_HitPosition.x * m_DragRoad.x + hit.m_HitPosition.z * m_DragRoad.y;
            float moved = along - m_DragStartAlong;

            float2 shift = m_DragStartShift;

            if (m_Dragging == DragMode.Move || m_Dragging == DragMode.MoveStart)
            {
                shift.x += moved;
            }

            if (m_Dragging == DragMode.Move || m_Dragging == DragMode.MoveEnd)
            {
                shift.y += moved;
            }

            m_OverrideSystem.SetShift(m_Selected, SelectedKey(), shift);
        }

        /// <summary>
        /// Highlights each crossing of the selected junction along its own curve, at its own width.
        ///
        /// A band drawn on the crossing is the readable thing here: it sits exactly where the paint
        /// is, it is as wide as the crossing actually is, and dragging thickens it in place. The
        /// first version drew filled circles at the junction centre, which covered half the
        /// neighbourhood and said nothing about which crossing was which.
        ///
        /// The live crossing also gets a small ring at each end of its curve — the grab handles.
        /// </summary>
        private void DrawHandles(RaycastHit hit)
        {
            if (m_Crossings.Count == 0)
            {
                return;
            }

            OverlayRenderSystem.Buffer buffer = m_OverlayRenderSystem.GetBuffer(out JobHandle dependencies);
            dependencies.Complete();

            Color idleOutline = new Color(0.55f, 0.85f, 1f, 0.55f);
            Color idleFill = new Color(0.35f, 0.75f, 1f, 0.12f);
            bool dragging = m_Dragging != DragMode.None;

            Color liveOutline = dragging ? new Color(1f, 0.8f, 0.3f, 1f) : new Color(0.4f, 1f, 0.7f, 0.95f);
            Color liveFill = dragging ? new Color(1f, 0.8f, 0.3f, 0.3f) : new Color(0.4f, 1f, 0.7f, 0.25f);
            Color hotOutline = new Color(1f, 0.85f, 0.35f, 1f);

            for (int i = 0; i < m_Crossings.Count; i++)
            {
                CrosswalkOverrideSystem.CrossingInfo crossing = m_Crossings[i];

                float width = DrawnWidth(crossing);

                if (!IsDrawn(width))
                {
                    continue;
                }

                bool isLive = i == m_SelectedCrossing;

                buffer.DrawCurve(
                    isLive ? liveOutline : idleOutline,
                    isLive ? liveFill : idleFill,
                    kOutlineWidth,
                    OverlayRenderSystem.StyleFlags.Projected,
                    crossing.m_Curve,
                    width);

                if (!isLive)
                {
                    continue;
                }

                // Three handles, drawn where the gestures are: an end to swing, an end to swing,
                // and the middle to slide the whole thing along the road. The one the cursor would
                // act on is lit, which is the only hint needed that they do different things.
                if (HandleSpots(crossing, out float3 handleStart, out float3 handleEnd,
                    out float3 handleMiddle, out float3 handleSideA, out float3 handleSideB))
                {
                    DrawHandle(buffer, handleStart, DragMode.MoveStart, liveOutline, hotOutline);
                    DrawHandle(buffer, handleEnd, DragMode.MoveEnd, liveOutline, hotOutline);
                    DrawHandle(buffer, handleMiddle, DragMode.Move, liveOutline, hotOutline);
                    DrawHandle(buffer, handleSideA, DragMode.Width, liveOutline, hotOutline);
                    DrawHandle(buffer, handleSideB, DragMode.Width, liveOutline, hotOutline);
                }
            }

            m_OverlayRenderSystem.AddBufferWriter(default(JobHandle));
        }

        /// <summary>One grab handle, lit if the cursor is on it.</summary>
        private void DrawHandle(
            OverlayRenderSystem.Buffer buffer,
            float3 position,
            DragMode mode,
            Color idle,
            Color hot)
        {
            bool active = m_Hovered == mode;

            buffer.DrawCircle(
                active ? hot : idle,
                kHandleClear,
                active ? kOutlineWidth * 1.6f : kOutlineWidth,
                OverlayRenderSystem.StyleFlags.Projected,
                default(float2),
                position,
                active ? kHandleDiameter * 1.3f : kHandleDiameter);
        }

        /// <summary>
        /// The junction the cursor is on: the hit entity if it is a node, otherwise the nearer end
        /// of the edge that was hit.
        /// </summary>
        private Entity ResolveNode(Entity entity, RaycastHit hit)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity))
            {
                return Entity.Null;
            }

            if (EntityManager.HasComponent<Game.Net.Node>(entity))
            {
                return entity;
            }

            if (!EntityManager.HasComponent<Edge>(entity))
            {
                return Entity.Null;
            }

            Edge edge = EntityManager.GetComponentData<Edge>(entity);

            float startDistance = NodeDistanceSq(edge.m_Start, hit.m_HitPosition);
            float endDistance = NodeDistanceSq(edge.m_End, hit.m_HitPosition);

            if (startDistance == float.MaxValue && endDistance == float.MaxValue)
            {
                return Entity.Null;
            }

            return startDistance <= endDistance ? edge.m_Start : edge.m_End;
        }

        private float NodeDistanceSq(Entity node, float3 point)
        {
            if (node == Entity.Null
                || !EntityManager.Exists(node)
                || !EntityManager.HasComponent<Game.Net.Node>(node))
            {
                return float.MaxValue;
            }

            return DistanceSq(EntityManager.GetComponentData<Game.Net.Node>(node).m_Position, point);
        }

        /// <summary>How far a point lies from the crossing's centreline, measured along the road.</summary>
        private static float SidewaysOf(float3 point, float3 middle, float2 road)
        {
            return (point.x - middle.x) * road.x + (point.z - middle.z) * road.y;
        }

        /// <summary>Squared distance on the ground plane. Height is ignored so a sloped junction picks cleanly.</summary>
        private static float DistanceSq(float3 a, float3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;

            return dx * dx + dz * dz;
        }

        private void Highlight(Entity node)
        {
            if (node == m_Highlighted)
            {
                return;
            }

            ClearHighlight();

            if (node == Entity.Null || !EntityManager.Exists(node))
            {
                return;
            }

            if (!EntityManager.HasComponent<Highlighted>(node))
            {
                EntityManager.AddComponent<Highlighted>(node);
                EntityManager.AddComponent<BatchesUpdated>(node);
            }

            m_Highlighted = node;
        }

        private void ClearHighlight()
        {
            if (m_Highlighted == Entity.Null)
            {
                return;
            }

            if (EntityManager.Exists(m_Highlighted) && EntityManager.HasComponent<Highlighted>(m_Highlighted))
            {
                EntityManager.RemoveComponent<Highlighted>(m_Highlighted);
                EntityManager.AddComponent<BatchesUpdated>(m_Highlighted);
            }

            m_Highlighted = Entity.Null;
        }
    }
}
