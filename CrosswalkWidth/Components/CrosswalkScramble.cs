using Colossal.Serialization.Entities;
using Unity.Entities;

namespace CrosswalkWidth.Components
{
    /// <summary>
    /// Marks a junction as having diagonal crossings through the middle — a scramble crossing, the
    /// Shibuya kind.
    ///
    /// Only the intent is stored, never the crossings themselves. LaneSystem rebuilds a node's lanes
    /// from its composition whenever it re-lays it, and a lane this mod added is not in that
    /// composition, so it is deleted every time — a road edited nearby, a signal changed, a save
    /// reloaded. Storing the two diagonals as data would mean storing entity references that stop
    /// being true almost immediately.
    ///
    /// So the junction carries a flag, and the diagonals are worked out from the crossings that are
    /// there right now and laid again whenever they go missing. That also means the diagonals follow
    /// the junction: widen a road and the corners move, and the next pass runs the diagonals between
    /// wherever the corners ended up.
    /// </summary>
    public struct CrosswalkScramble : IComponentData, IQueryTypeParameter, ISerializable
    {
        public const int kCurrentVersion = 2;

        public int m_Version;

        /// <summary>
        /// Which diagonals the player has taken out by hand, one bit per ordinal.
        ///
        /// Needed because the diagonals are not stored — they are laid again from the junction's
        /// shape whenever they go missing, and without this a crossing removed by hand would simply
        /// come back the next time the junction was touched.
        /// </summary>
        public int m_Removed;

        public bool IsRemoved(int ordinal)
        {
            return ordinal >= 0 && ordinal < 32 && (m_Removed & (1 << ordinal)) != 0;
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kCurrentVersion);
            writer.Write(m_Removed);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Version);

            if (m_Version >= 2)
            {
                reader.Read(out m_Removed);
            }
        }
    }
}
