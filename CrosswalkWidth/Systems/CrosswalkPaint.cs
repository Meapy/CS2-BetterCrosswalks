using Colossal.Mathematics;
using Game.Net;
using Unity.Entities;

namespace CrosswalkWidth.Systems
{
    /// <summary>
    /// Hides a lane's paint without touching the lane.
    ///
    /// The request was "stop drawing this crossing, but let it go on being one", and almost every
    /// way the game has of not drawing a lane also changes what the lane is:
    ///
    /// - <c>PedestrianLaneFlags.Unsafe</c> is how an unmarked crossing draws no zebra, and it is also
    ///   what makes it an unmarked crossing — no signals, and citizens treat it as jaywalking.
    /// - <c>Game.Tools.Hidden</c> is what the renderer honours, but <c>LaneHiddenSystem</c> takes it
    ///   straight back off any lane whose owner is not hidden too, every frame.
    /// - Removing <c>CullingInfo</c> is how <c>LaneSystem</c> lays a lane that is never drawn — at the
    ///   moment it creates one. On a lane already standing, the culling system is holding an index
    ///   into it and releases that index only when the lane is deleted.
    ///
    /// What is left is <see cref="CutRange"/>. <c>LaneSystem</c> writes it onto a crossing to stop its
    /// paint running into the pavement at either end, and <c>BatchInstanceSystem</c> counts tiles only
    /// in the gaps between the ranges:
    ///
    /// <code>
    /// if (num3 &gt;= num2) { curve2.m_Length = curve.m_Length * (num3 - num2); if (curve2.m_Length &gt; 0.1f) num += GetTileCount(...); }
    /// </code>
    ///
    /// so a single range from 0 to 1 leaves no gap, no tiles, and no instance at all. Nothing that
    /// simulates reads it: pathfinding and creatures never do, and the only other system that
    /// rewrites it, <c>Game.Net.OverrideSystem</c>, returns at once for any lane that is not a fence.
    ///
    /// A single range covering the whole curve is also something the game never writes — its own cuts
    /// are end trims, one or two ranges that always leave the middle — so it doubles as the record of
    /// what this mod did. Anything carrying exactly that was hidden here, and taking it off can never
    /// take off one of the game's.
    /// </summary>
    internal static class CrosswalkPaint
    {
        /// <summary>True if this lane carries the one cut this mod writes: all of it.</summary>
        public static bool IsHidden(EntityManager em, Entity lane)
        {
            if (lane == Entity.Null || !em.Exists(lane) || !em.HasBuffer<CutRange>(lane))
            {
                return false;
            }

            DynamicBuffer<CutRange> ranges = em.GetBuffer<CutRange>(lane, true);

            return ranges.Length == 1
                && ranges[0].m_CurveDelta.min <= 0.0001f
                && ranges[0].m_CurveDelta.max >= 0.9999f;
        }

        /// <summary>
        /// Hides a lane's paint, or shows it again. True if anything changed.
        ///
        /// Showing it again removes only the whole-curve cut. A lane carrying the game's own end trims
        /// is left alone, which is why "was this hidden" is answered from the cut itself rather than
        /// from anything remembered — there is nothing to get out of step.
        /// </summary>
        public static bool SetHidden(EntityManager em, Entity lane, bool hide)
        {
            if (lane == Entity.Null || !em.Exists(lane) || hide == IsHidden(em, lane))
            {
                return false;
            }

            if (hide)
            {
                DynamicBuffer<CutRange> ranges = em.HasBuffer<CutRange>(lane)
                    ? em.GetBuffer<CutRange>(lane)
                    : em.AddBuffer<CutRange>(lane);

                ranges.Clear();
                ranges.Add(new CutRange { m_CurveDelta = new Bounds1(0f, 1f) });
            }
            else
            {
                em.RemoveComponent<CutRange>(lane);
            }

            // How many instances a lane gets is decided when its batch is built, so the renderer has
            // to be told to build it again. Updated would hand the lane to the net pipeline instead.
            if (!em.HasComponent<Game.Common.BatchesUpdated>(lane))
            {
                em.AddComponent<Game.Common.BatchesUpdated>(lane);
            }

            return true;
        }
    }
}
