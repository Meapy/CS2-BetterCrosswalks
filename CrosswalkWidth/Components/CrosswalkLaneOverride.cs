using Colossal.Serialization.Entities;
using Unity.Entities;

namespace CrosswalkWidth.Components
{
    /// <summary>
    /// The width of one individual crossing at a junction, saved with the city.
    ///
    /// A junction usually has one crossing per arm, and they are not always wanted at the same
    /// size — a busy approach may deserve a broad crossing while the quiet side street keeps a
    /// modest one. So the per-junction <see cref="CrosswalkOverride"/> is the default for the whole
    /// junction, and an entry here overrides it for a single crossing.
    ///
    /// Crossings are identified by their position in the node's ordered list of crossing lanes
    /// rather than by entity, because LaneSystem destroys and re-creates a node's lanes whenever it
    /// re-lays it — an entity reference would not survive the first road edit nearby. The order of
    /// the node's SubLane buffer is stable for a given junction layout, and if the layout does
    /// change, the worst case is that an override lands on a neighbouring crossing rather than
    /// being lost.
    ///
    /// The version field is written first and read first, from the first release, so a later field
    /// can be appended without every existing save throwing on load.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct CrosswalkLaneOverride : IBufferElementData, ISerializable
    {
        public const int kCurrentVersion = 1;

        public int m_Version;

        /// <summary>Position of this crossing in the node's ordered list of crossing lanes.</summary>
        public int m_Index;

        /// <summary>Multiplier on the crossing lane's authored width.</summary>
        public float m_Scale;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kCurrentVersion);
            writer.Write(m_Index);
            writer.Write(m_Scale);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Version);
            reader.Read(out m_Index);
            reader.Read(out m_Scale);
        }
    }
}
