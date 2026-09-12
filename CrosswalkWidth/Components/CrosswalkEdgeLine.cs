using Colossal.Serialization.Entities;
using Unity.Entities;

namespace CrosswalkWidth.Components
{
    /// <summary>
    /// One of the two painted lines down the sides of a crossing this mod laid through the middle
    /// of a junction.
    ///
    /// Everywhere else in the city these lines cost no code at all: the game lays them itself from a
    /// <c>SecondaryNetLane</c> entry on the crossing lane prefab, and creates and destroys them with
    /// their junction. That mechanism cannot reach a middle crossing. <c>SecondaryLaneSystem</c>
    /// finds lanes by walking each junction's <c>SubLane</c> buffer, and a middle crossing is
    /// deliberately not in one — see <see cref="CrosswalkJunction"/> for what happened when it was.
    ///
    /// So for these, and only these, the mod lays the lines itself. They are lanes of its own like
    /// the crossing they border: no <c>Owner</c>, marked <see cref="CrosswalkAdded"/> and
    /// <see cref="CrosswalkJunction"/> so every path that clears a junction's middle crossings
    /// clears their lines with them, and marked with this so the rest of the mod can tell a line
    /// from a crossing. The tool must never offer one as a crossing to select, and the width pass
    /// must never treat one as a crossing to widen.
    ///
    /// They are not laid once and left. Their curve is worked out from the crossing's own curve and
    /// width every pass, so a middle crossing dragged wider or slid along the road takes its lines
    /// with it — the thing the game's own lines cannot do, because it lays those once and never
    /// looks at them again.
    /// </summary>
    public struct CrosswalkEdgeLine : IComponentData, IQueryTypeParameter, ISerializable
    {
        public const int kCurrentVersion = 1;

        public int m_Version;

        /// <summary>
        /// The middle crossing this line borders.
        ///
        /// An entity reference, which is usually the wrong thing to save — but right here. These
        /// crossings are destroyed and laid again whenever their junction changes, and a line whose
        /// crossing has gone is exactly a line that should go too. So the reference going stale is
        /// the signal, not a fault: the pass that finds one takes the line out, and the next lay-out
        /// puts both back together.
        /// </summary>
        public Entity m_Crossing;

        /// <summary>Which side of the crossing: -1 one way, +1 the other.</summary>
        public int m_Side;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kCurrentVersion);
            writer.Write(m_Crossing);
            writer.Write(m_Side);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Version);
            reader.Read(out m_Crossing);
            reader.Read(out m_Side);
        }
    }
}
