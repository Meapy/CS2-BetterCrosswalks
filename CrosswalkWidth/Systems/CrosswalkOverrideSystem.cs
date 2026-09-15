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
    /// `scale` times the width the game lays gives exactly one answer:
    ///
    ///     leave PrefabRef pointing at the lane the game chose, and set
    ///     m_WidthOffset = scale * laidWidth - variantWidth
    ///
    /// where `variantWidth` is the prefab on the lane and `laidWidth` is the width of the lane the
    /// road *declared* — the two differ on a themed road, and their difference is precisely the
    /// offset LaneSystem writes. At a scale of 1 the formula reproduces the game's own value
    /// exactly, so turning the mod off and removing it are real restores rather than
    /// approximations. No cloned prefabs, no shared prefab widths rewritten, nothing that can leak
    /// into a save.
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

        /// <summary>Furthest a crossing's end may be dragged along the road, in metres.</summary>
        private const float kMaxShift = 12f;

        /// <summary>
        /// Passes between one re-lay of a junction's side lines and the next.
        ///
        /// Counted in passes rather than frames, because this system runs twice in a frame — once in
        /// Modification4 and once in Modification4B, ahead of the system that lays the lines. So
        /// this is about a fifth of a second, which is slow enough that dragging a crossing wider
        /// does not rebuild the junction on every frame and fast enough that the lines visibly
        /// follow while it is happening.
        /// </summary>
        private const int kRelayCooldown = 24;

        /// <summary>
        /// Where the numbering of this mod's own crossings starts, above the junction's real ones.
        ///
        /// A width or a position set by hand is stored against a number. The junction's own
        /// crossings are numbered by their place in its lane list; the ones this mod lays are not in
        /// that list at all, and are numbered by which diagonal they are instead. Starting well
        /// above any real junction's crossing count keeps the two sets of numbers from ever meaning
        /// each other.
        /// </summary>
        public const int kAddedIndexBase = 1000;

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

            /// <summary>True for a middle crossing this mod laid, rather than one of the game's.</summary>
            public bool m_IsAdded;
        }

        /// <summary>Every crossing lane in the city.</summary>
        private EntityQuery m_AllCrossingQuery;

        /// <summary>Crossing lanes the game has laid or re-laid this frame.</summary>
        private EntityQuery m_FreshCrossingQuery;

        /// <summary>Junctions carrying a width of their own, for the "clear everything" path.</summary>
        private EntityQuery m_OverriddenNodeQuery;

        /// <summary>Crossing lanes this mod laid, which no junction's sub-lane order contains.</summary>
        private EntityQuery m_AddedCrossingQuery;

        private readonly List<Entity> m_CrossingLanes = new List<Entity>();

        /// <summary>Junctions the tool has just edited; their crossings are revisited next update.</summary>
        private readonly HashSet<Entity> m_PendingNodes = new HashSet<Entity>();

        /// <summary>Individual lanes asked for by name, for the ones the crossing order leaves out.</summary>
        private readonly HashSet<Entity> m_PendingLanes = new HashSet<Entity>();

        /// <summary>Lanes already on this update's work list, so none is queued twice.</summary>
        private readonly HashSet<Entity> m_Seen = new HashSet<Entity>();

        /// <summary>Lane prefabs the catalogue does not know, already named in the log once each.</summary>
        private readonly HashSet<Entity> m_ReportedUnknown = new HashSet<Entity>();

        /// <summary>Crossing order per junction, rebuilt each update it is needed.</summary>
        private readonly Dictionary<Entity, List<Entity>> m_OrderCache =
            new Dictionary<Entity, List<Entity>>();

        /// <summary>
        /// Junctions whose side lines no longer match their crossings, waiting to be handed back to
        /// the game so it lays them again. Always empty while the lines are switched off.
        /// </summary>
        private readonly HashSet<Entity> m_StaleLines = new HashSet<Entity>();

        /// <summary>When each junction was last handed back for its lines, in passes.</summary>
        private readonly Dictionary<Entity, int> m_LastRelaid = new Dictionary<Entity, int>();

        /// <summary>Scratch: junctions dealt with this pass, taken off the list after the walk.</summary>
        private readonly List<Entity> m_Relaid = new List<Entity>();

        private bool m_FullPassPending = true;
        private int m_LastReportedCount = -1;

        /// <summary>Passes since the system started, for the re-lay cooldown.</summary>
        private int m_Pass;

        /// <summary>
        /// Set if this system has thrown. It then does nothing for the rest of the session.
        ///
        /// A mod that throws every frame in a Modification phase is far worse than a mod that stops
        /// working: the city is mid-update when the exception unwinds. One failure is a bug to fix
        /// from the log, not something to keep repeating over a player's save.
        /// </summary>
        private bool m_Failed;

        private PrefabSystem m_PrefabSystem;

        public int LastAppliedCount { get; private set; }


        protected override void OnCreate()
        {
            base.OnCreate();

            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            m_AllCrossingQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Net.PedestrianLane>(),
                    ComponentType.ReadWrite<NodeLane>(),
                    ComponentType.ReadOnly<PrefabRef>()
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
                    ComponentType.ReadOnly<Updated>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });

            m_AddedCrossingQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<CrosswalkAdded>(),
                    ComponentType.ReadOnly<CrosswalkJunction>(),
                    ComponentType.ReadWrite<NodeLane>()
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
        /// What the crossings standing in this city actually are, grouped by lane prefab, as lines
        /// for the game log.
        ///
        /// Here because the question "why is there a line across this road with nothing between it"
        /// has exactly one answer per prefab, and no way to get at it from a screenshot. A crossing
        /// lane is laid at every junction whether or not anybody asked for a painted crossing; what
        /// separates a crossing you can see from one you cannot is <c>PedestrianLaneFlags.Unsafe</c>
        /// and whether the prefab has a mesh at all. Both are in here, against the prefab's name and
        /// against whether this mod is bordering it.
        /// </summary>
        public IEnumerable<string> DescribeLaidCrossings()
        {
            if (m_AllCrossingQuery.IsEmptyIgnoreFilter)
            {
                yield return "no crossings laid — no city loaded, most likely";
                yield break;
            }

            Dictionary<Entity, int3> tally = new Dictionary<Entity, int3>();

            NativeArray<Entity> lanes = m_AllCrossingQuery.ToEntityArray(Allocator.Temp);

            try
            {
                for (int i = 0; i < lanes.Length; i++)
                {
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(lanes[i]).m_Prefab;
                    PedestrianLaneFlags flags =
                        EntityManager.GetComponentData<Game.Net.PedestrianLane>(lanes[i]).m_Flags;

                    tally.TryGetValue(prefab, out int3 counts);

                    counts.x++;
                    counts.y += (flags & PedestrianLaneFlags.Crosswalk) != 0 ? 1 : 0;
                    counts.z += (flags & PedestrianLaneFlags.Unsafe) != 0 ? 1 : 0;

                    tally[prefab] = counts;
                }
            }
            finally
            {
                lanes.Dispose();
            }

            yield return $"{tally.Count} lane prefabs behind the pedestrian lanes standing in this city";

            foreach (KeyValuePair<Entity, int3> entry in tally)
            {
                bool hasMesh = EntityManager.HasBuffer<SubMesh>(entry.Key)
                    && EntityManager.GetBuffer<SubMesh>(entry.Key, true).Length != 0;

                bool bordered = EntityManager.HasBuffer<SecondaryNetLane>(entry.Key)
                    && EntityManager.GetBuffer<SecondaryNetLane>(entry.Key, true).Length != 0;

                string name = m_PrefabSystem.TryGetPrefab<PrefabBase>(entry.Key, out PrefabBase managed)
                    && managed != null
                        ? managed.name
                        : entry.Key.ToString();

                yield return $"  {name}: {entry.Value.x} laid, {entry.Value.y} marked Crosswalk, "
                    + $"{entry.Value.z} marked Unsafe, {(hasMesh ? "has paint" : "NO PAINT")}"
                    + (bordered ? ", side lines written" : string.Empty);
            }
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
                NoteLinesStale(node);
            }
        }

        /// <summary>
        /// Notes that this junction's side lines no longer match its crossings.
        ///
        /// A crossing's width lives on the crossing, but the lines beside it are laid from that
        /// width by <c>SecondaryLaneSystem</c> — once, when the junction is laid, and never again
        /// until it is laid once more. Widening a crossing afterwards therefore moves the band and
        /// leaves the lines where they were.
        ///
        /// So the junction has to be handed back to the game. That is normally a thing this mod must
        /// never do (see NOTES.md, "never tag the node Updated"), and it is safe here for one
        /// reason: this is reached only from the calls the *player* makes — the tool's setters and
        /// its buttons — never from anything a re-lay causes. A re-lay does not put a junction back
        /// on this list, so there is no cycle to get stuck in.
        ///
        /// Nothing is done when the lines are switched off, which is the default: then a crossing's
        /// width reaches the paint on its own and re-laying the junction would be pure cost.
        /// </summary>
        private void NoteLinesStale(Entity node)
        {
            CrosswalkWidthSetting settings = Mod.Settings;

            if (node == Entity.Null || settings == null || !settings.Enabled || !settings.EdgeLines)
            {
                return;
            }

            m_StaleLines.Add(node);
        }

        /// <summary>
        /// Hands back the junctions whose side lines are out of date, no more often than the
        /// cooldown allows.
        ///
        /// The cooldown is what makes this usable while a crossing is being dragged. The tool calls
        /// its setter on every frame the button is held, so without one a junction would be torn
        /// down and rebuilt sixty times a second — every lane at it destroyed and re-created, and
        /// the pedestrian graph churned with it. A junction that is still cooling is left on the
        /// list rather than dropped, so the width the drag finishes on is always the one the lines
        /// are finally laid at.
        /// </summary>
        private void RelayStaleLines()
        {
            if (m_StaleLines.Count == 0)
            {
                return;
            }

            m_Relaid.Clear();

            foreach (Entity node in m_StaleLines)
            {
                if (node == Entity.Null
                    || !EntityManager.Exists(node)
                    || !EntityManager.HasComponent<Game.Net.Node>(node)
                    || EntityManager.HasComponent<Deleted>(node)
                    || EntityManager.HasComponent<Temp>(node))
                {
                    m_Relaid.Add(node);
                    continue;
                }

                if (m_LastRelaid.TryGetValue(node, out int last) && m_Pass - last < kRelayCooldown)
                {
                    continue;
                }

                m_LastRelaid[node] = m_Pass;
                m_Relaid.Add(node);

                if (!EntityManager.HasComponent<Updated>(node))
                {
                    EntityManager.AddComponent<Updated>(node);
                }
            }

            for (int i = 0; i < m_Relaid.Count; i++)
            {
                m_StaleLines.Remove(m_Relaid[i]);
            }

            // Junctions come and go; nothing reads a stale entry, but the map would grow for the
            // rest of the session without this.
            if (m_LastRelaid.Count > 1024)
            {
                m_LastRelaid.Clear();
            }
        }

        /// <summary>
        /// Asks for one crossing lane to be revisited on the next update.
        ///
        /// For the lanes this mod lays itself. Asking for their junction would not reach them: a
        /// junction request is resolved through the crossing order, and those lanes are kept out of
        /// it so they cannot shift the numbering the per-crossing widths are keyed by.
        /// </summary>
        public void RequestLane(Entity lane)
        {
            if (lane != Entity.Null)
            {
                m_PendingLanes.Add(lane);
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
                    m_AuthoredWidth = Mod.Catalog != null ? Mod.Catalog.LaidWidth(prefab) : 0f
                });
            }

            AppendAddedCrossings(node, into);

            return into.Count;
        }

        /// <summary>
        /// Adds this junction's middle crossings to the list, after its own.
        ///
        /// They are not in the junction's lane list — that is the whole reason they are safe — so
        /// they have to be gathered by the junction they name, and numbered by which diagonal they
        /// are rather than by position.
        /// </summary>
        private void AppendAddedCrossings(Entity node, List<CrossingInfo> into)
        {
            if (node == Entity.Null || m_AddedCrossingQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            NativeArray<Entity> lanes = m_AddedCrossingQuery.ToEntityArray(Allocator.Temp);
            int appendedFrom = into.Count;

            try
            {
                for (int i = 0; i < lanes.Length; i++)
                {
                    Entity lane = lanes[i];
                    CrosswalkJunction belongs =
                        EntityManager.GetComponentData<CrosswalkJunction>(lane);

                    if (belongs.m_Junction != node
                        || !EntityManager.HasComponent<PrefabRef>(lane)
                        || !EntityManager.HasComponent<Game.Net.Curve>(lane))
                    {
                        continue;
                    }

                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(lane).m_Prefab;
                    Game.Net.Curve curve = EntityManager.GetComponentData<Game.Net.Curve>(lane);

                    float3 midpoint = default(float3);
                    midpoint.x = (curve.m_Bezier.a.x + curve.m_Bezier.d.x) * 0.5f;
                    midpoint.y = (curve.m_Bezier.a.y + curve.m_Bezier.d.y) * 0.5f;
                    midpoint.z = (curve.m_Bezier.a.z + curve.m_Bezier.d.z) * 0.5f;

                    into.Add(new CrossingInfo
                    {
                        m_Index = kAddedIndexBase + belongs.m_Ordinal,
                        m_Lane = lane,
                        m_AuthoredPrefab = prefab,
                        m_Midpoint = midpoint,
                        m_Curve = curve.m_Bezier,
                        m_AuthoredWidth = Mod.Catalog != null ? Mod.Catalog.LaidWidth(prefab) : 0f,
                        m_IsAdded = true
                    });
                }
            }
            finally
            {
                lanes.Dispose();
            }

            // A chunk query hands them back in whatever order the chunks happen to sit in, which
            // changes as lanes are created and destroyed. The tool numbers the list by position, so
            // an unstable order would move a crossing out from under a selection. Sort by ordinal.
            for (int i = appendedFrom + 1; i < into.Count; i++)
            {
                CrossingInfo moving = into[i];
                int j = i - 1;

                while (j >= appendedFrom && into[j].m_Index > moving.m_Index)
                {
                    into[j + 1] = into[j];
                    j--;
                }

                into[j + 1] = moving;
            }
        }

        protected override void OnUpdate()
        {
            // Cleared before the failure check, not inside Apply: the tool reads this cache every
            // frame, and a cache that stopped being refreshed would hand it lane entities that
            // LaneSystem has since destroyed.
            m_OrderCache.Clear();

            if (m_Failed)
            {
                return;
            }

            m_Pass++;

            try
            {
                Apply();
                RelayStaleLines();
            }
            catch (System.Exception e)
            {
                m_Failed = true;
                Mod.Log.Error(
                    $"{Mod.ModName}: stopping — the width pass threw and will not be run again this "
                    + $"session, so nothing further is written to the city. {e}");
            }
        }

        private void Apply()
        {

            bool full = m_FullPassPending;
            m_FullPassPending = false;

            NativeList<Entity> lanes = new NativeList<Entity>(64, Allocator.Temp);

            try
            {
                CollectWorkList(full, lanes);

                if (lanes.Length == 0)
                {
                    m_PendingNodes.Clear();
                    m_PendingLanes.Clear();
                    return;
                }

                NativeList<Entity> touched = new NativeList<Entity>(lanes.Length, Allocator.Temp);

                try
                {
                    for (int i = 0; i < lanes.Length; i++)
                    {
                        // Per lane, so a single awkward one cannot leave the rest of the city
                        // written but not redrawn. The batch tag below is added once, after the
                        // loop, and an exception escaping here would skip it for every lane
                        // already changed.
                        try
                        {
                            if (ApplyToLane(lanes[i]))
                            {
                                touched.Add(lanes[i]);
                            }
                        }
                        catch (System.Exception e)
                        {
                            if (m_ReportedUnknown.Add(lanes[i]))
                            {
                                Mod.Log.Warn(
                                    $"{Mod.ModName}: skipped crossing lane {lanes[i].Index}: {e.Message}");
                            }
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
                m_PendingLanes.Clear();
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

            if (m_PendingNodes.Count == 0 && m_PendingLanes.Count == 0)
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

            // A junction's own crossings are found through its sub-lane order, and the ones this
            // mod laid are not in it — so widening a junction by hand would reach every crossing
            // there except the diagonals. They are collected by their junction instead.
            if (m_PendingNodes.Count != 0 && !m_AddedCrossingQuery.IsEmptyIgnoreFilter)
            {
                NativeArray<Entity> added = m_AddedCrossingQuery.ToEntityArray(Allocator.Temp);

                try
                {
                    for (int i = 0; i < added.Length; i++)
                    {
                        if (m_PendingNodes.Contains(
                                EntityManager.GetComponentData<CrosswalkJunction>(added[i]).m_Junction)
                            && m_Seen.Add(added[i]))
                        {
                            into.Add(added[i]);
                        }
                    }
                }
                finally
                {
                    added.Dispose();
                }
            }

            foreach (Entity lane in m_PendingLanes)
            {
                if (EntityManager.Exists(lane)
                    && !EntityManager.HasComponent<Deleted>(lane)
                    && IsCrossing(lane)
                    && m_Seen.Add(lane))
                {
                    into.Add(lane);
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
                || !EntityManager.HasComponent<PrefabRef>(lane))
            {
                return false;
            }

            // The junction is normally the lane's owner, but a crossing this mod laid has no owner
            // on purpose — it names its junction on its own marker instead. See CrosswalkAdded.
            Entity node;

            if (EntityManager.HasComponent<Owner>(lane))
            {
                node = EntityManager.GetComponentData<Owner>(lane).m_Owner;
            }
            else if (EntityManager.HasComponent<CrosswalkJunction>(lane))
            {
                node = EntityManager.GetComponentData<CrosswalkJunction>(lane).m_Junction;
            }
            else
            {
                return false;
            }

            Entity prefab = EntityManager.GetComponentData<PrefabRef>(lane).m_Prefab;

            if (!TryGetWidths(prefab, out float laidWidth, out float variantWidth))
            {
                return false;
            }
            float scale = ScaleFor(node, lane);

            bool widened = WriteOffset(lane, TargetWidth(laidWidth, scale) - variantWidth);
            bool moved = ApplyShift(lane, node);

            return widened || moved;
        }

        /// <summary>
        /// Moves one crossing's ends along the road, if the player has moved them.
        ///
        /// The whole difficulty here is that LaneSystem owns this curve. It rewrites it from
        /// scratch every time it re-lays the junction, and this system writes over that afterwards,
        /// so "move it 2 metres" applied twice would move it four. The override therefore carries
        /// the curve the game laid as well as the shift, and every write is absolute:
        ///
        ///   sitting on the recorded base  -> the game has just re-laid it; apply the shift
        ///   sitting on base plus shift    -> already done; leave it alone
        ///   sitting on neither            -> the junction has changed shape; take what is there
        ///                                    as the new base and shift from that
        ///
        /// which means it settles wherever it is put and never walks.
        /// </summary>
        private bool ApplyShift(Entity lane, Entity node)
        {
            if (node == Entity.Null
                || !EntityManager.Exists(node)
                || !EntityManager.HasBuffer<CrosswalkLaneOverride>(node)
                || !EntityManager.HasComponent<Game.Net.Curve>(lane))
            {
                return false;
            }

            int index = IndexOf(node, lane);

            if (index < 0)
            {
                return false;
            }

            DynamicBuffer<CrosswalkLaneOverride> overrides =
                EntityManager.GetBuffer<CrosswalkLaneOverride>(node);

            int slot = -1;

            for (int i = 0; i < overrides.Length; i++)
            {
                if (overrides[i].m_Index == index)
                {
                    slot = i;
                    break;
                }
            }

            if (slot < 0)
            {
                return false;
            }

            CrosswalkLaneOverride entry = overrides[slot];

            CrosswalkWidthSetting settings = Mod.Settings;
            float2 shift = settings != null && settings.Enabled ? entry.m_Shift : default(float2);

            if (!entry.HasBase && math.abs(shift.x) < kEpsilon && math.abs(shift.y) < kEpsilon)
            {
                return false;   // nothing has ever been moved here, so nothing to hold in place
            }

            Game.Net.Curve curve = EntityManager.GetComponentData<Game.Net.Curve>(lane);
            float3 current0 = curve.m_Bezier.a;
            float3 current1 = curve.m_Bezier.d;

            float3 baseStart = entry.m_BaseStart;
            float3 baseEnd = entry.m_BaseEnd;
            bool haveBase = entry.HasBase;

            if (haveBase)
            {
                // Against the shift that was *written*, not the one being asked for. During a drag
                // the two differ every frame, and testing against the new one would read "this is
                // not where I left it" and re-base — turning each frame's cursor offset into
                // another displacement on top of the last.
                Shifted(baseStart, baseEnd, entry.m_AppliedShift, out float3 wasStart, out float3 wasEnd);

                bool atBase = Same(current0, baseStart) && Same(current1, baseEnd);
                bool atApplied = Same(current0, wasStart) && Same(current1, wasEnd);

                haveBase = atBase || atApplied;
            }

            if (!haveBase)
            {
                baseStart = current0;
                baseEnd = current1;

                entry.m_BaseStart = baseStart;
                entry.m_BaseEnd = baseEnd;
                entry.m_AppliedShift = default(float2);
                overrides[slot] = entry;
            }

            Shifted(baseStart, baseEnd, shift, out float3 targetStart, out float3 targetEnd);

            if (Same(current0, targetStart) && Same(current1, targetEnd))
            {
                if (math.abs(entry.m_AppliedShift.x - shift.x) > kEpsilon
                    || math.abs(entry.m_AppliedShift.y - shift.y) > kEpsilon)
                {
                    entry.m_AppliedShift = shift;
                    overrides[slot] = entry;
                }

                return false;
            }

            entry.m_AppliedShift = shift;
            overrides[slot] = entry;

            float3 step = (targetEnd - targetStart) * (1f / 3f);

            curve.m_Bezier = new Colossal.Mathematics.Bezier4x3(
                targetStart,
                targetStart + step,
                targetStart + step + step,
                targetEnd);

            curve.m_Length = math.distance(targetStart, targetEnd);

            EntityManager.SetComponentData(lane, curve);

            return true;
        }

        /// <summary>
        /// Where a crossing's ends land once each has been moved along the road by its own amount.
        ///
        /// The road direction is not looked up anywhere — a crossing runs across the road by
        /// definition, so turning its own start-to-end a quarter turn in the ground plane gives the
        /// direction traffic travels, which is the one direction it makes sense to move a crossing
        /// in. Height is carried across unchanged; a crossing that follows a slope keeps following
        /// it.
        /// </summary>
        private static void Shifted(float3 start, float3 end, float2 shift, out float3 movedStart, out float3 movedEnd)
        {
            float dx = end.x - start.x;
            float dz = end.z - start.z;
            float length = math.sqrt(dx * dx + dz * dz);

            if (length < 0.01f)
            {
                movedStart = start;
                movedEnd = end;
                return;
            }

            // A quarter turn of the crossing's own direction: (dx, dz) -> (dz, -dx).
            float alongX = dz / length;
            float alongZ = -dx / length;

            movedStart = start;
            movedStart.x += alongX * shift.x;
            movedStart.z += alongZ * shift.x;

            movedEnd = end;
            movedEnd.x += alongX * shift.y;
            movedEnd.z += alongZ * shift.y;
        }

        /// <summary>
        /// Puts one crossing's curve back where the game laid it, if this mod moved it.
        ///
        /// Used by the restore and purge paths rather than by the per-frame pass, which gets the
        /// same result by applying a shift of zero.
        /// </summary>
        private bool RestoreShift(Entity lane, Entity node)
        {
            if (node == Entity.Null
                || !EntityManager.Exists(node)
                || !EntityManager.HasBuffer<CrosswalkLaneOverride>(node)
                || !EntityManager.HasComponent<Game.Net.Curve>(lane))
            {
                return false;
            }

            int index = IndexOf(node, lane);

            if (index < 0)
            {
                return false;
            }

            DynamicBuffer<CrosswalkLaneOverride> overrides =
                EntityManager.GetBuffer<CrosswalkLaneOverride>(node, true);

            for (int i = 0; i < overrides.Length; i++)
            {
                CrosswalkLaneOverride entry = overrides[i];

                if (entry.m_Index != index || !entry.HasBase)
                {
                    continue;
                }

                Game.Net.Curve curve = EntityManager.GetComponentData<Game.Net.Curve>(lane);

                if (Same(curve.m_Bezier.a, entry.m_BaseStart) && Same(curve.m_Bezier.d, entry.m_BaseEnd))
                {
                    return false;
                }

                float3 step = (entry.m_BaseEnd - entry.m_BaseStart) * (1f / 3f);

                curve.m_Bezier = new Colossal.Mathematics.Bezier4x3(
                    entry.m_BaseStart,
                    entry.m_BaseStart + step,
                    entry.m_BaseStart + step + step,
                    entry.m_BaseEnd);

                curve.m_Length = math.distance(entry.m_BaseStart, entry.m_BaseEnd);

                EntityManager.SetComponentData(lane, curve);

                return true;
            }

            return false;
        }

        /// <summary>
        /// Two positions the same place, to within a centimetre across and five below.
        ///
        /// Height is part of the test on purpose. Raise the terrain under a junction and the game
        /// re-lays its crossings at the same plan position and a new height; ignoring height would
        /// call that "unchanged", keep the old base, and pin the crossing at the height the ground
        /// used to be. The looser tolerance keeps ordinary slope noise from re-basing constantly.
        /// </summary>
        private static bool Same(float3 a, float3 b)
        {
            return math.abs(a.x - b.x) < 0.01f
                && math.abs(a.z - b.z) < 0.01f
                && math.abs(a.y - b.y) < 0.05f;
        }

        /// <summary>
        /// The two widths a crossing's size is worked out from, or false if this lane is not one
        /// the catalogue recognises.
        ///
        /// <paramref name="laidWidth"/> is the width the game lays this crossing at with no mod
        /// present — the *declaring* lane's width, not the variant's. <paramref name="variantWidth"/>
        /// is the width of the prefab actually on the lane. The difference between them is the
        /// offset LaneSystem writes itself, and it is the value this mod must land on when its
        /// scale is 1.
        ///
        /// Refusing unknown lanes is deliberate. Writing the offset means overwriting whatever the
        /// game put there, and the only safe way to overwrite it is to be able to reproduce it. A
        /// crossing from a road family the catalogue never saw is left exactly as the game laid it.
        /// </summary>
        private bool TryGetWidths(Entity prefab, out float laidWidth, out float variantWidth)
        {
            laidWidth = 0f;
            variantWidth = 0f;

            CrosswalkCatalog catalog = Mod.Catalog;

            if (catalog == null || !catalog.IsCrossingLane(prefab))
            {
                ReportUnknown(prefab);
                return false;
            }

            variantWidth = catalog.AuthoredWidth(prefab);
            laidWidth = catalog.LaidWidth(prefab);

            if (variantWidth <= 0.01f || laidWidth <= 0.01f)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Writes one crossing lane's width offset, flags included. True if anything changed.
        /// </summary>
        private bool WriteOffset(Entity lane, float offset)
        {
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
        /// Names a crossing lane prefab the catalogue does not know, once, so a road family this
        /// mod silently leaves alone can be identified from the log rather than guessed at.
        /// </summary>
        private void ReportUnknown(Entity prefab)
        {
            if (prefab == Entity.Null || !m_ReportedUnknown.Add(prefab))
            {
                return;
            }

            string name = m_PrefabSystem.TryGetPrefab<PrefabBase>(prefab, out PrefabBase managed) && managed != null
                ? managed.name
                : prefab.ToString();

            Mod.Log.Info(
                $"{Mod.ModName}: crossing lane {name} is not one this mod recognises, so its "
                + "crossings are left at the width the game laid them");
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
                for (int i = 0; i < cached.Count; i++)
                {
                    if (EntityManager.Exists(cached[i]))
                    {
                        into.Add(cached[i]);
                    }
                }

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

                // A diagonal this mod laid is left out of the numbering on purpose. The per-crossing
                // overrides are keyed by position in this list, and letting a crossing that comes
                // and goes with every re-lay into it would shift every override after it — a
                // junction's widths would move from one crossing to another the first time a
                // scramble was turned on. Diagonals take the junction's width instead.
                if (EntityManager.HasComponent<CrosswalkAdded>(lane))
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
            CrosswalkWidthSetting settings = Mod.Settings;

            if (settings == null || !settings.Enabled)
            {
                // Off means off. A per-junction width is still remembered — turning the mod back on
                // restores it — but nothing of the mod's is left in the geometry while it is off,
                // which is what makes disabling it a real test of whether it caused something.
                return 1f;
            }

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
            // A middle crossing is numbered by which diagonal it is, because it is not in the
            // junction's lane list and has no position there to be numbered by.
            if (EntityManager.Exists(lane) && EntityManager.HasComponent<CrosswalkJunction>(lane))
            {
                CrosswalkJunction belongs = EntityManager.GetComponentData<CrosswalkJunction>(lane);

                return belongs.m_Junction == node
                    ? kAddedIndexBase + belongs.m_Ordinal
                    : -1;
            }

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

            PruneOverrides(node);

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

                CrosswalkLaneOverride entry = overrides[i];

                entry.m_Version = CrosswalkLaneOverride.kCurrentVersion;
                entry.m_Scale = scale <= 0f ? 0f : math.clamp(scale, 0.25f, 8f);

                // The scale and the position are set separately and neither should wipe the other,
                // so this edits the entry rather than replacing it. It is only dropped once both
                // are back to nothing.
                if (entry.IsEmpty)
                {
                    overrides[i] = entry;
                    RestoreNode(node);

                    overrides = EntityManager.GetBuffer<CrosswalkLaneOverride>(node);
                    overrides.RemoveAt(i);

                    // An empty buffer would still be written into the save, and a save carrying a
                    // mod's empty leftovers is a small lie about what is in the city. Take it off
                    // once the last entry is gone.
                    if (overrides.Length == 0)
                    {
                        EntityManager.RemoveComponent<CrosswalkLaneOverride>(node);
                    }
                }
                else
                {
                    overrides[i] = entry;
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

        /// <summary>
        /// Puts one junction's crossings back where the game laid them, before its overrides are
        /// thrown away.
        ///
        /// Where a crossing was moved from is recorded in the override itself and nowhere else, so
        /// dropping the override first would strand the crossing wherever it had been dragged to,
        /// with nothing left that knows where it belongs — not the mod, not the game, and not the
        /// "remove this mod's data" button. Every path that removes an override goes through here
        /// first.
        /// </summary>
        /// <summary>This junction's middle crossings, appended to a list of its own crossings.</summary>
        private void AppendAddedLanes(Entity node, List<Entity> into)
        {
            if (node == Entity.Null || m_AddedCrossingQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            NativeArray<Entity> lanes = m_AddedCrossingQuery.ToEntityArray(Allocator.Temp);

            try
            {
                for (int i = 0; i < lanes.Length; i++)
                {
                    CrosswalkJunction belongs =
                        EntityManager.GetComponentData<CrosswalkJunction>(lanes[i]);

                    if (belongs.m_Junction == node && !into.Contains(lanes[i]))
                    {
                        into.Add(lanes[i]);
                    }
                }
            }
            finally
            {
                lanes.Dispose();
            }
        }

        private void RestoreNode(Entity node)
        {
            if (node == Entity.Null
                || !EntityManager.Exists(node)
                || !EntityManager.HasBuffer<CrosswalkLaneOverride>(node))
            {
                return;
            }

            m_OrderCache.Clear();
            BuildOrder(node, m_CrossingLanes);

            List<Entity> lanes = new List<Entity>(m_CrossingLanes);

            // The junction's own crossings are only half of what an override can name. A middle
            // crossing is not in the lane list, so restoring only that list would drop a dragged
            // diagonal back to no base at all: the override is about to be deleted, and with it the
            // only record of where the diagonal started out.
            AppendAddedLanes(node, lanes);

            NativeList<Entity> touched = new NativeList<Entity>(lanes.Count, Allocator.Temp);

            try
            {
                for (int i = 0; i < lanes.Count; i++)
                {
                    if (RestoreShift(lanes[i], node))
                    {
                        touched.Add(lanes[i]);
                    }
                }

                if (touched.Length != 0)
                {
                    EntityManager.AddComponent(touched.AsArray(), ComponentType.ReadWrite<BatchesUpdated>());
                }
            }
            finally
            {
                touched.Dispose();
            }
        }

        /// <summary>The distance each end of one crossing has been moved along the road, in metres.</summary>
        public float2 GetShift(Entity node, int index)
        {
            if (node == Entity.Null
                || index < 0
                || !EntityManager.Exists(node)
                || !EntityManager.HasBuffer<CrosswalkLaneOverride>(node))
            {
                return default(float2);
            }

            DynamicBuffer<CrosswalkLaneOverride> overrides =
                EntityManager.GetBuffer<CrosswalkLaneOverride>(node, true);

            for (int i = 0; i < overrides.Length; i++)
            {
                if (overrides[i].m_Index == index)
                {
                    return overrides[i].m_Shift;
                }
            }

            return default(float2);
        }

        /// <summary>
        /// Moves one crossing's ends along the road. x is the start end, y is the end end; both the
        /// same slides the whole crossing, one alone swings it round.
        ///
        /// Clamped to a junction's worth of movement in either direction, because the crossing has
        /// to stay somewhere its ends are still on a footpath — a crossing dragged half a block up
        /// the road is a lane hanging in the air.
        /// </summary>
        public void SetShift(Entity node, int index, float2 shift)
        {
            if (node == Entity.Null || index < 0 || !EntityManager.Exists(node))
            {
                return;
            }

            shift = math.clamp(shift, -kMaxShift, kMaxShift);

            if (!EntityManager.HasBuffer<CrosswalkLaneOverride>(node))
            {
                if (math.abs(shift.x) < kEpsilon && math.abs(shift.y) < kEpsilon)
                {
                    return;
                }

                EntityManager.AddBuffer<CrosswalkLaneOverride>(node);
            }
            else
            {
                PruneOverrides(node);
            }

            // Pruning can take the buffer away again if every entry in it was stale, so this is
            // checked after it rather than before.
            if (!EntityManager.HasBuffer<CrosswalkLaneOverride>(node))
            {
                if (math.abs(shift.x) < kEpsilon && math.abs(shift.y) < kEpsilon)
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

                CrosswalkLaneOverride entry = overrides[i];

                entry.m_Version = CrosswalkLaneOverride.kCurrentVersion;
                entry.m_Shift = shift;

                if (entry.IsEmpty)
                {
                    // The entry is about to go, and it holds the only record of where this
                    // crossing belongs. Put the crossing back while the record still exists.
                    overrides[i] = entry;
                    RestoreNode(node);

                    overrides = EntityManager.GetBuffer<CrosswalkLaneOverride>(node);
                    overrides.RemoveAt(i);

                    if (overrides.Length == 0)
                    {
                        EntityManager.RemoveComponent<CrosswalkLaneOverride>(node);
                    }
                }
                else
                {
                    overrides[i] = entry;
                }

                RequestNode(node);
                return;
            }

            if (math.abs(shift.x) >= kEpsilon || math.abs(shift.y) >= kEpsilon)
            {
                overrides.Add(new CrosswalkLaneOverride
                {
                    m_Version = CrosswalkLaneOverride.kCurrentVersion,
                    m_Index = index,
                    m_Shift = shift
                });

                RequestNode(node);
            }
        }

        /// <summary>
        /// Drops per-crossing entries that no longer name a crossing at this junction.
        ///
        /// Overrides are keyed by position in the node's crossing list, which is stable for a given
        /// junction layout but not across a change to it. Remove an arm from a four-way and the
        /// entries for crossings 2 and 3 have nothing to apply to — they would sit in the save
        /// forever, and would silently reappear on whatever ends up at those positions if an arm
        /// were added again. Done here, where overrides are edited, rather than in the per-frame
        /// pass, because it is a structural change and it only ever needs doing when the player is
        /// already working on this junction.
        /// </summary>
        private void PruneOverrides(Entity node)
        {
            if (!EntityManager.HasBuffer<CrosswalkLaneOverride>(node))
            {
                return;
            }

            BuildOrder(node, m_CrossingLanes);

            int count = m_CrossingLanes.Count;

            if (count == 0)
            {
                return;   // mid-rebuild, most likely; better to keep the entries than guess
            }

            DynamicBuffer<CrosswalkLaneOverride> overrides =
                EntityManager.GetBuffer<CrosswalkLaneOverride>(node);

            bool removedAny = false;

            for (int i = overrides.Length - 1; i >= 0; i--)
            {
                int key = overrides[i].m_Index;

                // Middle crossings are keyed from kAddedIndexBase up, by which diagonal they are,
                // and they are deliberately not in the junction's lane list — so every one of them
                // is above the count this loop is checking against. Judging them by it would throw
                // away the width of every middle crossing the moment the player edited any of them.
                // They are keyed by ordinal, which survives a re-lay, so there is nothing to prune.
                if (key >= kAddedIndexBase)
                {
                    continue;
                }

                if (key >= count || key < 0)
                {
                    overrides.RemoveAt(i);
                    removedAny = true;
                }
            }

            // Only when this call is what emptied it. A buffer that was already empty belongs to
            // whoever just added it and is about to put an entry in — taking it away here would
            // pull it out from under them.
            if (removedAny && overrides.Length == 0)
            {
                EntityManager.RemoveComponent<CrosswalkLaneOverride>(node);
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
                    RestoreNode(node);
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
        /// Not to zero — to `laidWidth - variantWidth`, which is the value LaneSystem writes
        /// itself. Zeroing would look like a restore and would in fact be a second, quieter change
        /// to the city: on a themed road the game uses that offset to hold the variant it chose to
        /// the width the composition declared, so a crossing zeroed here comes back the wrong size
        /// and stays wrong after the mod is gone.
        ///
        /// NodeLane goes into the save, which is why this runs at shutdown and on the purge.
        /// </summary>
        public void RestoreAuthoredWidths()
        {
            // This runs outside the per-frame pass, where the cache was last filled — possibly
            // several frames ago, and the indices it holds decide which override belongs to which
            // crossing.
            m_OrderCache.Clear();

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

                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(lane).m_Prefab;

                    if (!TryGetWidths(prefab, out float laidWidth, out float variantWidth))
                    {
                        continue;   // never touched it, so there is nothing to put back
                    }

                    bool changed = WriteOffset(lane, laidWidth - variantWidth);

                    // Not every crossing lane has an owner: the ones this mod lays deliberately do
                // not, and name their junction on their own component instead.
                Entity node = Entity.Null;

                if (EntityManager.HasComponent<Owner>(lane))
                {
                    node = EntityManager.GetComponentData<Owner>(lane).m_Owner;
                }
                else if (EntityManager.HasComponent<CrosswalkJunction>(lane))
                {
                    node = EntityManager.GetComponentData<CrosswalkJunction>(lane).m_Junction;
                }

                    changed |= RestoreShift(lane, node);

                    if (changed)
                    {
                        touched.Add(lane);
                    }
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

        /// <summary>
        /// Checks that every crossing lane points at a lane prefab the game actually knows, and
        /// repairs the ones that do not. Returns how many were repaired.
        ///
        /// This exists because versions 1.2 to 1.5 of this mod gave a junction its own width by
        /// cloning the lane prefab with EntityManager.Instantiate and pointing the lane at the
        /// clone. Instantiate strips the Prefab tag, and the clone was never registered with
        /// PrefabSystem, so a city saved in that state records prefab references that resolve to
        /// nothing on the next load:
        ///
        ///     [SceneFlow] [WARN]  Unknown prefab ID: [Missing]:[Missing]
        ///
        /// and the load then fails somewhere unrelated — in one case ClimateSystem, then the
        /// terrain render settings, then a native crash. A named missing prefab is survivable; a
        /// blank one is not.
        ///
        /// What this can and cannot do is worth being exact about. A clone made *this session*
        /// still carries the original's PrefabData index, so it resolves and the lane is put back
        /// on the real prefab. A clone that came out of a save does not: ResolvePrefabsSystem
        /// stamps an unresolved reference with a negative PrefabData index — that is what prints
        /// `Unknown prefab ID: [Missing]:[Missing]` — and PrefabSystem refuses negative indices, so
        /// there is nothing left to resolve back to. Those lanes are named in the log and left
        /// alone. And if the load itself fails, as it did once, this never runs at all.
        ///
        /// So: a genuine safety net for the current session, and a diagnostic for an affected save
        /// that still loads. It is not a repair for a save that will not load — nothing inside the
        /// mod can be, because the failure happens during deserialization, before any mod system
        /// gets a frame.
        /// </summary>
        public int RepairLanePrefabs()
        {
            NativeArray<Entity> lanes = m_AllCrossingQuery.ToEntityArray(Allocator.Temp);
            NativeList<Entity> repaired = new NativeList<Entity>(8, Allocator.Temp);

            int beyondRepair = 0;

            try
            {
                for (int i = 0; i < lanes.Length; i++)
                {
                    Entity lane = lanes[i];

                    if (!IsCrossing(lane))
                    {
                        continue;
                    }

                    PrefabRef prefabRef = EntityManager.GetComponentData<PrefabRef>(lane);

                    if (IsRealPrefab(prefabRef.m_Prefab))
                    {
                        continue;
                    }

                    Entity replacement = ResolveRealPrefab(prefabRef.m_Prefab);

                    if (replacement == Entity.Null)
                    {
                        // Counted, not reported one by one. A wrong test here once matched every
                        // crossing in the city and wrote eleven and a half thousand warnings on a
                        // single load, which took the game's own logger down with it.
                        beyondRepair++;
                        continue;
                    }

                    prefabRef.m_Prefab = replacement;
                    EntityManager.SetComponentData(lane, prefabRef);
                    repaired.Add(lane);
                }

                if (repaired.Length != 0)
                {
                    EntityManager.AddComponent(repaired.AsArray(), ComponentType.ReadWrite<BatchesUpdated>());

                    Mod.Log.Info(
                        $"{Mod.ModName}: repaired {repaired.Length} crossing lanes that pointed at a "
                        + "prefab the game does not know — left over from an older version of this mod");
                }

                if (beyondRepair != 0)
                {
                    Mod.Log.Warn(
                        $"{Mod.ModName}: {beyondRepair} crossing lanes point at a prefab the game "
                        + "cannot resolve and cannot be put right from here; they are left alone");
                }

                return repaired.Length;
            }
            finally
            {
                repaired.Dispose();
                lanes.Dispose();
            }
        }

        /// <summary>
        /// True if this entity is a lane prefab the game would be able to save a reference to.
        ///
        /// The test is whether PrefabSystem can name it. It is tempting to look for the
        /// `Unity.Entities.Prefab` tag instead, and that is wrong: PrefabSystem builds prefab
        /// entities with plain `CreateEntity` from the component set the prefab asks for and never
        /// adds that tag. Testing for it matched nothing, so every crossing in the city looked
        /// broken — eleven and a half thousand warnings on one load, and the logger gave up partway
        /// through.
        ///
        /// PrefabData alone is not enough either: ResolvePrefabsSystem stamps an unresolved
        /// reference with a *negative* index, which is exactly the case worth catching, and
        /// TryGetPrefab is what rejects it.
        /// </summary>
        private bool IsRealPrefab(Entity prefab)
        {
            return prefab != Entity.Null
                && EntityManager.Exists(prefab)
                && EntityManager.HasComponent<NetLaneData>(prefab)
                && EntityManager.HasComponent<PrefabData>(prefab)
                && m_PrefabSystem.TryGetPrefab<PrefabBase>(prefab, out PrefabBase managed)
                && managed != null;
        }

        /// <summary>
        /// The real prefab entity behind something that is not one: a clone keeps the original's
        /// PrefabData index, so the managed prefab it names still leads back to the genuine entity.
        /// </summary>
        private Entity ResolveRealPrefab(Entity impostor)
        {
            if (impostor == Entity.Null || !EntityManager.Exists(impostor))
            {
                return Entity.Null;
            }

            if (!m_PrefabSystem.TryGetPrefab<PrefabBase>(impostor, out PrefabBase managed) || managed == null)
            {
                return Entity.Null;
            }

            if (!m_PrefabSystem.TryGetEntity(managed, out Entity real) || !IsRealPrefab(real))
            {
                return Entity.Null;
            }

            return real;
        }

        /// <summary>
        /// Takes every trace of this mod back out of the city: widths back to authored, every
        /// per-junction and per-crossing override forgotten.
        ///
        /// Saving after this leaves a city indistinguishable from one the mod never ran on.
        /// </summary>
        public int PurgeFromCity()
        {
            // Restore before clearing, not after: the curve a crossing was moved from is recorded
            // in the override itself, so clearing first would throw away the only record of where
            // the crossing belongs and leave it standing wherever it was dragged to.
            int cleared = 0;

            try
            {
                // ClearAllOverrides restores before it strips, which is the order that matters.
                cleared = ClearAllOverrides();
            }
            catch (System.Exception e)
            {
                Mod.Log.Error($"{Mod.ModName}: could not restore every crossing: {e}");
            }

            try
            {
                RepairLanePrefabs();
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"{Mod.ModName}: could not check the crossing lane prefabs: {e.Message}");
            }

            m_FullPassPending = false;
            m_PendingNodes.Clear();

            return cleared;
        }

        /// <summary>Removes every per-junction and per-crossing width in the city.</summary>
        public int ClearAllOverrides()
        {
            if (m_OverriddenNodeQuery.IsEmptyIgnoreFilter)
            {
                return 0;
            }

            int count = m_OverriddenNodeQuery.CalculateEntityCount();

            // Crossings first, overrides second. The override is the only record of where a moved
            // crossing belongs, so stripping it first would leave every one of them standing where
            // it was dragged to with nothing able to put it back.
            RestoreAuthoredWidths();

            EntityManager.RemoveComponent(m_OverriddenNodeQuery, ComponentType.ReadWrite<CrosswalkOverride>());
            EntityManager.RemoveComponent(m_OverriddenNodeQuery, ComponentType.ReadWrite<CrosswalkLaneOverride>());

            RequestFullPass();

            return count;
        }
    }
}
