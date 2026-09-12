using System.Collections.Generic;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CrosswalkWidth.Systems
{
    /// <summary>
    /// Moves crossings far enough out of the junction that a widened one still fits, and lets the
    /// game pull the roads, stop lines and signals back to make room.
    ///
    /// This is not something the mod has to build. A junction's size already depends on how far out
    /// its crossings sit: GeometrySystem.CalculateCornerOffset asks
    ///
    ///     CheckCrosswalks(crosswalks) = max over crossings of max(m_Start.z, m_End.z)
    ///
    /// and folds the answer into the corner offset that decides where each edge stops short of the
    /// node. Everything anchored to that geometry — the carriageway, the stop line, the signal
    /// posts — follows. So the whole job here is to increase `z` by as much as the crossing grew,
    /// and the game does the rest with its own rules about how junctions are shaped.
    ///
    /// `z` is how far *inside* the edge end the crossing sits: LaneSystem applies it as
    /// `position += tangent * m_Start.z`, and that tangent points from the edge end toward the node
    /// centre. So one number says both "the crossing is this far in" and, through CheckCrosswalks,
    /// "the junction must be this much bigger to hold it" — which is why raising it moves the road
    /// back rather than moving the crossing forward into the traffic. Raising it by half the growth
    /// keeps the crossing's outer edge exactly as far from the stop line as the game intended,
    /// which is the overlap in the screenshot.
    ///
    /// How completely the road makes way is the game's call, not this mod's: GeometrySystem scales
    /// the offset by how directly the two edges face each other, so a straight-through pair gets
    /// the full push and a sharp corner gets less. That is the same rule the game applies to its
    /// own crossings, which is the argument for using this lever rather than inventing one.
    ///
    /// Two things to know about the data:
    ///
    /// - NetCompositionCrosswalk lives on the composition prefab, shared by every junction using
    ///   that composition. This is therefore a global adjustment, driven by the global percentage.
    ///   A single junction widened past that by hand can still overlap; nothing at composition
    ///   level can know about it.
    /// - NetCompositionSystem rebuilds the buffer whenever it re-initialises a composition, which
    ///   silently drops the change. So every pass re-derives what it expects to find and repairs
    ///   anything that no longer matches, rather than assuming one write holds forever.
    ///
    /// Prefab data is not written into a city save (prefab entities are excluded from the
    /// serializer), so this cannot outlive the session — but it is restored on disable and on
    /// shutdown anyway, because a mod turned off should leave nothing behind in the running game
    /// either.
    /// </summary>
    internal sealed class CrosswalkClearance
    {
        /// <summary>What one composition's crossings were before this mod moved them.</summary>
        private sealed class Baseline
        {
            /// <summary>Authored z of each crossing, start and end, in buffer order.</summary>
            public readonly List<float2> m_Z = new List<float2>();
        }

        private readonly Dictionary<Entity, Baseline> m_Baselines = new Dictionary<Entity, Baseline>();

        /// <summary>Difference below which two offsets count as the same.</summary>
        private const float kEpsilon = 0.005f;

        public int CompositionCount => m_Baselines.Count;

        /// <summary>Metres the last pass pushed crossings out by. For the log.</summary>
        public float LastPush { get; private set; }

        /// <summary>
        /// Brings every composition's crossings to `authored z + push`, where push is worked out per
        /// crossing from the width its lane will be laid at.
        ///
        /// Returns the number of compositions changed, so the caller only hands the city back to the
        /// geometry pipeline when something actually moved.
        /// </summary>
        public int Apply(
            EntityManager em,
            NativeArray<Entity> compositions,
            float scale,
            float minimumWidth,
            float maximumWidth,
            float roomFactor,
            CrosswalkCatalog catalog)
        {
            int changed = 0;
            float maxPush = 0f;

            for (int i = 0; i < compositions.Length; i++)
            {
                Entity composition = compositions[i];

                if (!em.HasBuffer<NetCompositionCrosswalk>(composition))
                {
                    continue;
                }

                DynamicBuffer<NetCompositionCrosswalk> crosswalks =
                    em.GetBuffer<NetCompositionCrosswalk>(composition);

                if (crosswalks.Length == 0)
                {
                    continue;
                }

                Baseline baseline = Recapture(crosswalks, composition);

                bool touched = false;

                for (int j = 0; j < crosswalks.Length; j++)
                {
                    NetCompositionCrosswalk crosswalk = crosswalks[j];

                    // The width this crossing is laid at, which is the declared lane's width — the
                    // same figure the per-crossing widening scales from, so the two agree about how
                    // much wider a crossing has become.
                    float width = catalog != null ? catalog.AuthoredWidth(crosswalk.m_Lane) : 0f;

                    if (width <= 0.01f)
                    {
                        continue;
                    }

                    // A share of the growth the crossing will actually get, which is not the same
                    // as a share of the percentage: a floor of 10m on a 4m crossing widens it two and a half
                    // times at 100%, where the percentage alone says nothing has changed and would
                    // make no room at all. A ceiling is the same story the other way — 400% capped
                    // at 5m would otherwise enlarge every junction in the city for one metre of
                    // crossing.
                    float target = width * scale;

                    if (minimumWidth > 0f)
                    {
                        target = math.max(target, minimumWidth);
                    }

                    if (maximumWidth > 0f)
                    {
                        target = math.min(target, maximumWidth);
                    }

                    float push = math.max(0f, (target - width) * roomFactor);
                    maxPush = math.max(maxPush, push);

                    float2 authored = baseline.m_Z[j];
                    float2 targetZ = authored + push;

                    if (math.abs(crosswalk.m_Start.z - targetZ.x) < kEpsilon
                        && math.abs(crosswalk.m_End.z - targetZ.y) < kEpsilon)
                    {
                        continue;
                    }

                    crosswalk.m_Start.z = targetZ.x;
                    crosswalk.m_End.z = targetZ.y;
                    crosswalks[j] = crosswalk;
                    touched = true;
                }

                if (touched)
                {
                    changed++;
                }
            }

            LastPush = maxPush;

            return changed;
        }

        /// <summary>Puts every composition's crossings back where its author put them.</summary>
        public int Restore(EntityManager em, NativeArray<Entity> compositions)
        {
            int changed = 0;

            for (int i = 0; i < compositions.Length; i++)
            {
                Entity composition = compositions[i];

                if (!m_Baselines.TryGetValue(composition, out Baseline baseline)
                    || !em.Exists(composition)
                    || !em.HasBuffer<NetCompositionCrosswalk>(composition))
                {
                    continue;
                }

                DynamicBuffer<NetCompositionCrosswalk> crosswalks =
                    em.GetBuffer<NetCompositionCrosswalk>(composition);

                if (crosswalks.Length != baseline.m_Z.Count)
                {
                    continue;   // rebuilt since; it already holds the authored values
                }

                bool touched = false;

                for (int j = 0; j < crosswalks.Length; j++)
                {
                    NetCompositionCrosswalk crosswalk = crosswalks[j];
                    float2 authored = baseline.m_Z[j];

                    if (math.abs(crosswalk.m_Start.z - authored.x) < kEpsilon
                        && math.abs(crosswalk.m_End.z - authored.y) < kEpsilon)
                    {
                        continue;
                    }

                    crosswalk.m_Start.z = authored.x;
                    crosswalk.m_End.z = authored.y;
                    crosswalks[j] = crosswalk;
                    touched = true;
                }

                if (touched)
                {
                    changed++;
                }
            }

            LastPush = 0f;

            return changed;
        }

        /// <summary>
        /// The authored offsets for one composition, recaptured if the game has rebuilt the buffer
        /// since they were last taken.
        ///
        /// The test is whether what is there now is what this mod last wrote. If it is not, either
        /// nothing has been written yet or NetCompositionSystem has re-initialised the composition
        /// and put the authored values back — and in both of those cases what is there now *is* the
        /// authored value. That is what keeps this from compounding: the baseline is never taken
        /// from a figure this mod produced.
        /// </summary>
        private Baseline Recapture(DynamicBuffer<NetCompositionCrosswalk> crosswalks, Entity composition)
        {
            if (m_Baselines.TryGetValue(composition, out Baseline baseline)
                && baseline.m_Z.Count == crosswalks.Length
                && Matches(crosswalks, baseline))
            {
                return baseline;
            }

            if (baseline == null)
            {
                baseline = new Baseline();
                m_Baselines.Add(composition, baseline);
            }

            baseline.m_Z.Clear();

            for (int i = 0; i < crosswalks.Length; i++)
            {
                baseline.m_Z.Add(new float2(crosswalks[i].m_Start.z, crosswalks[i].m_End.z));
            }

            return baseline;
        }

        /// <summary>True if the buffer still holds either the baseline or the baseline plus a push.</summary>
        private bool Matches(DynamicBuffer<NetCompositionCrosswalk> crosswalks, Baseline baseline)
        {
            for (int i = 0; i < crosswalks.Length; i++)
            {
                float2 authored = baseline.m_Z[i];
                float startDelta = crosswalks[i].m_Start.z - authored.x;
                float endDelta = crosswalks[i].m_End.z - authored.y;

                // Either untouched, or carrying a push this mod could have written. A negative
                // delta, or two ends that disagree, means the game rebuilt it from different
                // pieces and the record is stale.
                if (startDelta < -kEpsilon || endDelta < -kEpsilon
                    || math.abs(startDelta - endDelta) > kEpsilon)
                {
                    return false;
                }
            }

            return true;
        }

        public void Forget()
        {
            m_Baselines.Clear();
            LastPush = 0f;
        }
    }
}
