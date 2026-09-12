using Colossal.Serialization.Entities;
using Unity.Entities;

namespace CrosswalkWidth.Components
{
    /// <summary>
    /// Marks a crossing lane this mod laid rather than the game.
    ///
    /// Field-less, and it must stay that way. It shipped as <see cref="IEmptySerializable"/>, which
    /// writes nothing and reads nothing, and CS2's entity stream has no per-component framing — a
    /// component's bytes are found by everything before it having read exactly what it wrote. Giving
    /// this one a payload would make every save that already contains it read this component's bytes
    /// out of the next component along, and everything after that in the chunk. The junction is on
    /// <see cref="CrosswalkJunction"/> instead, which is a new type and so has no old format to
    /// disagree with.
    /// </summary>
    public struct CrosswalkAdded : IComponentData, IQueryTypeParameter, IEmptySerializable
    {
    }
}
