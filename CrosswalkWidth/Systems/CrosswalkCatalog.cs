using System;
using System.Collections.Generic;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CrosswalkWidth.Systems
{
    /// <summary>
    /// Finds the lane prefabs the game lays a pedestrian crossing along, remembers their authored
    /// width, and rewrites it.
    ///
    /// A crossing is not part of the road's cross-section at all. It is a lane laid across the
    /// carriageway:
    ///
    ///   NetPieceCrosswalk on a piece declares a span (start.x to end.x) and names a lane prefab,
    ///   which becomes NetCrosswalkData on that piece entity;
    ///   NetCompositionSystem.AddCompositionCrosswalks merges adjacent spans into one
    ///   NetCompositionCrosswalk per composition;
    ///   LaneSystem then lays a pedestrian lane along that span at every node.
    ///
    /// The painted zebra is that lane's mesh. A lane's drawn width is NetLaneData.m_Width on its
    /// prefab — LaneSystem defines NodeLane.m_WidthOffset as the *difference* between two lane
    /// prefabs' widths, which is only meaningful if that field is what the lane is drawn at:
    ///
    ///     component3.m_WidthOffset.x = componentData.m_Width - netLaneData.m_Width;
    ///
    /// A crossing lane runs across the road, so its width is the depth of the band in the direction
    /// traffic travels. One field therefore covers both halves of the request: more paint on the
    /// ground, and more room for people to cross side by side rather than in single file.
    ///
    /// Every write is base * factor rather than current * factor, from a baseline captured before
    /// the first write. NetLaneData is ISerializable — it goes into the save — so a read-modify-
    /// write here would climb every session with no way back to the authored figure.
    /// </summary>
    internal sealed class CrosswalkCatalog
    {
        internal sealed class LaneBaseline
        {
            public Entity m_Lane;
            public float m_Width;

            /// <summary>Piece prefabs whose NetCrosswalkData named this lane. For the log.</summary>
            public readonly List<Entity> m_DeclaredBy = new List<Entity>();

            /// <summary>
            /// True if this same lane prefab is also laid along a piece as an ordinary lane.
            ///
            /// Not filtered out, only reported: if a road family shares one prefab between its
            /// crossings and its kerbside walking lanes, widening it widens both, and the log is
            /// where that would show up rather than in a puzzling screenshot.
            /// </summary>
            public bool m_AlsoAPieceLane;
        }

        private readonly Dictionary<Entity, LaneBaseline> m_Lanes = new Dictionary<Entity, LaneBaseline>();

        public int LaneCount => m_Lanes.Count;

        public int CrossingPieceCount { get; private set; }

        /// <summary>
        /// Records the crossing lane prefab named by every piece that declares a crossing.
        ///
        /// Additive: a lane already known keeps the baseline it was first captured with, so a
        /// second pass after assets load cannot recapture a width this mod already wrote.
        /// </summary>
        public void Discover(EntityManager em, NativeArray<Entity> crossingPieces)
        {
            CrossingPieceCount = crossingPieces.Length;

            for (int i = 0; i < crossingPieces.Length; i++)
            {
                Entity piece = crossingPieces[i];

                if (!em.HasComponent<NetCrosswalkData>(piece))
                {
                    continue;
                }

                Entity lane = em.GetComponentData<NetCrosswalkData>(piece).m_Lane;

                Capture(em, lane, piece);

                // Some connections swap the walkable crossing lane for a "not walk" variant —
                // PedestrianLaneData.m_NotWalkLanePrefab, used where a crossing meets something a
                // citizen may not step onto. It is drawn in the same place, so leaving it at the
                // authored width would show a narrow band butted against a wide one.
                if (lane != Entity.Null && em.HasComponent<PedestrianLaneData>(lane))
                {
                    Capture(em, em.GetComponentData<PedestrianLaneData>(lane).m_NotWalkLanePrefab, piece);
                }
            }
        }

        private void Capture(EntityManager em, Entity lane, Entity declaredBy)
        {
            if (lane == Entity.Null || !em.Exists(lane) || !em.HasComponent<NetLaneData>(lane))
            {
                return;
            }

            if (m_Lanes.TryGetValue(lane, out LaneBaseline existing))
            {
                if (!existing.m_DeclaredBy.Contains(declaredBy))
                {
                    existing.m_DeclaredBy.Add(declaredBy);
                }

                return;
            }

            LaneBaseline baseline = new LaneBaseline
            {
                m_Lane = lane,
                m_Width = em.GetComponentData<NetLaneData>(lane).m_Width
            };

            baseline.m_DeclaredBy.Add(declaredBy);
            m_Lanes.Add(lane, baseline);
        }

        /// <summary>
        /// Marks the crossing lanes that are also used as ordinary lanes along a piece.
        ///
        /// Reporting only — see LaneBaseline.m_AlsoAPieceLane.
        /// </summary>
        public void NotePieceLanes(EntityManager em, NativeArray<Entity> piecesWithLanes)
        {
            for (int i = 0; i < piecesWithLanes.Length; i++)
            {
                Entity piece = piecesWithLanes[i];

                if (!em.HasBuffer<NetPieceLane>(piece))
                {
                    continue;
                }

                DynamicBuffer<NetPieceLane> lanes = em.GetBuffer<NetPieceLane>(piece, true);

                for (int j = 0; j < lanes.Length; j++)
                {
                    if (m_Lanes.TryGetValue(lanes[j].m_Lane, out LaneBaseline baseline))
                    {
                        baseline.m_AlsoAPieceLane = true;
                    }
                }
            }
        }

        /// <summary>
        /// Rewrites every recorded crossing lane to base * factor, clamped.
        ///
        /// Factor 1 with no clamps restores the authored figures exactly, which is what disabling
        /// the mod and shutting down both do.
        /// </summary>
        public void Apply(EntityManager em, float factor, float minimumWidth, float maximumWidth)
        {
            foreach (KeyValuePair<Entity, LaneBaseline> entry in m_Lanes)
            {
                LaneBaseline baseline = entry.Value;

                if (!em.Exists(baseline.m_Lane) || !em.HasComponent<NetLaneData>(baseline.m_Lane))
                {
                    continue;
                }

                float target = baseline.m_Width * factor;

                if (minimumWidth > 0f)
                {
                    target = math.max(target, minimumWidth);
                }

                if (maximumWidth > 0f)
                {
                    target = math.min(target, maximumWidth);
                }

                NetLaneData laneData = em.GetComponentData<NetLaneData>(baseline.m_Lane);
                laneData.m_Width = math.max(0.1f, target);
                em.SetComponentData(baseline.m_Lane, laneData);
            }
        }

        public void Restore(EntityManager em)
        {
            Apply(em, 1f, 0f, 0f);
        }

        /// <summary>What the mod found and what it did to it, as lines for the game log.</summary>
        public IEnumerable<string> Describe(EntityManager em, Func<Entity, string> nameOf)
        {
            yield return $"{CrossingPieceCount} pieces declare a crossing, {m_Lanes.Count} crossing lane prefabs";

            foreach (KeyValuePair<Entity, LaneBaseline> entry in m_Lanes)
            {
                LaneBaseline baseline = entry.Value;

                float current = em.Exists(baseline.m_Lane) && em.HasComponent<NetLaneData>(baseline.m_Lane)
                    ? em.GetComponentData<NetLaneData>(baseline.m_Lane).m_Width
                    : 0f;

                float ratio = baseline.m_Width > 0.001f ? current / baseline.m_Width : 1f;

                yield return $"  {nameOf(baseline.m_Lane)}: {baseline.m_Width:0.00}m -> {current:0.00}m (x{ratio:0.00})"
                    + (baseline.m_AlsoAPieceLane ? "  [also used as an ordinary lane]" : string.Empty);

                for (int i = 0; i < baseline.m_DeclaredBy.Count && i < 6; i++)
                {
                    yield return $"      declared by {nameOf(baseline.m_DeclaredBy[i])}";
                }

                if (baseline.m_DeclaredBy.Count > 6)
                {
                    yield return $"      ... and {baseline.m_DeclaredBy.Count - 6} more pieces";
                }
            }
        }
    }
}
