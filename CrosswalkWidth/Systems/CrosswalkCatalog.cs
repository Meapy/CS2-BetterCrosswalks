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

        /// <summary>
        /// Used to read the authored width off the managed prefab rather than out of the ECS
        /// component.
        ///
        /// It matters which one is trusted. NetLaneData.m_Width is a live component that earlier
        /// versions of this mod wrote to, and a city saved while one of those was running can carry
        /// the inflated figure. PedestrianLane.m_Width on the managed asset is what the asset
        /// author set and nothing in the game or in this mod writes to it, so it is the one figure
        /// that is still true after a session that went wrong.
        /// </summary>
        public PrefabSystem PrefabSystem { get; set; }

        public int LaneCount => m_Lanes.Count;

        public int CrossingPieceCount { get; private set; }

        /// <summary>True if this lane prefab is one the game lays crossings along.</summary>
        public bool IsCrossingLane(Entity lane)
        {
            return m_Lanes.ContainsKey(lane);
        }

        /// <summary>The width the asset author gave this crossing lane, before any scaling.</summary>
        public float AuthoredWidth(Entity lane)
        {
            return m_Lanes.TryGetValue(lane, out LaneBaseline baseline) ? baseline.m_Width : 0f;
        }

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

            // The lane a road names is often a placeholder, not the lane that gets laid. LaneSystem
            // resolves it through CheckPrefab, which picks a themed variant out of the placeholder's
            // PlaceholderObjectElement buffer according to the city's theme — so a North American
            // city lays "NA Crosswalk Lane 2" where the road declared "Crosswalk Lane 2".
            //
            // Matching a laid lane against only the declared prefab therefore finds nothing at all,
            // which is exactly what the log reported: 58 sub-lanes walked, none recognised, one of
            // them plainly named NA Crosswalk Lane 2. Every variant is captured with its own
            // authored width, since the variants are not all drawn the same.
            CaptureVariants(em, lane, declaredBy);

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
                m_Width = AuthoredWidthOf(em, lane)
            };

            baseline.m_DeclaredBy.Add(declaredBy);
            m_Lanes.Add(lane, baseline);
        }

        /// <summary>
        /// The width the asset author gave a lane: the managed PedestrianLane component if it can
        /// be reached, otherwise whatever the ECS component currently says.
        /// </summary>
        private float AuthoredWidthOf(EntityManager em, Entity lane)
        {
            if (PrefabSystem != null
                && PrefabSystem.TryGetPrefab<PrefabBase>(lane, out PrefabBase prefab)
                && prefab != null
                && prefab.TryGet(out Game.Prefabs.PedestrianLane pedestrianLane)
                && pedestrianLane != null
                && pedestrianLane.m_Width > 0.01f)
            {
                return pedestrianLane.m_Width;
            }

            return em.GetComponentData<NetLaneData>(lane).m_Width;
        }

        /// <summary>
        /// Records the themed variants a placeholder lane can resolve to.
        ///
        /// One level of nesting is walked, guarded against cycles, because a variant can itself be
        /// a placeholder in principle.
        /// </summary>
        private void CaptureVariants(EntityManager em, Entity lane, Entity declaredBy)
        {
            if (!em.HasBuffer<PlaceholderObjectElement>(lane))
            {
                return;
            }

            DynamicBuffer<PlaceholderObjectElement> variants =
                em.GetBuffer<PlaceholderObjectElement>(lane, true);

            for (int i = 0; i < variants.Length; i++)
            {
                Entity variant = variants[i].m_Object;

                if (variant == Entity.Null || variant == lane || m_Lanes.ContainsKey(variant))
                {
                    continue;
                }

                Capture(em, variant, declaredBy);
            }
        }

        /// <summary>
        /// Also records the lane named by every crossing the game has actually built.
        ///
        /// NetCrosswalkData on the piece prefabs is what a road *declares*; NetCompositionCrosswalk
        /// is what a composition ended up with once AddCompositionCrosswalks had merged the spans.
        /// They are normally the same lane, but only the second is the prefab LaneSystem lays, so a
        /// road family that substitutes one is only visible here.
        /// </summary>
        public void DiscoverFromCompositions(EntityManager em, NativeArray<Entity> compositions)
        {
            for (int i = 0; i < compositions.Length; i++)
            {
                Entity composition = compositions[i];

                if (!em.HasBuffer<NetCompositionCrosswalk>(composition))
                {
                    continue;
                }

                DynamicBuffer<NetCompositionCrosswalk> crosswalks =
                    em.GetBuffer<NetCompositionCrosswalk>(composition, true);

                for (int j = 0; j < crosswalks.Length; j++)
                {
                    Entity lane = crosswalks[j].m_Lane;

                    Capture(em, lane, composition);

                    if (lane != Entity.Null && em.HasComponent<PedestrianLaneData>(lane))
                    {
                        Capture(em, em.GetComponentData<PedestrianLaneData>(lane).m_NotWalkLanePrefab, composition);
                    }
                }
            }
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
        /// The mod no longer widens prefabs — width is applied per crossing, on the lane, through
        /// NodeLane.m_WidthOffset, which is what the game itself does and the only thing the
        /// renderer honours. This is kept for the one call that still matters: factor 1, which puts
        /// a shared prefab back to its authored width and so undoes anything an earlier version of
        /// this mod left baked into a save.
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
