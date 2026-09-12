using Colossal.Serialization.Entities;
using Unity.Entities;
using Unity.Mathematics;

namespace CrosswalkWidth.Components
{
    /// <summary>
    /// What has been changed about one individual crossing at a junction, saved with the city.
    ///
    /// A junction usually has one crossing per arm, and they are not always wanted the same — a busy
    /// approach may deserve a broad crossing well clear of the traffic while the quiet side street
    /// keeps a modest one where it is. So the per-junction <see cref="CrosswalkOverride"/> is the
    /// default for the whole junction, and an entry here overrides it for a single crossing.
    ///
    /// Two things can be set:
    ///
    /// - <see cref="m_Scale"/>, a multiplier on the width the game lays the crossing at.
    /// - <see cref="m_Shift"/>, how far each end has been moved along the road, in metres. Moving
    ///   both ends together slides the crossing towards or away from the junction; moving one end
    ///   alone swings it, so it runs from a different point on one kerb — a crossing set at an
    ///   angle rather than square across.
    ///
    /// The shift is stored with the curve it was measured from (<see cref="m_BaseStart"/> and
    /// <see cref="m_BaseEnd"/>) rather than on its own. LaneSystem rewrites a crossing's curve every
    /// time it re-lays the junction, and this mod writes over that afterwards — so without a record
    /// of what the game laid, the next pass would shift an already-shifted crossing and the crossing
    /// would walk down the street a little further every time the junction was touched. With it,
    /// every write is absolute, and a curve that matches neither the base nor the shifted position
    /// is recognised as a junction that has changed shape and adopted as the new base.
    ///
    /// Crossings are identified by their position in the node's ordered list of crossing lanes
    /// rather than by entity, because LaneSystem destroys and re-creates a node's lanes whenever it
    /// re-lays it — an entity reference would not survive the first road edit nearby. The order of
    /// the node's SubLane buffer is stable for a given junction layout, and entries whose index no
    /// longer names a crossing are pruned when the junction is next edited.
    ///
    /// The version field is written first and read first, from the first release, so the fields
    /// added in version 2 can be absent from an older save without every load throwing.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct CrosswalkLaneOverride : IBufferElementData, ISerializable
    {
        public const int kCurrentVersion = 2;

        public int m_Version;

        /// <summary>Position of this crossing in the node's ordered list of crossing lanes.</summary>
        public int m_Index;

        /// <summary>Multiplier on the width the game lays this crossing at.</summary>
        public float m_Scale;

        /// <summary>
        /// Metres each end has been moved along the road: x the start end, y the end end. Positive
        /// is the direction the crossing's own start-to-end runs, rotated to face along the road.
        /// </summary>
        public float2 m_Shift;

        /// <summary>Where the game last laid this crossing's start, before the shift was applied.</summary>
        public float3 m_BaseStart;

        /// <summary>Where the game last laid this crossing's end, before the shift was applied.</summary>
        public float3 m_BaseEnd;

        /// <summary>
        /// The shift actually written into the curve, which is not always the shift asked for.
        ///
        /// Dragging changes <see cref="m_Shift"/> every frame while the button is held, and the
        /// curve only catches up on the next pass. Without a record of what was written, the pass
        /// cannot tell "the game has just re-laid this" from "the player has moved it since", and a
        /// drag turns into the crossing travelling the sum of every frame's cursor offset — several
        /// hundred metres for a one-second drag.
        /// </summary>
        public float2 m_AppliedShift;

        /// <summary>
        /// True once a base curve has been recorded.
        ///
        /// Tested on the horizontal components only: a crossing at the map origin is not a thing,
        /// but a crossing at height zero is perfectly ordinary.
        /// </summary>
        public bool HasBase =>
            m_BaseStart.x != 0f || m_BaseStart.z != 0f || m_BaseEnd.x != 0f || m_BaseEnd.z != 0f;

        /// <summary>True if this entry is doing nothing and could be dropped.</summary>
        public bool IsEmpty =>
            m_Scale <= 0f && math.abs(m_Shift.x) < 0.01f && math.abs(m_Shift.y) < 0.01f;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kCurrentVersion);
            writer.Write(m_Index);
            writer.Write(m_Scale);

            // Written one float at a time rather than as vectors, so the layout on disk is
            // completely explicit and a future field can be appended without any doubt about what
            // came before it.
            float shiftStart = m_Shift.x;
            float shiftEnd = m_Shift.y;

            writer.Write(shiftStart);
            writer.Write(shiftEnd);
            writer.Write(m_BaseStart.x);
            writer.Write(m_BaseStart.y);
            writer.Write(m_BaseStart.z);
            writer.Write(m_BaseEnd.x);
            writer.Write(m_BaseEnd.y);
            writer.Write(m_BaseEnd.z);

            float appliedStart = m_AppliedShift.x;
            float appliedEnd = m_AppliedShift.y;

            writer.Write(appliedStart);
            writer.Write(appliedEnd);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Version);
            reader.Read(out m_Index);
            reader.Read(out m_Scale);

            m_Shift = default(float2);
            m_BaseStart = default(float3);
            m_BaseEnd = default(float3);
            m_AppliedShift = default(float2);

            if (m_Version < 2)
            {
                return;
            }

            reader.Read(out float shiftStart);
            reader.Read(out float shiftEnd);
            reader.Read(out float baseStartX);
            reader.Read(out float baseStartY);
            reader.Read(out float baseStartZ);
            reader.Read(out float baseEndX);
            reader.Read(out float baseEndY);
            reader.Read(out float baseEndZ);
            reader.Read(out float appliedStart);
            reader.Read(out float appliedEnd);

            m_Shift = new float2(shiftStart, shiftEnd);
            m_BaseStart = new float3(baseStartX, baseStartY, baseStartZ);
            m_BaseEnd = new float3(baseEndX, baseEndY, baseEndZ);
            m_AppliedShift = new float2(appliedStart, appliedEnd);
        }
    }
}
