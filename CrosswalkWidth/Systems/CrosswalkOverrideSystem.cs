using System.Collections.Generic;
using CrosswalkWidth.Components;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CrosswalkWidth.Systems
{
    /// <summary>
    /// Makes each crossing as wide as it is meant to be — the paint on the ground as well as the
    /// room people walk in.
    ///
    /// The width of a laid crossing is not the lane prefab's width alone. It is
    ///
    ///     NetLaneData.m_Width  +  NodeLane.m_WidthOffset
    ///
    /// and both halves of the game read it that way. CreatureUtils.GetLaneOffset spreads walkers
    /// across `prefabLaneData.m_Width + lerp(nodeLane.m_WidthOffset.x, .y, t)`, and the renderer
    /// scales the zebra mesh by BatchDataHelpers.BuildCurveScale, which is
    ///
    ///     1 + nodeLane.m_WidthOffset / netLaneData.m_Width
    ///
    /// That second one is the whole reason an earlier version looked half-finished: it widened the
    /// lane prefab, so people crossed in a broad stream, but with the offset still zero the curve
    /// scale stayed at 1 and the painted band never moved. LaneSystem itself never widens a prefab
    /// — it writes the offset (`declared width - resolved width`) and leaves the prefab alone.
    ///
    /// So this system does the same thing the game does. Solving both equations for a target of
    /// `scale` times the authored width gives exactly one answer:
    ///
    ///     leave PrefabRef pointing at the authored lane, and set
    ///     m_WidthOffset = (scale - 1) * authored width
    ///
    /// which makes the walking width `scale * authored` and the curve scale `scale` at the same
    /// time. No cloned prefabs, no shared prefab widths rewritten, and nothing that can leak into
    /// a save.
    ///
    /// Three levels of width, resolved per crossing:
    ///
    ///   a CrosswalkLaneOverride entry for that crossing's index, if there is one;
    ///   otherwise the junction's CrosswalkOverride;
    ///   otherwise the global setting.
    ///
    /// Cost is kept to what changed. A full sweep of the city's crossings happens on load, on a
    /// settings change and when overrides are cleared; every other frame the system looks only at
    /// crossing lanes the game has just laid (which carry Updated) and at junctions the tool has
    /// just edited. Writing an absolute value rather than adjusting the current one means a lane
    /// visited twice lands in the same place, so nothing drifts.
    /// </summary>
    public partial class CrosswalkOverrideSystem : GameSystemBase
    {
        /// <summary>Width difference below which a rewrite is not worth the batch rebuild.</summary>
        private const float kEpsilon = 0.001f;

        /// <summary>One crossing at a junction, as the tool and this system both see it.</summary>
        public struct CrossingInfo
        {
            /// <summary>Position among the node's crossing lanes. The key an override is stored under.</summary>
            public int m_Index;

            public Entity m_Lane;

            /// <summary>The lane prefab this crossing is laid from.</summary>
            public Entity m_AuthoredPrefab;

            /// <summary>Middle of the crossing, for drawing a handle and for hit-testing.</summary>
            public float3 m_Midpoint;

            /// <summary>The crossing's own curve, so it can be drawn exactly where it lies.</summary>
            public Colossal.Mathematics.Bezier4x3 m_Curve;

            /// <summary>Width the lane prefab is authored at, before any scaling.</summary>
            public float m_AuthoredWidth;
        }

        /// <summary>Every crossing lane in the city.</summary>
        private EntityQuery m_AllCrossingQuery;

        /// <summary>Crossing lanes the game has laid or re-laid this frame.</summary>
        private EntityQuery m_FreshCrossingQuery;

        /// <summary>Junctions carrying a width of their own, for the "clear everything" path.</summary>
        private EntityQuery m_OverriddenNodeQuery;

        private readonly List<Entity> m_CrossingLanes = new List<Entity>();

        /// <summary>Junctions the tool has just edited; their crossings are revisited next update.</summary>
        private readonly HashSet<Entity> m_PendingNodes = new HashSet<Entity>();

        /// <summary>Lanes already on this update's work list, so none is queued twice.</summary>
        private readonly HashSet<Entity> m_Seen = new HashSet<Entity>();

        /// <summary>Crossing order per junction, rebuilt each update it is needed.</summary>
        private readonly Dictionary<Entity, List<Entity>> m_OrderCache =
            new Dictionary<Entity, List<Entity>>();

        private bool m_FullPassPending = true;
        private int m_LastReportedCount = -1;

        public int LastAppliedCount { get; private set; }

        protected override void OnCreate()
        {
            base.OnCreate();

            m_AllCrossingQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Net.PedestrianLane>(),
                    ComponentType.ReadWrite<NodeLane>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Owner>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });

            m_FreshCrossingQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Net.PedestrianLane>(),
                    ComponentType.ReadWrite<NodeLane>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<Updated>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });

            m_OverriddenNodeQuery = GetEntityQuery(new EntityQueryDesc
            {
                Any = new[]
                {
                    ComponentType.ReadOnly<CrosswalkOverride>(),
                    ComponentType.ReadOnly<CrosswalkLaneOverride>()
                },
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Net.Node>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });
        }

        /// <summary>
        /// Asks for every crossing in the city to be revisited on the next update.
        ///
        /// Used where the answer can have changed everywhere at once — a save finishing loading,
        /// the global percentage moving, overrides being cleared.
        /// </summary>
        public void RequestFullPass()
        {
            m_FullPassPending = true;
        }

        /// <summary>Asks for one junction's crossings to be revisited on the next update.</summary>
        public void RequestNode(Entity node)
        {
            if (node != Entity.Null)
            {
                m_PendingNodes.Add(node);
            }
        }

        /// <summary>
        /// The crossings of one junction, in the order their overrides are keyed by.
        ///
        /// Read-only: takes no structural changes, so a caller may hold the results across a frame
        /// but not across anything that re-lays the node.
        /// </summary>
        public int CollectCrossings(Entity node, List<CrossingInfo> into)
        {
            into.Clear();

            BuildOrder(node, m_CrossingLanes);

            for (int i = 0; i < m_CrossingLanes.Count; i++)
            {
                Entity lane = m_CrossingLanes[i];
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(lane).m_Prefab;

                Game.Net.Curve curve = EntityManager.HasComponent<Game.Net.Curve>(lane)
                    ? EntityManager.GetComponentData<Game.Net.Curve>(lane)
                    : default(Game.Net.Curve);

                float3 midpoint = default(float3);
                midpoint.x = (curve.m_Bezier.a.x + curve.m_Bezier.d.x) * 0.5f;
                midpoint.y = (curve.m_Bezier.a.y + curve.m_Bezier.d.y) * 0.5f;
                midpoint.z = (curve.m_Bezier.a.z + curve.m_Bezier.d.z) * 0.5f;

                into.Add(new CrossingInfo
                {
                    m_Index = i,
                    m_Lane = lane,
                    m_AuthoredPrefab = prefab,
                    m_Midpoint = midpoint,
                    m_Curve = curve.m_Bezier,
                    m_AuthoredWidth = AuthoredWidth(prefab)
                });
            }

            return into.Count;
        }

        protected override void OnUpdate()
        {
            m_OrderCache.Clear();

            bool full = m_FullPassPending;
            m_FullPassPending = false;

            NativeList<Entity> lanes = new NativeList<Entity>(64, Allocator.Temp);

            try
            {
                CollectWorkList(full, lanes);

                if (lanes.Length == 0)
                {
                    m_PendingNodes.Clear();
                    return;
                }

                NativeList<Entity> touched = new NativeList<Entity>(lanes.Length, Allocator.Temp);

                try
                {
                    for (int i = 0; i < lanes.Length; i++)
                    {
                        if (ApplyToLane(lanes[i]))
                        {
                            touched.Add(lanes[i]);
                        }
                    }

                    if (touched.Length != 0)
                    {
                        // BatchesUpdated and nothing else. The width is already in place; what is
                        // left is for the renderer to rebuild this lane's batch so the new curve
                        // scale reaches the shader. Tagging the lane Updated instead would hand it
                        // back to the net pipeline, which re-lays the node and undoes the write —
                        // that is exactly the loop an earlier version got stuck in, alternating
                        // between applied and reverted every other frame.
                        EntityManager.AddComponent(touched.AsArray(), ComponentType.ReadWrite<BatchesUpdated>());
                    }

                    LastAppliedCount = touched.Length;

                    if (full && touched.Length != m_LastReportedCount)
                    {
                        m_LastReportedCount = touched.Length;
                        Mod.Log.Info(
                            $"{Mod.ModName}: widened {touched.Length} of {lanes.Length} crossing lanes");
                    }
                }
                finally
                {
                    touched.Dispose();
                }
            }
            finally
            {
                lanes.Dispose();
                m_PendingNodes.Clear();
            }
        }

        /// <summary>
        /// The crossing lanes worth looking at this update: all of them on a full pass, otherwise
        /// only the ones the game has just laid plus the ones at junctions the tool has touched.
        /// </summary>
        private void CollectWorkList(bool full, NativeList<Entity> into)
        {
            if (full)
            {
                AppendCrossings(m_AllCrossingQuery, into);
                return;
            }

            if (!m_FreshCrossingQuery.IsEmptyIgnoreFilter)
            {
                AppendCrossings(m_FreshCrossingQuery, into);
            }

            if (m_PendingNodes.Count == 0)
            {
                return;
            }

            // A junction the tool has just edited may also have been re-laid this frame, so the two
            // sources overlap. Applying a lane twice would be harmless — the second write finds the
            // value already right and does nothing — but the seen set keeps the counts in the log
            // honest.
            m_Seen.Clear();

            for (int i = 0; i < into.Length; i++)
            {
                m_Seen.Add(into[i]);
            }

            foreach (Entity node in m_PendingNodes)
            {
                BuildOrder(node, m_CrossingLanes);

                for (int i = 0; i < m_CrossingLanes.Count; i++)
                {
                    if (m_Seen.Add(m_CrossingLanes[i]))
                    {
                        into.Add(m_CrossingLanes[i]);
                    }
                }
            }
        }

        private void AppendCrossings(EntityQuery query, NativeList<Entity> into)
        {
            NativeArray<Entity> candidates = query.ToEntityArray(Allocator.Temp);

            try
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (IsCrossing(candidates[i]))
                    {
                        into.Add(candidates[i]);
                    }
                }
            }
            finally
            {
                candidates.Dispose();
            }
        }

        /// <summary>
        /// Writes one crossing lane's width offset. True if it changed and the lane needs its
        /// render batch rebuilt.
        /// </summary>
        private bool ApplyToLane(Entity lane)
        {
            if (!EntityManager.Exists(lane)
                || !EntityManager.HasComponent<NodeLane>(lane)
                || !EntityManager.HasComponent<PrefabRef>(lane)
                || !EntityManager.HasComponent<Owner>(lane))
            {
                return false;
            }

            Entity prefab = EntityManager.GetComponentData<PrefabRef>(lane).m_Prefab;
            float authored = AuthoredWidth(prefab);

            if (authored <= 0.01f)
            {
                return false;
            }

            Entity node = EntityManager.GetComponentData<Owner>(lane).m_Owner;
            float scale = ScaleFor(node, lane);
            float offset = TargetWidth(authored, scale) - authored;

            NodeLane nodeLane = EntityManager.GetComponentData<NodeLane>(lane);

            if (math.abs(nodeLane.m_WidthOffset.x - offset) < kEpsilon
                && math.abs(nodeLane.m_WidthOffset.y - offset) < kEpsilon)
            {
                return false;
            }

            nodeLane.m_WidthOffset = new float2(offset, offset);

            // The flags say which ends carry an offset at all; the save only writes the ends whose
            // flag is set, so a value without its flag would be silently lost on the next load.
            nodeLane.m_Flags &= ~(NodeLaneFlags.StartWidthOffset | NodeLaneFlags.EndWidthOffset);

            if (math.abs(offset) > kEpsilon)
            {
                nodeLane.m_Flags |= NodeLaneFlags.StartWidthOffset | NodeLaneFlags.EndWidthOffset;
            }

            EntityManager.SetComponentData(lane, nodeLane);

            return true;
        }

        /// <summary>
        /// The width one crossing should end up at: its scale applied to the authored width, held
        /// inside the floor and ceiling from the settings if either is set.
        ///
        /// A crossing narrower than a person is worse than useless, so the result never goes below
        /// a stride's width whatever the settings say.
        /// </summary>
        private float TargetWidth(float authored, float scale)
        {
            float target = authored * scale;

            CrosswalkWidthSetting settings = Mod.Settings;

            if (settings != null && settings.Enabled)
            {
                if (settings.MinimumWidth > 0f)
                {
                    target = math.max(target, settings.MinimumWidth);
                }

                if (settings.MaximumWidth > 0f)
                {
                    target = math.min(target, settings.MaximumWidth);
                }
            }

            return math.max(0.5f, target);
        }

        /// <summary>True if this lane is a pedestrian crossing rather than an ordinary walkway.</summary>
        private bool IsCrossing(Entity lane)
        {
            return EntityManager.HasComponent<Game.Net.PedestrianLane>(lane)
                && (EntityManager.GetComponentData<Game.Net.PedestrianLane>(lane).m_Flags
                    & PedestrianLaneFlags.Crosswalk) != 0;
        }

        /// <summary>The width the lane prefab was authored at.</summary>
        private float AuthoredWidth(Entity prefab)
        {
            if (prefab == Entity.Null
                || !EntityManager.Exists(prefab)
                || !EntityManager.HasComponent<NetLaneData>(prefab))
            {
                return 0f;
            }

            return EntityManager.GetComponentData<NetLaneData>(prefab).m_Width;
        }

        /// <summary>
        /// The crossing lanes of one junction, in SubLane order — the order overrides are keyed by.
        ///
        /// Cached for the duration of one update, because a junction with a per-crossing override
        /// is walked once per crossing otherwise.
        /// </summary>
        private void BuildOrder(Entity node, List<Entity> into)
        {
            into.Clear();

            if (node == Entity.Null
                || !EntityManager.Exists(node)
                || !EntityManager.HasBuffer<Game.Net.SubLane>(node))
            {
                return;
            }

            if (m_OrderCache.TryGetValue(node, out List<Entity> cached))
            {
                into.AddRange(cached);
                return;
            }

            DynamicBuffer<Game.Net.SubLane> subLanes = EntityManager.GetBuffer<Game.Net.SubLane>(node, true);

            for (int i = 0; i < subLanes.Length; i++)
            {
                Entity lane = subLanes[i].m_SubLane;

                if (lane == Entity.Null
                    || !EntityManager.Exists(lane)
                    || !EntityManager.HasComponent<PrefabRef>(lane)
                    || !EntityManager.HasComponent<NodeLane>(lane)
                    || !IsCrossing(lane))
                {
                    continue;
                }

                into.Add(lane);
            }

            m_OrderCache.Add(node, new List<Entity>(into));
        }

        /// <summary>
        /// The scale in force for one crossing: its own override, else the junction's, else the
        /// global setting.
        /// </summary>
        private float ScaleFor(Entity node, Entity lane)
        {
            if (node != Entity.Null
                && EntityManager.Exists(node)
                && EntityManager.HasBuffer<CrosswalkLaneOverride>(node))
            {
                int index = IndexOf(node, lane);

                if (index >= 0)
                {
                    DynamicBuffer<CrosswalkLaneOverride> overrides =
                        EntityManager.GetBuffer<CrosswalkLaneOverride>(node, true);

                    for (int i = 0; i < overrides.Length; i++)
                    {
                        if (overrides[i].m_Index == index && overrides[i].m_Scale > 0f)
                        {
                            return overrides[i].m_Scale;
                        }
                    }
                }
            }

            return GetScale(node, -1);
        }

        private float GlobalScale()
        {
            CrosswalkWidthSetting settings = Mod.Settings;

            if (settings == null || !settings.Enabled)
            {
                return 1f;
            }

            return settings.WidthPercentage / 100f;
        }

        private int IndexOf(Entity node, Entity lane)
        {
            BuildOrder(node, m_CrossingLanes);

            return m_CrossingLanes.IndexOf(lane);
        }

        /// <summary>
        /// The scale in force for one crossing by index: its own override, else the junction's,
        /// else the global setting. An index below zero skips straight to the junction.
        /// </summary>
        public float GetScale(Entity node, int index)
        {
            if (node != Entity.Null && EntityManager.Exists(node))
            {
                if (index >= 0 && EntityManager.HasBuffer<CrosswalkLaneOverride>(node))
                {
                    DynamicBuffer<CrosswalkLaneOverride> overrides =
                        EntityManager.GetBuffer<CrosswalkLaneOverride>(node, true);

                    for (int i = 0; i < overrides.Length; i++)
                    {
                        if (overrides[i].m_Index == index && overrides[i].m_Scale > 0f)
                        {
                            return overrides[i].m_Scale;
                        }
                    }
                }

                if (EntityManager.HasComponent<CrosswalkOverride>(node))
                {
                    float nodeScale = EntityManager.GetComponentData<CrosswalkOverride>(node).m_Scale;

                    if (nodeScale > 0f)
                    {
                        return nodeScale;
                    }
                }
            }

            return GlobalScale();
        }

        /// <summary>
        /// Sets, or clears, one crossing's width. An index below zero sets the whole junction.
        ///
        /// A scale of zero clears, which is what "back to global" does — the crossing follows the
        /// setting again rather than being pinned at the same number by coincidence.
        ///
        /// The junction is queued rather than tagged Updated: handing it to the net pipeline would
        /// re-lay every lane at it, which is both far more work than a width change needs and the
        /// thing that used to undo the write on the following frame.
        /// </summary>
        public void SetOverride(Entity node, int index, float scale)
        {
            if (node == Entity.Null || !EntityManager.Exists(node))
            {
                return;
            }

            if (index < 0)
            {
                SetNodeOverride(node, scale);
                return;
            }

            if (!EntityManager.HasBuffer<CrosswalkLaneOverride>(node))
            {
                if (scale <= 0f)
                {
                    return;
                }

                EntityManager.AddBuffer<CrosswalkLaneOverride>(node);
            }

            DynamicBuffer<CrosswalkLaneOverride> overrides =
                EntityManager.GetBuffer<CrosswalkLaneOverride>(node);

            for (int i = 0; i < overrides.Length; i++)
            {
                if (overrides[i].m_Index != index)
                {
                    continue;
                }

                if (scale <= 0f)
                {
                    overrides.RemoveAt(i);
                }
                else
                {
                    overrides[i] = new CrosswalkLaneOverride
                    {
                        m_Version = CrosswalkLaneOverride.kCurrentVersion,
                        m_Index = index,
                        m_Scale = math.clamp(scale, 0.25f, 8f)
                    };
                }

                RequestNode(node);
                return;
            }

            if (scale > 0f)
            {
                overrides.Add(new CrosswalkLaneOverride
                {
                    m_Version = CrosswalkLaneOverride.kCurrentVersion,
                    m_Index = index,
                    m_Scale = math.clamp(scale, 0.25f, 8f)
                });

                RequestNode(node);
            }
        }

        private void SetNodeOverride(Entity node, float scale)
        {
            if (scale <= 0f)
            {
                if (EntityManager.HasComponent<CrosswalkOverride>(node))
                {
                    EntityManager.RemoveComponent<CrosswalkOverride>(node);
                }

                if (EntityManager.HasBuffer<CrosswalkLaneOverride>(node))
                {
                    EntityManager.RemoveComponent<CrosswalkLaneOverride>(node);
                }

                RequestNode(node);
                return;
            }

            CrosswalkOverride value = new CrosswalkOverride
            {
                m_Version = CrosswalkOverride.kCurrentVersion,
                m_Scale = math.clamp(scale, 0.25f, 8f)
            };

            if (EntityManager.HasComponent<CrosswalkOverride>(node))
            {
                EntityManager.SetComponentData(node, value);
            }
            else
            {
                EntityManager.AddComponentData(node, value);
            }

            RequestNode(node);
        }

        /// <summary>
        /// Puts every crossing back to the width the game laid it at.
        ///
        /// Called at shutdown: NodeLane is written into the save, so a city saved after the mod is
        /// gone must not carry offsets nothing will maintain.
        /// </summary>
        public void RestoreAuthoredWidths()
        {
            NativeArray<Entity> lanes = m_AllCrossingQuery.ToEntityArray(Allocator.Temp);
            NativeList<Entity> touched = new NativeList<Entity>(16, Allocator.Temp);

            try
            {
                for (int i = 0; i < lanes.Length; i++)
                {
                    Entity lane = lanes[i];

                    if (!IsCrossing(lane))
                    {
                        continue;
                    }

                    NodeLane nodeLane = EntityManager.GetComponentData<NodeLane>(lane);

                    if (math.abs(nodeLane.m_WidthOffset.x) < kEpsilon
                        && math.abs(nodeLane.m_WidthOffset.y) < kEpsilon)
                    {
                        continue;
                    }

                    nodeLane.m_WidthOffset = default(float2);
                    nodeLane.m_Flags &= ~(NodeLaneFlags.StartWidthOffset | NodeLaneFlags.EndWidthOffset);

                    EntityManager.SetComponentData(lane, nodeLane);
                    touched.Add(lane);
                }

                if (touched.Length != 0)
                {
                    EntityManager.AddComponent(touched.AsArray(), ComponentType.ReadWrite<BatchesUpdated>());
                }
            }
            finally
            {
                touched.Dispose();
                lanes.Dispose();
            }
        }

        /// <summary>Removes every per-junction and per-crossing width in the city.</summary>
        public int ClearAllOverrides()
        {
            if (m_OverriddenNodeQuery.IsEmptyIgnoreFilter)
            {
                return 0;
            }

            int count = m_OverriddenNodeQuery.CalculateEntityCount();

            EntityManager.RemoveComponent(m_OverriddenNodeQuery, ComponentType.ReadWrite<CrosswalkOverride>());
            EntityManager.RemoveComponent(m_OverriddenNodeQuery, ComponentType.ReadWrite<CrosswalkLaneOverride>());

            RequestFullPass();

            return count;
        }
    }
}
