using Colossal.Serialization.Entities;
using Unity.Entities;

namespace CrosswalkWidth.Components
{
    /// <summary>
    /// A per-junction crossing width, held on the node entity and saved with the city.
    ///
    /// Stored as a multiplier on the crossing's authored width rather than a figure in metres,
    /// because a junction can have more than one kind of crossing on it and they are not all
    /// authored the same width. A multiplier keeps them in proportion; an absolute figure would
    /// flatten a wide boulevard crossing and a narrow side-street one to the same size.
    ///
    /// The version field is written first and read first, and it is here from the very first
    /// release on purpose. Colossal's serializer reads a component back by size: add a field to a
    /// component that shipped without a version and every existing save throws
    /// ComponentSerializerException on load. With the version in place, a later field can be
    /// appended at the end and read conditionally.
    /// </summary>
    public struct CrosswalkOverride : IComponentData, IQueryTypeParameter, ISerializable
    {
        public const int kCurrentVersion = 1;

        public int m_Version;

        /// <summary>Multiplier on the crossing lane's authored width.</summary>
        public float m_Scale;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kCurrentVersion);
            writer.Write(m_Scale);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Version);
            reader.Read(out m_Scale);

            // Anything appended in a future version is read here, guarded by m_Version.
        }
    }
}
