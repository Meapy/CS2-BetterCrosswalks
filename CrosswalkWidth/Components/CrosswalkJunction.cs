using Colossal.Serialization.Entities;
using Unity.Entities;

namespace CrosswalkWidth.Components
{
    /// <summary>
    /// The junction a crossing this mod laid belongs to.
    ///
    /// This exists instead of an <see cref="Game.Common.Owner"/> component, and that is the point of
    /// it. A lane with an Owner is put into that junction's SubLane buffer by the game, and
    /// <c>TrafficLightInitializationSystem.InitializeTrafficLights</c> walks that buffer by number:
    ///
    /// <code>
    /// for (int j = laneGroup.m_LaneRange.x; j &lt;= laneGroup.m_LaneRange.y; j++)
    /// {
    ///     Entity subLane = subLanes[j].m_SubLane;               // no bounds check
    ///     LaneSignal laneSignal = m_LaneSignalData[subLane];    // no HasComponent check
    /// </code>
    ///
    /// The range comes from <c>MasterLane.m_MinIndex</c> / <c>m_MaxIndex</c>, worked out from the
    /// road's composition when the junction was laid rather than from the buffer as it now stands.
    /// Putting a lane of this mod's into that buffer moves everything after it, and those numbers
    /// then land on the wrong lane or past the end. Both lines above are unguarded and inside a
    /// Burst job, so what follows is the process vanishing with no managed stacktrace — which is
    /// what happened every time these crossings were added, removed, or merely present while a
    /// junction was updated.
    ///
    /// A lane with no owner is in no such buffer, so it cannot move anything the game is counting
    /// on. What that costs is everything the game does for a lane by walking its junction: this mod
    /// has to clear its own crossings when a junction is rebuilt, because LaneSystem no longer will.
    /// </summary>
    public struct CrosswalkJunction : IComponentData, IQueryTypeParameter, ISerializable
    {
        public const int kCurrentVersion = 2;

        public int m_Version;

        public Entity m_Junction;

        /// <summary>
        /// Which of the junction's diagonals this is — first, second, and so on around the ring.
        ///
        /// The key a width or a position set by hand is stored under. It has to be something that
        /// survives the lane being cleared and laid again, which an entity or a position in a list
        /// does not: these lanes are rebuilt from scratch every time the junction changes shape, and
        /// they come back in the same order because the corners are worked out in the same order.
        /// </summary>
        public int m_Ordinal;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kCurrentVersion);
            writer.Write(m_Junction);
            writer.Write(m_Ordinal);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Version);
            reader.Read(out m_Junction);

            // Appended after version 1, so a save written by that version has no such field to read.
            if (m_Version >= 2)
            {
                reader.Read(out m_Ordinal);
            }
        }
    }
}
