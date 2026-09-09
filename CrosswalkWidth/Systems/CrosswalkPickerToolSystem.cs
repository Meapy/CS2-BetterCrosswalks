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

        /// <summary>How near the cursor has to be to a crossing's middle to grab it, in metres.</summary>
        private const float kPickRadius = 14f;

        private CrosswalkOverrideSystem m_OverrideSystem;
        private OverlayRenderSystem m_OverlayRenderSystem;

        private readonly List<CrosswalkOverrideSystem.CrossingInfo> m_Crossings =
            new List<CrosswalkOverrideSystem.CrossingInfo>();

        private Entity m_Highlighted;
        private Entity m_Selected;
        private int m_SelectedCrossing = -1;
        private int m_HoveredCrossing = -1;
        private int m_PendingSteps;
        private bool m_LoggedFirstHover;

        private bool m_Dragging;
        private float m_DragStartRadius;
        private float m_DragStartScale;

        public override string toolID => kToolID;

        public Entity selectedNode => m_Selected;

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
            m_OverlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();

            m_PendingSteps = 0;
            m_Selected = Entity.Null;
            m_SelectedCrossing = -1;
            m_HoveredCrossing = -1;
            m_Dragging = false;
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
            m_Dragging = false;
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

        /// <summary>Widens or narrows the crossing being edited. Called from the toolbar panel.</summary>
        public void AdjustSelected(int steps)
        {
            if (m_Selected == Entity.Null || m_SelectedCrossing < 0 || steps == 0)
            {
                return;
            }

            CrosswalkWidthSetting settings = Mod.Settings;
            float step = (settings != null ? settings.ToolStepPercentage : 25) / 100f;
            float scale = m_OverrideSystem.GetScale(m_Selected, m_SelectedCrossing) + step * steps;

            m_OverrideSystem.SetOverride(m_Selected, m_SelectedCrossing, math.max(0.25f, scale));

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

            m_OverrideSystem.SetOverride(m_Selected, m_SelectedCrossing, 0f);
            Mod.Log.Info(
                $"{Mod.ModName}: junction {m_Selected.Index} crossing {selectedCrossingNumber} "
                + "back on the global width");
        }

        /// <summary>Gives every crossing at this junction the width of the one being edited.</summary>
        public void ApplyToWholeJunction()
        {
            if (m_Selected == Entity.Null || m_SelectedCrossing < 0)
            {
                return;
            }

            float scale = m_OverrideSystem.GetScale(m_Selected, m_SelectedCrossing);

            for (int i = 0; i < m_Crossings.Count; i++)
            {
                m_OverrideSystem.SetOverride(m_Selected, i, scale);
            }

            Mod.Log.Info(
                $"{Mod.ModName}: junction {m_Selected.Index} all {m_Crossings.Count} crossings "
                + $"set to {(int)math.round(scale * 100f)}%");
        }

        /// <summary>The width in force at the crossing being edited, as a percentage.</summary>
        public int SelectedPercent()
        {
            if (m_Selected == Entity.Null || m_SelectedCrossing < 0)
            {
                return 0;
            }

            return (int)math.round(m_OverrideSystem.GetScale(m_Selected, m_SelectedCrossing) * 100f);
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

            m_HoveredCrossing = hasHit ? NearestCrossing(hit.m_HitPosition) : -1;

            if (m_Dragging)
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
                    m_Dragging = false;
                    Mod.Log.Info($"{Mod.ModName}: drag finished at {SelectedPercent()}%");
                }

                DrawHandles(hit);
                return inputDeps;
            }

            Highlight(node);

            if (applyAction.WasPressedThisFrame())
            {
                if (m_HoveredCrossing >= 0)
                {
                    // Pressing on a crossing that is already the live one starts a drag; otherwise
                    // it becomes the live one first, so a single press never both switches crossing
                    // and resizes it.
                    if (m_HoveredCrossing == m_SelectedCrossing)
                    {
                        BeginDrag(hit);
                    }
                    else
                    {
                        m_SelectedCrossing = m_HoveredCrossing;
                        Mod.Log.Info(
                            $"{Mod.ModName}: junction {m_Selected.Index} crossing "
                            + $"{selectedCrossingNumber} of {m_Crossings.Count} at {SelectedPercent()}%");
                    }
                }
                else if (node != Entity.Null)
                {
                    m_Selected = node;
                    m_OverrideSystem.CollectCrossings(m_Selected, m_Crossings);
                    m_SelectedCrossing = m_Crossings.Count > 0 ? NearestCrossing(hit.m_HitPosition) : -1;

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

        /// <summary>Index of the crossing whose middle is nearest the cursor, or -1 if none is near.</summary>
        private int NearestCrossing(float3 point)
        {
            int best = -1;
            float bestDistance = kPickRadius * kPickRadius;

            for (int i = 0; i < m_Crossings.Count; i++)
            {
                float distance = DistanceSq(m_Crossings[i].m_Midpoint, point);

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            return best;
        }

        /// <summary>
        /// Starts a drag on the crossing being edited.
        ///
        /// Relative rather than absolute: it records how far the cursor is from the crossing's
        /// middle when the button goes down and scales from there, so the width does not jump when
        /// the drag starts and the gesture works the same at any zoom.
        /// </summary>
        private void BeginDrag(RaycastHit hit)
        {
            if (m_SelectedCrossing < 0 || m_SelectedCrossing >= m_Crossings.Count)
            {
                return;
            }

            float radius = math.sqrt(DistanceSq(m_Crossings[m_SelectedCrossing].m_Midpoint, hit.m_HitPosition));

            if (radius < 0.5f)
            {
                return;   // too close to the middle for a ratio to mean anything
            }

            m_Dragging = true;
            m_DragStartRadius = radius;
            m_DragStartScale = m_OverrideSystem.GetScale(m_Selected, m_SelectedCrossing);

            Mod.Log.Info(
                $"{Mod.ModName}: dragging junction {m_Selected.Index} crossing "
                + $"{selectedCrossingNumber} from {SelectedPercent()}%");
        }

        private void UpdateDrag(RaycastHit hit)
        {
            if (m_SelectedCrossing < 0 || m_SelectedCrossing >= m_Crossings.Count || m_DragStartRadius < 0.5f)
            {
                return;
            }

            float radius = math.sqrt(DistanceSq(m_Crossings[m_SelectedCrossing].m_Midpoint, hit.m_HitPosition));

            if (radius < 0.5f)
            {
                return;
            }

            float scale = m_DragStartScale * (radius / m_DragStartRadius);
            m_OverrideSystem.SetOverride(m_Selected, m_SelectedCrossing, math.clamp(scale, 0.25f, 8f));
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
            Color liveOutline = m_Dragging ? new Color(1f, 0.8f, 0.3f, 1f) : new Color(0.4f, 1f, 0.7f, 0.95f);
            Color liveFill = m_Dragging ? new Color(1f, 0.8f, 0.3f, 0.3f) : new Color(0.4f, 1f, 0.7f, 0.25f);

            for (int i = 0; i < m_Crossings.Count; i++)
            {
                CrosswalkOverrideSystem.CrossingInfo crossing = m_Crossings[i];

                float width = crossing.m_AuthoredWidth * m_OverrideSystem.GetScale(m_Selected, i);

                if (width <= 0.01f)
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

                buffer.DrawCircle(
                    liveOutline,
                    kHandleClear,
                    kOutlineWidth,
                    OverlayRenderSystem.StyleFlags.Projected,
                    default(float2),
                    crossing.m_Curve.a,
                    kHandleDiameter);

                buffer.DrawCircle(
                    liveOutline,
                    kHandleClear,
                    kOutlineWidth,
                    OverlayRenderSystem.StyleFlags.Projected,
                    default(float2),
                    crossing.m_Curve.d,
                    kHandleDiameter);
            }

            m_OverlayRenderSystem.AddBufferWriter(default(JobHandle));
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
