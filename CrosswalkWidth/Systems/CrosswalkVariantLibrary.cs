using System.Collections.Generic;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;

namespace CrosswalkWidth.Systems
{
    /// <summary>
    /// Copies of the crossing lane prefabs at other widths, so one junction can differ from the
    /// next.
    ///
    /// The global setting works by rewriting NetLaneData.m_Width on the game's own crossing lane
    /// prefabs, which every junction shares — that is the whole reason it applies everywhere at
    /// once. A per-junction width therefore cannot be a different value in the same component; it
    /// has to be a different prefab.
    ///
    /// So a junction with an override gets its crossing lanes pointed at a clone of the lane prefab
    /// that carries the width it wants. EntityManager.Instantiate copies the mesh, materials, lane
    /// archetypes and pathfind reference along with everything else, and only the width is changed
    /// afterwards.
    ///
    /// Clones are cached per (authored lane, width) and the width is quantised to 5cm, so a player
    /// dragging a junction back and forth reuses a handful of prefabs rather than creating one per
    /// frame.
    ///
    /// **Unverified.** A clone keeps the original's PrefabData, which indexes PrefabSystem's list of
    /// managed prefabs, so anything resolving a managed PrefabBase from a cloned lane gets the
    /// original back. Nothing in the render path was found doing that — meshes and materials are
    /// read from the entity's own components — but it was not exhaustively traced.
    /// </summary>
    internal sealed class CrosswalkVariantLibrary
    {
        /// <summary>Authored lane → quantised width in centimetres → clone.</summary>
        private readonly Dictionary<Entity, Dictionary<int, Entity>> m_Variants =
            new Dictionary<Entity, Dictionary<int, Entity>>();

        /// <summary>Clone → the authored lane it came from.</summary>
        private readonly Dictionary<Entity, Entity> m_Origin = new Dictionary<Entity, Entity>();

        public int VariantCount => m_Origin.Count;

        /// <summary>True if this lane prefab is one this library created.</summary>
        public bool IsVariant(Entity lane)
        {
            return m_Origin.ContainsKey(lane);
        }

        /// <summary>
        /// The authored lane behind a prefab, whether it is a clone or the original. Lets the
        /// override system re-derive the base after it has already swapped a lane once.
        /// </summary>
        public Entity ResolveOrigin(Entity lane)
        {
            return m_Origin.TryGetValue(lane, out Entity origin) ? origin : lane;
        }

        /// <summary>
        /// A clone of <paramref name="authoredLane"/> whose width is <paramref name="width"/>,
        /// creating it if this is the first time that width has been asked for.
        ///
        /// Returns Entity.Null if the lane cannot be cloned, so the caller leaves the junction on
        /// the shared prefab rather than pointing it at nothing.
        /// </summary>
        public Entity GetVariant(EntityManager em, Entity authoredLane, float width)
        {
            if (authoredLane == Entity.Null
                || !em.Exists(authoredLane)
                || !em.HasComponent<NetLaneData>(authoredLane))
            {
                return Entity.Null;
            }

            width = math.clamp(width, 0.1f, 40f);
            int key = (int)math.round(width * 20f);   // 5cm buckets

            if (!m_Variants.TryGetValue(authoredLane, out Dictionary<int, Entity> byWidth))
            {
                byWidth = new Dictionary<int, Entity>();
                m_Variants.Add(authoredLane, byWidth);
            }

            if (byWidth.TryGetValue(key, out Entity existing) && em.Exists(existing))
            {
                return existing;
            }

            Entity clone = em.Instantiate(authoredLane);

            NetLaneData laneData = em.GetComponentData<NetLaneData>(clone);
            laneData.m_Width = key / 20f;
            em.SetComponentData(clone, laneData);

            byWidth[key] = clone;
            m_Origin[clone] = authoredLane;

            return clone;
        }

        /// <summary>
        /// Destroys every clone. Called when the mod is unloaded, so a world left behind has no
        /// entities pointing at prefabs that no longer make sense.
        ///
        /// Lanes still referencing a clone are handled by the override system putting them back on
        /// the authored prefab first — this runs after that.
        /// </summary>
        public void DestroyAll(EntityManager em)
        {
            foreach (KeyValuePair<Entity, Entity> entry in m_Origin)
            {
                if (em.Exists(entry.Key))
                {
                    em.DestroyEntity(entry.Key);
                }
            }

            m_Origin.Clear();
            m_Variants.Clear();
        }
    }
}
