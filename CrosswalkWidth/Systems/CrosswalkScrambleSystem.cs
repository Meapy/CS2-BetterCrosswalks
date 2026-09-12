using System.Collections.Generic;
using Colossal.Mathematics;
using CrosswalkWidth.Components;
using Game;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CrosswalkWidth.Systems
{
    /// <summary>
    /// Lays the diagonals of a scramble crossing — the Shibuya kind, where the whole junction stops
    /// and people cross corner to corner through the middle.
    ///
    /// The junction carries a <see cref="CrosswalkScramble"/> tag; the diagonals are laid from
    /// whatever crossings are standing there and cleared again whenever the junction is rebuilt, so
    /// they follow its geometry — widen a road, the corners move, and the next pass runs the
    /// diagonals between wherever the corners ended up.
    ///
    /// Clearing them is this system's own job. These lanes are deliberately not owned by their
    /// junction (see <see cref="CrosswalkJunction"/> for why), so LaneSystem cannot see them and
    /// does not sweep them away when it re-lays a node the way it does with anything else it does
    /// not recognise.
    ///
    /// The diagonals are not invented out of nothing. Every one of them starts and ends on a
    /// <see cref="PathNode"/> that an existing crossing already ends on, so what is being added to
    /// the pedestrian graph is an edge between two points that were already in it — the safest kind
    /// of addition there is, and the one that survives a save and reload without referring to
    /// anything that might not be there.
    ///
    /// See NOTES.md, "adding a crossing", for the traced game behaviour behind each of these.
    /// </summary>
    public partial class CrosswalkScrambleSystem : GameSystemBase
    {
        private const float kPi = 3.14159265f;

        /// <summary>
        /// The least two corners may be apart, in radians, for the line between them to count as
        /// crossing the junction rather than cutting one of its corners — a little over a right
        /// angle.
        ///
        /// Loose on purpose. Opposite corners of a tidy four-way are half a turn apart, but a
        /// five-way's are nearer 115 degrees and a lopsided four-way's around 130, and refusing
        /// those would be a worse failure than the occasional odd diagonal this is here to stop. A
        /// corner being cut rather than a junction being crossed comes in under 100.
        /// </summary>
        private const float kMinSweep = 1.75f;

        /// <summary>
        /// How far the line between two corners may pass from the middle of the junction, as a
        /// fraction of how far the corners themselves are from it. Past this it is running around
        /// the outside rather than through the middle.
        /// </summary>
        private const float kMaxMiss = 0.7f;

        /// <summary>A diagonal shorter than this is not worth laying.</summary>
        private const float kMinDiagonal = 4f;

        /// <summary>
        /// How far a corner may sit from the path node the diagonal is actually wired to, in metres.
        ///
        /// The corner is the midpoint of two crossing ends, which is what makes the scramble
        /// symmetric, but the path node has to be one of the two — a place in the pedestrian graph
        /// cannot be averaged. On an ordinary junction the two ends are close enough that the gap is
        /// a stride and nobody sees it. On a very wide one it would be far enough for a citizen to
        /// visibly step sideways as they join the crossing, so past this the corner is pulled back
        /// towards the node it belongs to.
        /// </summary>
        private const float kMaxCornerShift = 4f;

        /// <summary>
        /// The most a diagonal's middle may be lifted to sit on the junction surface, in metres.
        ///
        /// Sanity only. A real crown is a few centimetres to half a metre; anything past this means
        /// the height read back was not the one expected, and a crossing arching a storey into the
        /// air is a far worse failure than one lying flat.
        /// </summary>
        private const float kMaxLift = 3f;

        /// <summary>
        /// A little extra height on a lifted diagonal, in metres.
        ///
        /// A cubic can be made to pass through the junction's surface at one point, and the middle
        /// is the point that matters, but the real surface between there and each corner is two
        /// crowned roads and the fillet between them rather than anything a cubic describes — so it
        /// still runs a shade under in the quarters. Six centimetres covers that and is invisible
        /// from any camera height the game allows.
        /// </summary>
        private const float kLiftClearance = 0.06f;

        /// <summary>
        /// Frames a junction must wait between one lay-out of its diagonals and the next.
        ///
        /// Nothing should be able to make this system rebuild every frame, but if something ever
        /// did — a mod that re-lays junctions continuously, a shape this system reads wrongly — the
        /// cost of the mistake is one rebuild every quarter second rather than sixty.
        /// </summary>
        private const int kRebuildCooldown = 15;

        /// <summary>Junctions looked at per update, so a city full of them cannot stall a frame.</summary>
        private const int kMaxPerUpdate = 4;

        /// <summary>
        /// Frames to wait after a city has finished loading before laying or clearing anything.
        ///
        /// A save loads by laying the whole city's lanes, and this system was joining in while that
        /// was still going on — laying diagonals during the load, then clearing and laying them
        /// again the moment loading reported complete. Three rounds of creating and deleting lanes
        /// across a city, the last of them landing exactly as the game finished building the
        /// pedestrian network, and it crashed on load. Nothing here is urgent; a couple of seconds
        /// of quiet costs nobody anything.
        /// </summary>
        private const int kSettleFrames = 180;

        /// <summary>Lanes cleared per update, rather than a whole city's worth at once.</summary>
        private const int kMaxClearedPerUpdate = 8;

        private EntityQuery m_ScrambleNodeQuery;
        private EntityQuery m_AddedLaneQuery;
        private EntityQuery m_ReshapedNodeQuery;
        private EntityQuery m_LegacyLaneQuery;

        private CrosswalkOverrideSystem m_OverrideSystem;
        private PrefabSystem m_PrefabSystem;

        private readonly Dictionary<Entity, int> m_LastBuilt = new Dictionary<Entity, int>();
        private readonly Dictionary<Entity, int> m_LiveCount = new Dictionary<Entity, int>();

        /// <summary>
        /// Junctions the player has asked to turn diagonals on or off at, waiting for the right
        /// phase of the frame.
        ///
        /// The button is pressed during the UI phase, which is past the point where the game's lane
        /// pipeline will look at anything. A lane created there is never added to its junction's
        /// SubLane buffer, never gets a signal group, and — because nothing can find it again —
        /// cannot be removed either. A lane deleted there is destroyed with the junction still
        /// pointing at it. So a press records what is wanted and nothing more, and the next update,
        /// which runs in Modification4 where lanes are laid, acts on it.
        /// </summary>
        private readonly Dictionary<Entity, bool> m_Requests = new Dictionary<Entity, bool>();

        /// <summary>Middle crossings the player has asked to remove, waiting for the right phase.</summary>
        private readonly List<Entity> m_PendingRemovals = new List<Entity>();

        private readonly List<Crossing> m_Crossings = new List<Crossing>();
        private readonly List<Corner> m_Corners = new List<Corner>();
        private readonly List<int2> m_Pairs = new List<int2>();
        private readonly List<Entity> m_Laid = new List<Entity>();

        /// <summary>Lanes whose junction has gone, cleared a few per update.</summary>
        private readonly List<Entity> m_Orphans = new List<Entity>();

        /// <summary>Which lane indices are taken at the junction being laid, in the low byte only.</summary>
        private readonly bool[] m_UsedIndices = new bool[256];

        /// <summary>Lane prefabs already named in the log as unusable, so each is named once.</summary>
        private readonly HashSet<Entity> m_ReportedPrefab = new HashSet<Entity>();

        private float3 m_Centre;
        private float m_SurfaceHeight;
        private float m_LastLift;

        private int m_Frame;
        private bool m_Failed;

        /// <summary>True once a city is loaded, false in the menu and during a load.</summary>
        private bool m_InCity;

        /// <summary>
        /// False until a full pass has confirmed every middle crossing in this city carries a
        /// number. Saves written by 0.12.0 have crossings with no number at all, and a whole-query
        /// scan is not something to run every frame for the rest of a session.
        /// </summary>
        private bool m_NumbersChecked;


        /// <summary>No junction is laid again before this frame, to let a sweep finish first.</summary>
        private int m_HoldUntilFrame;

        /// <summary>One crossing already standing at the junction, as this system reads it.</summary>
        private struct Crossing
        {
            public Entity m_Lane;
            public Entity m_Prefab;
            public PedestrianLaneFlags m_Flags;
            public float3 m_Start;
            public float3 m_End;
            public PathNode m_StartNode;
            public PathNode m_EndNode;
            public float3 m_Midpoint;
            public float m_Angle;
            public float m_Length;
            public NodeLane m_NodeLane;
            public bool m_HasSignal;
            public LaneSignal m_Signal;
            public bool m_HasSeed;
        }

        /// <summary>A place several crossings end at, which is to say a corner of the junction.</summary>
        private struct Corner
        {
            public float3 m_Position;
            public PathNode m_Node;
            public int m_Crossing;
            public float m_Angle;
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            m_OverrideSystem = World.GetOrCreateSystemManaged<CrosswalkOverrideSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            m_ScrambleNodeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<CrosswalkScramble>(),
                    ComponentType.ReadOnly<Game.Net.Node>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });

            // Junctions the game is re-laying. These lanes are not owned by the junction any more,
            // so LaneSystem no longer sweeps them away when it rebuilds one — this mod has to do it
            // itself, or a diagonal would be left behind across a junction that has changed shape.
            m_ReshapedNodeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<CrosswalkScramble>(),
                    ComponentType.ReadOnly<Game.Net.Node>(),
                    ComponentType.ReadOnly<Updated>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });

            // Crossings laid by a build of this mod that still gave them an Owner. Those are in
            // their junction's SubLane buffer, which is the thing that crashes the game, so a save
            // carrying them has to be migrated rather than left alone.
            m_LegacyLaneQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<CrosswalkAdded>(),
                    ComponentType.ReadOnly<Owner>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>()
                }
            });

            m_AddedLaneQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<CrosswalkAdded>(),
                    ComponentType.ReadOnly<CrosswalkJunction>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>()
                }
            });
        }

        /// <summary>True if this junction has enough roads meeting at it for a scramble to mean anything.</summary>
        public bool CanScramble(Entity node)
        {
            CrosswalkWidthSetting settings = Mod.Settings;

            if (settings == null || !settings.EnableMiddleCrossings)
            {
                return false;   // the button does not appear while the feature is switched off
            }

            if (node == Entity.Null
                || !EntityManager.Exists(node)
                || !EntityManager.HasBuffer<ConnectedEdge>(node))
            {
                return false;
            }

            return EntityManager.GetBuffer<ConnectedEdge>(node, true).Length >= 4;
        }

        /// <summary>True if this junction is set to have diagonals, whether or not they are standing.</summary>
        public bool HasScramble(Entity node)
        {
            if (node == Entity.Null || !EntityManager.Exists(node))
            {
                return false;
            }

            // A press that has not been acted on yet still counts, so the button's label flips the
            // moment it is clicked rather than a frame later.
            if (m_Requests.TryGetValue(node, out bool wanted))
            {
                return wanted;
            }

            return EntityManager.HasComponent<CrosswalkScramble>(node);
        }

        /// <summary>
        /// Asks for the diagonals at one junction to be turned on or off.
        ///
        /// Nothing happens here beyond recording it. Both laying a lane and deleting one have to
        /// happen in Modification4, and this is called from the UI phase — see m_Requests.
        /// </summary>
        public bool SetScramble(Entity node, bool wanted)
        {
            if (node == Entity.Null || !EntityManager.Exists(node))
            {
                return false;
            }

            if (wanted && !CanScramble(node))
            {
                Mod.Log.Info(
                    $"{Mod.ModName}: junction {node.Index} has fewer than four roads meeting at it, "
                    + "so there is no middle to cross");
                return false;
            }

            m_Requests[node] = wanted;

            return true;
        }

        /// <summary>
        /// Takes one diagonal out and remembers not to lay it again.
        ///
        /// The remembering is the point. These crossings are not stored — they are worked out from
        /// the junction's shape whenever they are missing — so deleting the lane alone would have it
        /// back the next time anything touched the junction. The junction keeps a note of which of
        /// its diagonals have been taken out by hand.
        /// </summary>
        public bool RemoveOne(Entity lane)
        {
            if (lane == Entity.Null
                || !EntityManager.Exists(lane)
                || !EntityManager.HasComponent<CrosswalkJunction>(lane))
            {
                return false;
            }

            // Recorded, not done. This is called from a button, which is the UI phase — far too late
            // in the frame to be deleting a lane. ApplyRequests carries it out in Modification4.
            m_PendingRemovals.Add(lane);
            return true;
        }

        private void RemoveOneNow(Entity lane)
        {
            if (lane == Entity.Null
                || !EntityManager.Exists(lane)
                || !EntityManager.HasComponent<CrosswalkJunction>(lane))
            {
                return;
            }

            CrosswalkJunction belongs = EntityManager.GetComponentData<CrosswalkJunction>(lane);
            Entity node = belongs.m_Junction;

            // Recorded first, and nothing is deleted unless it was. The record is the only thing
            // that stops this crossing being laid again: the junction is still tagged, the next
            // reconcile finds a diagonal missing there, and lays it. Delete without recording and
            // the player gets a crossing that will not go away, deleted and re-laid every few
            // seconds for as long as they look at it.
            if (node == Entity.Null
                || !EntityManager.Exists(node)
                || !EntityManager.HasComponent<CrosswalkScramble>(node)
                || belongs.m_Ordinal < 0
                || belongs.m_Ordinal >= 32)
            {
                Mod.Log.Warn(
                    $"{Mod.ModName}: middle crossing {lane.Index} was left alone — there is nowhere "
                    + "to record that it was taken out, so removing it would not stick");
                return;
            }

            CrosswalkScramble scramble = EntityManager.GetComponentData<CrosswalkScramble>(node);
            scramble.m_Version = CrosswalkScramble.kCurrentVersion;
            scramble.m_Removed |= 1 << belongs.m_Ordinal;

            EntityManager.SetComponentData(node, scramble);

            if (!EntityManager.HasComponent<Deleted>(lane))
            {
                EntityManager.AddComponent<Deleted>(lane);
            }

            m_LiveCount.Remove(node);
            m_LastBuilt[node] = m_Frame;

            Mod.Log.Info(
                $"{Mod.ModName}: junction {node.Index} — middle crossing {belongs.m_Ordinal + 1} "
                + "taken out, and it will not be laid again");
        }

        /// <summary>Forgets every middle crossing taken out by hand at one junction.</summary>
        public void RestoreRemoved(Entity node)
        {
            if (node == Entity.Null
                || !EntityManager.Exists(node)
                || !EntityManager.HasComponent<CrosswalkScramble>(node))
            {
                return;
            }

            CrosswalkScramble scramble = EntityManager.GetComponentData<CrosswalkScramble>(node);

            if (scramble.m_Removed == 0)
            {
                return;
            }

            scramble.m_Version = CrosswalkScramble.kCurrentVersion;
            scramble.m_Removed = 0;

            EntityManager.SetComponentData(node, scramble);
            m_LastBuilt.Remove(node);
        }

        /// <summary>Carries out the presses recorded since the last update.</summary>
        private void ApplyRequests()
        {
            for (int i = 0; i < m_PendingRemovals.Count; i++)
            {
                RemoveOneNow(m_PendingRemovals[i]);
            }

            m_PendingRemovals.Clear();

            if (m_Requests.Count == 0)
            {
                return;
            }

            foreach (KeyValuePair<Entity, bool> request in m_Requests)
            {
                Entity node = request.Key;

                if (node == Entity.Null || !EntityManager.Exists(node))
                {
                    continue;
                }

                if (request.Value)
                {
                    if (!EntityManager.HasComponent<CrosswalkScramble>(node))
                    {
                        EntityManager.AddComponentData(node, new CrosswalkScramble
                        {
                            m_Version = CrosswalkScramble.kCurrentVersion
                        });
                    }

                    // Cleared so the pass a moment later lays them in this same frame, rather than
                    // waiting out a cooldown left over from an earlier attempt. Turning a junction's
                    // middle crossings on again also forgets any taken out by hand before — the
                    // button says spawn, so it spawns the lot.
                    RestoreRemoved(node);
                    m_LastBuilt.Remove(node);
                    continue;
                }

                RemoveLanes(node);

                if (EntityManager.HasComponent<CrosswalkScramble>(node))
                {
                    EntityManager.RemoveComponent<CrosswalkScramble>(node);
                }

                m_LastBuilt.Remove(node);

                Mod.Log.Info($"{Mod.ModName}: junction {node.Index} no longer has middle crossings");
            }

            m_Requests.Clear();
        }

        /// <summary>
        /// Takes every diagonal in the city back out, and forgets which junctions had them.
        ///
        /// Used by the city-wide reset and the purge: after this the city has no crossing in it
        /// that this mod laid.
        /// </summary>
        public int RemoveAll()
        {
            int nodes = 0;

            NativeArray<Entity> tagged = m_ScrambleNodeQuery.ToEntityArray(Allocator.Temp);

            try
            {
                for (int i = 0; i < tagged.Length; i++)
                {
                    RemoveLanes(tagged[i]);
                    nodes++;
                }
            }
            finally
            {
                tagged.Dispose();
            }

            if (!m_ScrambleNodeQuery.IsEmptyIgnoreFilter)
            {
                EntityManager.RemoveComponent(
                    m_ScrambleNodeQuery, ComponentType.ReadWrite<CrosswalkScramble>());
            }

            // Anything left carrying the marker belongs to a junction whose tag has already gone —
            // an orphan from an older version, or from a junction deleted while its lanes lived on.
            RemoveAllLanes();

            m_LastBuilt.Clear();
            m_Requests.Clear();

            return nodes;
        }

        /// <summary>
        /// Nothing happens during a load, and nothing happens for a while after one.
        ///
        /// The city loads by laying every lane in it, and this system used to join in — laying
        /// diagonals while that was still running, then clearing and laying them again the instant
        /// loading reported complete. That third round landed just as the game finished building the
        /// pedestrian network and took the game down on load, which is the worst way for this to
        /// fail: it locks a player out of their city rather than costing them a session.
        ///
        /// So the system is inert until a city is loaded and has been still for a few seconds. What
        /// it does then depends on the setting: lay the diagonals, or clear away any that are there.
        /// </summary>
        protected override void OnGameLoadingComplete(
            Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);

            m_InCity = mode == GameMode.Game;
            m_HoldUntilFrame = m_Frame + kSettleFrames;

            m_LastBuilt.Clear();
            m_Requests.Clear();
            m_PendingRemovals.Clear();
            m_LiveCount.Clear();
            m_NumbersChecked = false;
        }

        protected override void OnUpdate()
        {
            if (m_Failed)
            {
                // Dropped, not kept. The panel is still there and its buttons still record what was
                // asked for; with nothing left to carry them out, holding on to them would be a list
                // that grows for the rest of the session and is acted on by nobody.
                m_Requests.Clear();
                m_PendingRemovals.Clear();
                return;
            }

            m_Frame++;

            // Not during a load, and not until the city has been still for a moment afterwards.
            if (!m_InCity || m_Frame < m_HoldUntilFrame)
            {
                return;
            }

            if (m_Requests.Count == 0 && m_PendingRemovals.Count == 0
                && m_ScrambleNodeQuery.IsEmptyIgnoreFilter
                && m_AddedLaneQuery.IsEmptyIgnoreFilter
                && m_LegacyLaneQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            CrosswalkWidthSetting settings = Mod.Settings;

            if (settings == null || !settings.EnableMiddleCrossings)
            {
                // Switched off means this system does nothing whatsoever — it does not lay
                // crossings, and it does not clear away ones already in the city either.
                //
                // Clearing them automatically was the obvious thing to do and it was a mistake. A
                // save carrying middle crossings would load, this system would start deleting them
                // a few frames later, and the game went down — every load, with no way for the
                // player to get in and stop it. Whatever is wrong with these lanes, touching them
                // without being asked turns one bad save into a city that cannot be opened.
                //
                // So removal is a thing the player asks for, from the settings, at a moment of their
                // choosing: "Put every crossing back to normal" or "Remove this mod's data from the
                // city". Both clear them. Neither happens on its own.
                m_Requests.Clear();
                m_PendingRemovals.Clear();
                return;
            }

            try
            {
                ApplyRequests();
                Reconcile();
            }
            catch (System.Exception e)
            {
                m_Failed = true;
                Mod.Log.Error(
                    $"{Mod.ModName}: middle crossings turned off for this session after an error: {e}");
            }
        }

        /// <summary>
        /// Lays the diagonals again at any tagged junction that has none standing.
        ///
        /// The test is deliberately "none at all" rather than "the right number". LaneSystem takes
        /// every one of a junction's added lanes at once when it re-lays it, so none standing is
        /// exactly the state that needs fixing, while a junction that still has some is either
        /// intact or one frame away from being emptied — and rebuilding it in that frame would only
        /// lay lanes for the pending deletion to take.
        /// </summary>
        private void Reconcile()
        {
            if (m_Frame < m_HoldUntilFrame)
            {
                return;   // a sweep is still being cleared up; laying now would race it
            }

            if (MigrateLegacy())
            {
                return;   // one junction at a time, and nothing else while it is happening
            }

            if (MigrateUnnumbered())
            {
                return;
            }

            SweepReshaped();
            CountLiveLanes();

            NativeArray<Entity> nodes = m_ScrambleNodeQuery.ToEntityArray(Allocator.Temp);

            try
            {
                // The build times are keyed by node, and a junction that is bulldozed leaves its
                // entry behind. Nothing reads a stale entry — the query only yields junctions that
                // exist — but the map would grow for the life of the session, so it is emptied
                // whenever it has drifted well past the number of junctions actually tagged.
                if (m_LastBuilt.Count > nodes.Length + 256)
                {
                    m_LastBuilt.Clear();
                }

                int done = 0;

                for (int i = 0; i < nodes.Length && done < kMaxPerUpdate; i++)
                {
                    Entity node = nodes[i];

                    if (m_LiveCount.ContainsKey(node))
                    {
                        continue;
                    }

                    if (m_LastBuilt.TryGetValue(node, out int last)
                        && m_Frame - last < kRebuildCooldown)
                    {
                        continue;
                    }

                    Rebuild(node);
                    done++;
                }
            }
            finally
            {
                nodes.Dispose();
            }
        }

        /// <summary>
        /// Takes out crossings left by a build that still gave them an Owner, one junction at a
        /// time. True while there is still work to do.
        ///
        /// These are the dangerous ones: an owned lane sits in its junction's SubLane buffer, and
        /// that is what the traffic light initialiser walks by number. Simply deleting them is not
        /// enough — taking a lane out of that buffer shifts it exactly as putting one in does. So
        /// the junction is handed back to the game as well, and LaneSystem rebuilds it and works out
        /// those numbers again against the buffer as it then stands.
        ///
        /// Tagging a junction Updated is otherwise a thing this system must never do, because
        /// LaneSystem would re-lay it and clear the diagonals, which would be laid again. It is safe
        /// here and only here: what is being cleared is never laid again in that form, so it happens
        /// once per save and then never.
        /// </summary>
        private bool MigrateLegacy()
        {
            if (m_LegacyLaneQuery.IsEmptyIgnoreFilter)
            {
                return false;
            }

            NativeArray<Entity> lanes = m_LegacyLaneQuery.ToEntityArray(Allocator.Temp);

            try
            {
                Entity junction = Entity.Null;
                int taken = 0;

                for (int i = 0; i < lanes.Length && taken < kMaxClearedPerUpdate; i++)
                {
                    Entity lane = lanes[i];

                    if (!EntityManager.Exists(lane) || EntityManager.HasComponent<Deleted>(lane))
                    {
                        continue;
                    }

                    Entity owner = EntityManager.GetComponentData<Owner>(lane).m_Owner;

                    if (junction == Entity.Null)
                    {
                        junction = owner;
                    }
                    else if (owner != junction)
                    {
                        continue;   // the rest wait their turn
                    }

                    EntityManager.AddComponent<Deleted>(lane);
                    taken++;
                }

                if (junction != Entity.Null
                    && EntityManager.Exists(junction)
                    && !EntityManager.HasComponent<Deleted>(junction)
                    && !EntityManager.HasComponent<Updated>(junction))
                {
                    EntityManager.AddComponent<Updated>(junction);
                }

                if (taken != 0)
                {
                    m_LastBuilt.Clear();
                    m_HoldUntilFrame = m_Frame + 30;

                    Mod.Log.Info(
                        $"{Mod.ModName}: cleared {taken} middle crossing{(taken == 1 ? "" : "s")} "
                        + "left by an earlier version, and handed junction "
                        + $"{junction.Index} back to the game to rebuild");
                }

                return taken != 0;
            }
            finally
            {
                lanes.Dispose();
            }
        }

        /// <summary>
        /// Clears middle crossings that were saved without a number, so they are laid again with
        /// one. True while there is still work to do.
        ///
        /// 0.12.0 wrote these lanes before there was anything to number them by, so every one of
        /// them reads back as number zero. Number zero is the key a width or a position set by hand
        /// is stored under, so a junction full of them is a junction where every diagonal shares one
        /// setting: widen the first and the rest follow, and there is no way to tell them apart
        /// again. They cannot be numbered in place either, because the numbering comes out of laying
        /// the junction — which corner pairs with which — and that is not knowable from the lane.
        ///
        /// So they are taken out and laid again. That is safe in a way the owned-lane migration is
        /// not: these lanes are in no junction's lane list, so deleting them moves nothing, and the
        /// junction is never handed back to the game. A player loading a 0.12.0 save sees the
        /// diagonals at one junction blink once.
        /// </summary>
        private bool MigrateUnnumbered()
        {
            if (m_NumbersChecked || m_AddedLaneQuery.IsEmptyIgnoreFilter)
            {
                m_NumbersChecked = true;
                return false;
            }

            NativeArray<Entity> lanes = m_AddedLaneQuery.ToEntityArray(Allocator.Temp);

            try
            {
                Entity junction = Entity.Null;
                int taken = 0;

                for (int i = 0; i < lanes.Length && taken < kMaxClearedPerUpdate; i++)
                {
                    Entity lane = lanes[i];

                    if (!EntityManager.Exists(lane) || EntityManager.HasComponent<Deleted>(lane))
                    {
                        continue;
                    }

                    CrosswalkJunction belongs = EntityManager.GetComponentData<CrosswalkJunction>(lane);

                    if (belongs.m_Version >= CrosswalkJunction.kCurrentVersion)
                    {
                        continue;
                    }

                    if (junction == Entity.Null)
                    {
                        junction = belongs.m_Junction;
                    }
                    else if (belongs.m_Junction != junction)
                    {
                        continue;   // the rest wait their turn
                    }

                    EntityManager.AddComponent<Deleted>(lane);
                    taken++;
                }

                if (taken == 0)
                {
                    m_NumbersChecked = true;
                    return false;
                }

                // Only this junction's cooldown, so the rest of the city is not re-laid alongside it.
                if (junction != Entity.Null)
                {
                    m_LastBuilt.Remove(junction);
                }

                m_HoldUntilFrame = m_Frame + 30;

                Mod.Log.Info(
                    $"{Mod.ModName}: re-laying {taken} unnumbered middle crossing"
                    + $"{(taken == 1 ? "" : "s")} at junction {junction.Index}");

                return true;
            }
            finally
            {
                lanes.Dispose();
            }
        }

        /// <summary>
        /// Clears the diagonals at any junction the game is rebuilding, so they are laid again
        /// against its new shape.
        ///
        /// This used to happen by itself: the lanes belonged to the junction, and LaneSystem deletes
        /// everything it does not recognise whenever it re-lays one. They are deliberately not owned
        /// by the junction any more, so that no longer happens and this has to do it.
        /// </summary>
        private void SweepReshaped()
        {
            if (m_ReshapedNodeQuery.IsEmptyIgnoreFilter || m_AddedLaneQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            NativeArray<Entity> nodes = m_ReshapedNodeQuery.ToEntityArray(Allocator.Temp);

            try
            {
                for (int i = 0; i < nodes.Length; i++)
                {
                    RemoveLanes(nodes[i]);
                    m_LastBuilt.Remove(nodes[i]);

                    // Not in this frame. The junction is being re-laid around us: LaneSystem writes
                    // through a barrier that plays back at the end of this phase, and the crossings
                    // it is replacing are still the old ones right now. Laying diagonals against
                    // them would pin the new crossings to corners that are about to move.
                    m_HoldUntilFrame = m_Frame + 2;
                }
            }
            finally
            {
                nodes.Dispose();
            }
        }

        /// <summary>How many added lanes each junction still has standing.</summary>
        private void CountLiveLanes()
        {
            m_LiveCount.Clear();
            m_Orphans.Clear();

            if (m_AddedLaneQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            NativeArray<Entity> lanes = m_AddedLaneQuery.ToEntityArray(Allocator.Temp);

            try
            {
                for (int i = 0; i < lanes.Length; i++)
                {
                    Entity junction =
                        EntityManager.GetComponentData<CrosswalkJunction>(lanes[i]).m_Junction;

                    if (junction == Entity.Null
                        || !EntityManager.Exists(junction)
                        || !EntityManager.HasComponent<CrosswalkScramble>(junction))
                    {
                        // The junction has been bulldozed, or no longer wants diagonals. Nothing
                        // else will ever clear these — the game only sweeps lanes it finds through a
                        // junction, and these are not reachable that way.
                        m_Orphans.Add(lanes[i]);
                        continue;
                    }

                    m_LiveCount.TryGetValue(junction, out int count);
                    m_LiveCount[junction] = count + 1;
                }
            }
            finally
            {
                lanes.Dispose();
            }

            for (int i = 0; i < m_Orphans.Count && i < kMaxClearedPerUpdate; i++)
            {
                if (EntityManager.Exists(m_Orphans[i])
                    && !EntityManager.HasComponent<Deleted>(m_Orphans[i]))
                {
                    EntityManager.AddComponent<Deleted>(m_Orphans[i]);
                }
            }
        }

        /// <summary>Lays one junction's diagonals from the crossings standing there now.</summary>
        private void Rebuild(Entity node)
        {
            m_LastBuilt[node] = m_Frame;

            if (!CollectCrossings(node) || !BuildCorners(node) || !ChoosePairs())
            {
                return;
            }

            int allocated = NextLaneIndex(node);
            m_Laid.Clear();
            m_LastLift = 0f;

            if (allocated < 0)
            {
                Mod.Log.Warn(
                    $"{Mod.ModName}: junction {node.Index} has no free lane index left, so no middle "
                    + "crossings were laid there");
                return;
            }

            // Kept apart from here on. The allocator hands back a composed value — the block in the
            // high byte, the place within it in the low byte — and only the place is a position in
            // m_UsedIndices. Treating the whole number as that position is how an earlier cut of
            // this silently laid nothing at every junction whose crossings were not in block zero.
            int block = allocated & 0xFF00;
            int place = allocated & 0xFF;

            CrosswalkScramble removed = EntityManager.HasComponent<CrosswalkScramble>(node)
                ? EntityManager.GetComponentData<CrosswalkScramble>(node)
                : default(CrosswalkScramble);

            for (int i = 0; i < m_Pairs.Count; i++)
            {
                Corner a = m_Corners[m_Pairs[i].x];
                Corner b = m_Corners[m_Pairs[i].y];

                // The corner it starts from, not its place in this list. The list is compacted — a
                // pair that sweeps too narrowly or misses the middle is never added to it — so a
                // junction that loses one diagonal would renumber every diagonal after it, and a
                // width set by hand on the third would jump to the second. The corner ring is built
                // the same way every time from the crossings standing there, so a corner keeps its
                // number for as long as the junction keeps its shape.
                int ordinal = m_Pairs[i].x;

                if (math.distance(a.m_Position, b.m_Position) < kMinDiagonal)
                {
                    continue;
                }

                if (removed.IsRemoved(ordinal))
                {
                    continue;   // taken out by hand; it does not come back on its own
                }

                if (place > byte.MaxValue)
                {
                    Mod.Log.Warn(
                        $"{Mod.ModName}: junction {node.Index} ran out of room for middle crossings "
                        + $"after laying {m_Laid.Count}");
                    break;
                }

                Entity diagonal = CreateDiagonal(node, a, b, (ushort)(block | place), ordinal);

                if (diagonal != Entity.Null)
                {
                    // Claimed as it is used, so the second diagonal at this junction cannot be given
                    // the same one — the lanes laid a moment ago are not in the junction's sub-lane
                    // buffer yet and would not be seen by another scan.
                    m_UsedIndices[place] = true;

                    while (place <= byte.MaxValue && m_UsedIndices[place])
                    {
                        place++;
                    }

                    m_Laid.Add(diagonal);
                }
            }

            if (m_Laid.Count == 0)
            {
                return;
            }

            // The new lanes carry Created from the archetype, which is what the pathfinding graph
            // watches for — LanesModifiedSystem adds them as edges without needing an owner. They
            // are not added to the junction's SubLane buffer and are not given a signal group, both
            // on purpose.
            //
            // The junction itself is deliberately left untagged. Tagging it Updated here would have
            // LaneSystem re-lay it next frame, which now clears these lanes, which would lay them
            // again — a loop with no way out of it.
            LogGeometry(node);

            // The lanes themselves, not the junction. A request for the junction is resolved through
            // the crossing order, and these lanes are deliberately not in it — so asking for the
            // junction would refresh every crossing except the ones just laid, and the diagonals
            // would stand at the prefab's own width until something forced a full pass.
            for (int i = 0; i < m_Laid.Count; i++)
            {
                m_OverrideSystem.RequestLane(m_Laid[i]);
            }

            // The heights go in the log because a diagonal that vanishes into the road looks like a
            // texture fault and is actually a geometry one, and the two are told apart by whether
            // the middle needed lifting and by how much.
            float lowest = m_Corners[0].m_Position.y;
            float highest = lowest;

            for (int i = 1; i < m_Corners.Count; i++)
            {
                lowest = math.min(lowest, m_Corners[i].m_Position.y);
                highest = math.max(highest, m_Corners[i].m_Position.y);
            }

            Mod.Log.Info(
                $"{Mod.ModName}: junction {node.Index} — {m_Laid.Count} middle "
                + $"crossing{(m_Laid.Count == 1 ? "" : "s")}, from {m_Crossings.Count} crossings "
                + $"and {m_Corners.Count} corners; corners {lowest:0.00}m to {highest:0.00}m, "
                + $"middle {m_SurfaceHeight:0.00}m, most lifted {m_LastLift:0.00}m");
        }

        /// <summary>
        /// Writes out everything the renderer reads for every crossing at this junction, the game's
        /// own alongside this mod's.
        ///
        /// Here because three rounds of this feature's faults have looked like texture problems in a
        /// screenshot and turned out to be numbers — a corner count, a curve height, a stripe
        /// spacing. A side-by-side of a crossing that draws correctly and one that does not is worth
        /// more than any amount of looking at pixels, and it costs a few lines in a log nobody reads
        /// unless something is wrong.
        /// </summary>
        private void LogGeometry(Entity node)
        {
            // At Debug, not Info, and the buffer is not even walked unless someone has turned Debug
            // on. This runs on every junction that gets diagonals, which on a city-wide first pass
            // is hundreds of them several lines each — useful when something is wrong, and pure
            // noise in the log of a player for whom nothing is.
            if (!Mod.Log.isDebugEnabled || !EntityManager.HasBuffer<Game.Net.SubLane>(node))
            {
                return;
            }

            DynamicBuffer<Game.Net.SubLane> subLanes =
                EntityManager.GetBuffer<Game.Net.SubLane>(node, true);

            for (int i = 0; i < subLanes.Length; i++)
            {
                Entity lane = subLanes[i].m_SubLane;

                if (lane == Entity.Null
                    || !EntityManager.Exists(lane)
                    || EntityManager.HasComponent<Deleted>(lane)
                    || !EntityManager.HasComponent<NodeLane>(lane)
                    || !EntityManager.HasComponent<Game.Net.Curve>(lane)
                    || !EntityManager.HasComponent<Game.Net.PedestrianLane>(lane))
                {
                    continue;
                }

                Game.Net.PedestrianLane pedestrian =
                    EntityManager.GetComponentData<Game.Net.PedestrianLane>(lane);

                if ((pedestrian.m_Flags & PedestrianLaneFlags.Crosswalk) == 0)
                {
                    continue;
                }

                if (EntityManager.HasComponent<CrosswalkAdded>(lane))
                {
                    continue;   // logged from m_Laid below, which is where this frame's are
                }

                LogLane(lane, false);
            }

            // The diagonals just laid are not in the junction's sub-lane buffer yet — the game adds
            // them to it later in this same frame — so they have to be read from the list of what
            // was created, or the dump would show only the road crossings and never the ones being
            // compared against them.
            for (int i = 0; i < m_Laid.Count; i++)
            {
                LogLane(m_Laid[i], true);
            }
        }

        private void LogLane(Entity lane, bool mine)
        {
            if (!Mod.Log.isDebugEnabled
                || !EntityManager.Exists(lane)
                || !EntityManager.HasComponent<NodeLane>(lane)
                || !EntityManager.HasComponent<Game.Net.Curve>(lane)
                || !EntityManager.HasComponent<Game.Net.PedestrianLane>(lane))
            {
                return;
            }

            NodeLane nodeLane = EntityManager.GetComponentData<NodeLane>(lane);
            Game.Net.Curve curve = EntityManager.GetComponentData<Game.Net.Curve>(lane);
            Game.Net.PedestrianLane pedestrian =
                EntityManager.GetComponentData<Game.Net.PedestrianLane>(lane);

            float chord = math.distance(curve.m_Bezier.a, curve.m_Bezier.d);
            float rise = math.max(
                curve.m_Bezier.b.y - (curve.m_Bezier.a.y + curve.m_Bezier.d.y) * 0.5f, 0f);

            int index = EntityManager.HasComponent<Game.Net.Lane>(lane)
                ? EntityManager.GetComponentData<Game.Net.Lane>(lane).m_MiddleNode.GetLaneIndex()
                : -1;

            Mod.Log.Debug(
                $"{Mod.ModName}:   {(mine ? "middle " : "road   ")}crossing {lane.Index}: "
                + $"lane index {index}, "
                + $"length {curve.m_Length:0.0}m over a {chord:0.0}m chord, rise {rise:0.00}m, "
                + $"width offset {nodeLane.m_WidthOffset.x:0.00}/{nodeLane.m_WidthOffset.y:0.00}, "
                + $"flags {nodeLane.m_Flags}, shared {nodeLane.m_SharedStartCount}/"
                + $"{nodeLane.m_SharedEndCount}, "
                + $"cut {(EntityManager.HasBuffer<CutRange>(lane) ? "yes" : "no")}, "
                + $"signal {(EntityManager.HasComponent<LaneSignal>(lane) ? "yes" : "no")}, "
                + $"lane flags {pedestrian.m_Flags}");
        }

        /// <summary>The crossings the game has laid at this junction, ignoring this mod's own.</summary>
        private bool CollectCrossings(Entity node)
        {
            m_Crossings.Clear();

            if (!EntityManager.HasBuffer<Game.Net.SubLane>(node))
            {
                return false;
            }

            DynamicBuffer<Game.Net.SubLane> subLanes =
                EntityManager.GetBuffer<Game.Net.SubLane>(node, true);

            for (int i = 0; i < subLanes.Length; i++)
            {
                Entity lane = subLanes[i].m_SubLane;

                if (lane == Entity.Null
                    || !EntityManager.Exists(lane)
                    || EntityManager.HasComponent<Deleted>(lane)
                    || EntityManager.HasComponent<CrosswalkAdded>(lane)
                    || !EntityManager.HasComponent<Game.Net.PedestrianLane>(lane)
                    || !EntityManager.HasComponent<Game.Net.Curve>(lane)
                    || !EntityManager.HasComponent<Game.Net.Lane>(lane)
                    || !EntityManager.HasComponent<NodeLane>(lane)
                    || !EntityManager.HasComponent<PrefabRef>(lane))
                {
                    continue;
                }

                Game.Net.PedestrianLane pedestrian =
                    EntityManager.GetComponentData<Game.Net.PedestrianLane>(lane);

                if ((pedestrian.m_Flags & PedestrianLaneFlags.Crosswalk) == 0)
                {
                    continue;
                }

                Game.Net.Curve curve = EntityManager.GetComponentData<Game.Net.Curve>(lane);
                Game.Net.Lane graph = EntityManager.GetComponentData<Game.Net.Lane>(lane);

                bool hasSignal = EntityManager.HasComponent<LaneSignal>(lane);

                m_Crossings.Add(new Crossing
                {
                    m_Lane = lane,
                    m_Prefab = EntityManager.GetComponentData<PrefabRef>(lane).m_Prefab,
                    m_Flags = pedestrian.m_Flags,
                    m_Start = curve.m_Bezier.a,
                    m_End = curve.m_Bezier.d,
                    m_StartNode = graph.m_StartNode,
                    m_EndNode = graph.m_EndNode,
                    m_Length = math.distance(curve.m_Bezier.a, curve.m_Bezier.d),
                    m_NodeLane = EntityManager.GetComponentData<NodeLane>(lane),
                    m_HasSignal = hasSignal,
                    m_Signal = hasSignal
                        ? EntityManager.GetComponentData<LaneSignal>(lane)
                        : default(LaneSignal),
                    m_HasSeed = EntityManager.HasComponent<PseudoRandomSeed>(lane)
                });
            }

            return m_Crossings.Count >= 4;
        }

        /// <summary>
        /// Works out where the corners of the junction are, in order around it.
        ///
        /// Not by how close the crossings' ends are to each other — that was the first attempt and
        /// it is wrong. Two crossings meeting at the same corner end at the edges of their own
        /// roads, and on a six-lane boulevard those two points are further apart than the whole
        /// width of a side street. Any radius generous enough for the boulevard swallows the side
        /// street whole, and any radius tight enough for the side street leaves the boulevard with
        /// eight corners instead of four — and eight corners pair up into four diagonals, which is
        /// twice as many as the junction has.
        ///
        /// So the corners are counted rather than measured. Crossings are put in order around the
        /// node, and every neighbouring pair of them has exactly one corner between it: the corner
        /// is where crossing i's near end meets crossing i+1's near end. Four crossings give four
        /// corners on any junction of any size, which is the thing the old radius could not
        /// promise.
        /// </summary>
        private bool BuildCorners(Entity node)
        {
            m_Corners.Clear();

            m_Centre = EntityManager.HasComponent<Game.Net.Node>(node)
                ? EntityManager.GetComponentData<Game.Net.Node>(node).m_Position
                : default(float3);

            // NodeGeometry.m_Position is a height, not a position — GeometrySystem fills it from
            // the node's own y, flattened where the junction has been levelled. It is the better of
            // the two because it is what the junction's own surface was built from.
            m_SurfaceHeight = EntityManager.HasComponent<NodeGeometry>(node)
                ? EntityManager.GetComponentData<NodeGeometry>(node).m_Position
                : m_Centre.y;

            for (int i = m_Crossings.Count - 1; i >= 0; i--)
            {
                Crossing crossing = m_Crossings[i];

                if (crossing.m_Length < 0.5f)
                {
                    m_Crossings.RemoveAt(i);
                    continue;
                }

                crossing.m_Midpoint = (crossing.m_Start + crossing.m_End) * 0.5f;

                float3 fromCentre = crossing.m_Midpoint - m_Centre;
                crossing.m_Angle = math.atan2(fromCentre.z, fromCentre.x);

                m_Crossings[i] = crossing;
            }

            if (m_Crossings.Count < 4)
            {
                return false;
            }

            m_Crossings.Sort(CompareCrossingsByAngle);

            int count = m_Crossings.Count;

            for (int i = 0; i < count; i++)
            {
                Crossing a = m_Crossings[i];
                Crossing b = m_Crossings[(i + 1) % count];

                NearestEnd(a, b.m_Midpoint, out float3 fromA, out PathNode nodeA);
                NearestEnd(b, a.m_Midpoint, out float3 fromB, out PathNode nodeB);

                // Halfway between the two, which is the corner itself.
                //
                // Taking one or the other looks reasonable per corner and terrible across a
                // junction: which of the two roads wins is decided by a few centimetres of distance
                // and changes from corner to corner, so the four corners end up pulled towards
                // different roads and the X they make is lopsided and off centre. The midpoint has
                // no such choice in it, so opposite corners stay opposite and the diagonals cross
                // in the middle.
                float3 point = (fromA + fromB) * 0.5f;

                // The path node still comes from one of the two ends — the outer one — because a
                // path node names a real place in the pedestrian graph and cannot be averaged.
                bool useA = math.distance(fromA, m_Centre) >= math.distance(fromB, m_Centre);
                float3 anchor = useA ? fromA : fromB;

                point = PulledBack(point, anchor);

                float3 offset = point - m_Centre;

                m_Corners.Add(new Corner
                {
                    m_Position = point,
                    m_Node = useA ? nodeA : nodeB,
                    m_Crossing = useA ? i : (i + 1) % count,
                    m_Angle = math.atan2(offset.z, offset.x)
                });
            }

            m_Corners.Sort(CompareByAngle);

            return m_Corners.Count >= 4;
        }

        /// <summary>
        /// The curve for a diagonal: a straight line corner to corner, lifted in the middle to sit
        /// on the junction rather than inside it.
        ///
        /// A road is crowned — its centre sits higher than its kerbs — and a junction is two crowns
        /// meeting, so the highest ground in it is the middle. The game's own crossings never notice:
        /// each spans one road's mouth, where a straight line between the kerbs sags below the crown
        /// by a couple of centimetres over a few metres. A diagonal spans the whole junction, and the
        /// same straight line can pass under the crown.
        ///
        /// So the two middle control points are raised until the curve passes through the junction's
        /// own surface height at its midpoint. A cubic at its midpoint is (a + 3b + 3c + d) / 8, so
        /// moving b and c by four thirds of the shortfall moves the midpoint by exactly the
        /// shortfall — the same four thirds LaneSystem uses in ModifyCurveHeight for the lanes that
        /// run around the inside of a node.
        ///
        /// Only ever upward. If the height read back is wrong, or the junction really does dip in
        /// the middle, a band hovering a few centimetres over the road is barely visible from above,
        /// while one a few centimetres under it is not visible at all.
        /// </summary>
        private Bezier4x3 Diagonal(float3 from, float3 to)
        {
            Bezier4x3 curve = NetUtils.StraightCurve(from, to);

            float lift = m_SurfaceHeight - (from.y + to.y) * 0.5f;

            m_LastLift = math.max(m_LastLift, math.max(0f, lift));

            if (lift <= 0.001f)
            {
                return curve;
            }

            lift = math.min(lift + kLiftClearance, kMaxLift) * (4f / 3f);

            curve.b.y += lift;
            curve.c.y += lift;

            return curve;
        }

        /// <summary>The length along a curve, close enough for the renderer's stripe spacing.</summary>
        private static float CurveLength(Bezier4x3 curve)
        {
            const int kSteps = 16;

            float3 previous = curve.a;
            float total = 0f;

            for (int i = 1; i <= kSteps; i++)
            {
                float3 point = Evaluate(curve, (float)i / kSteps);

                total += math.distance(previous, point);
                previous = point;
            }

            return total;
        }

        private static float3 Evaluate(Bezier4x3 curve, float t)
        {
            float u = 1f - t;
            float wa = u * u * u;
            float wb = 3f * u * u * t;
            float wc = 3f * u * t * t;
            float wd = t * t * t;

            return new float3(
                curve.a.x * wa + curve.b.x * wb + curve.c.x * wc + curve.d.x * wd,
                curve.a.y * wa + curve.b.y * wb + curve.c.y * wc + curve.d.y * wd,
                curve.a.z * wa + curve.b.z * wb + curve.c.z * wc + curve.d.z * wd);
        }

        /// <summary>Holds a corner within reach of the path node the diagonal will be wired to.</summary>
        private static float3 PulledBack(float3 point, float3 anchor)
        {
            float distance = math.distance(point, anchor);

            if (distance <= kMaxCornerShift || distance < 0.001f)
            {
                return point;
            }

            float keep = kMaxCornerShift / distance;

            return new float3(
                anchor.x + (point.x - anchor.x) * keep,
                anchor.y + (point.y - anchor.y) * keep,
                anchor.z + (point.z - anchor.z) * keep);
        }

        /// <summary>The end of one crossing that lies nearer a given point, and its path node.</summary>
        private static void NearestEnd(Crossing crossing, float3 towards, out float3 point, out PathNode node)
        {
            if (math.distance(crossing.m_End, towards) < math.distance(crossing.m_Start, towards))
            {
                point = crossing.m_End;
                node = crossing.m_EndNode;
                return;
            }

            point = crossing.m_Start;
            node = crossing.m_StartNode;
        }

        private static int CompareCrossingsByAngle(Crossing a, Crossing b)
        {
            return a.m_Angle.CompareTo(b.m_Angle);
        }

        private static int CompareByAngle(Corner a, Corner b)
        {
            return a.m_Angle.CompareTo(b.m_Angle);
        }

        /// <summary>
        /// Picks which corners to run a diagonal between: each corner to the one across from it.
        ///
        /// With four corners that is the two diagonals of the square. With more, "across" is half
        /// way round the ring, which gives a diagonal for every other corner and keeps a six- or
        /// eight-road junction from turning into a cat's cradle.
        ///
        /// Two checks then throw out anything that is not really a diagonal, whatever the ring says:
        /// a pair too close together in angle is a corner being cut rather than a junction being
        /// crossed, and a line that misses the middle by too much is running around the outside of
        /// the junction rather than through it. Both are deliberately generous — with the corners
        /// counted properly neither should ever fire, and a threshold tight enough to be clever
        /// would start refusing lopsided junctions that are perfectly fine.
        /// </summary>
        private bool ChoosePairs()
        {
            m_Pairs.Clear();

            int count = m_Corners.Count;
            int half = count / 2;

            if (half < 1)
            {
                return false;
            }

            float reach = 0f;

            for (int i = 0; i < count; i++)
            {
                reach += math.distance(m_Corners[i].m_Position, m_Centre);
            }

            reach /= count;

            // Strictly count/2, not a rounded-up half. One iteration more on an odd ring gives one
            // corner two diagonals of its own while another gets none.
            for (int i = 0; i < half; i++)
            {
                int j = (i + half) % count;

                if (i == j)
                {
                    continue;
                }

                if (Sweep(m_Corners[i].m_Angle, m_Corners[j].m_Angle) < kMinSweep)
                {
                    continue;
                }

                if (DistanceToMiddle(m_Corners[i].m_Position, m_Corners[j].m_Position) > reach * kMaxMiss)
                {
                    continue;
                }

                m_Pairs.Add(new int2(math.min(i, j), math.max(i, j)));
            }

            return m_Pairs.Count != 0;
        }

        /// <summary>The angle between two bearings, never more than half a turn.</summary>
        private static float Sweep(float a, float b)
        {
            float difference = math.abs(a - b);

            return difference > kPi ? (kPi + kPi - difference) : difference;
        }

        /// <summary>How far the line between two corners passes from the middle of the junction.</summary>
        private float DistanceToMiddle(float3 a, float3 b)
        {
            float dx = b.x - a.x;
            float dz = b.z - a.z;
            float lengthSq = dx * dx + dz * dz;

            if (lengthSq < 0.01f)
            {
                return math.distance(a, m_Centre);
            }

            float along = ((m_Centre.x - a.x) * dx + (m_Centre.z - a.z) * dz) / lengthSq;
            along = math.clamp(along, 0f, 1f);

            float offX = m_Centre.x - (a.x + dx * along);
            float offZ = m_Centre.z - (a.z + dz * along);

            return math.sqrt(offX * offX + offZ * offZ);
        }

        /// <summary>
        /// A free lane index for a new crossing, taken from the same block the junction's own
        /// crossings live in. Negative if there is none to be had.
        ///
        /// This number is **not** a flat counter, which is what two earlier versions of this method
        /// assumed and what crashed the game repeatedly. `LaneSystem` walks a junction handing out
        /// indices from `prevLaneIndex`, and advances it by **256 at a time** between one kind of
        /// lane and the next:
        ///
        /// <code>
        /// prevLaneIndex += 256;                               // between blocks
        /// m_SegmentIndex = (byte)(prevLaneIndex &gt;&gt; 8);        // the block number
        /// laneData.m_Index = (byte)(middleNode.GetLaneIndex() &amp; 0xFF);   // the place in it
        /// </code>
        ///
        /// So the sixteen bits are two numbers: the **high byte says which block of lanes at this
        /// junction the lane belongs to**, and the low byte is its place within that block. A
        /// junction's crossings all sit in the pedestrian block — 1537, 1539, 1541, 1543 in a real
        /// city, which is block 6, places 1, 3, 5 and 7.
        ///
        /// Both previous attempts got this wrong in opposite directions. "One above the highest
        /// anywhere" landed just past the last block, which was roughly right by luck but never
        /// checked. Then 0.10.6 read the low byte alone and picked the lowest free one — index 72,
        /// which is block 0, place 72: the *car lane* block, a place in a part of the junction the
        /// crossing has nothing to do with. That version made the crash more likely, not less.
        ///
        /// The right answer is to stay in the block the junction's own crossings are in, and take a
        /// free place inside it.
        /// </summary>
        private int NextLaneIndex(Entity node)
        {
            int block = CrossingBlock(node);

            if (block < 0)
            {
                return -1;
            }

            for (int i = 0; i < m_UsedIndices.Length; i++)
            {
                m_UsedIndices[i] = false;
            }

            if (EntityManager.HasBuffer<Game.Net.SubLane>(node))
            {
                DynamicBuffer<Game.Net.SubLane> subLanes =
                    EntityManager.GetBuffer<Game.Net.SubLane>(node, true);

                for (int i = 0; i < subLanes.Length; i++)
                {
                    Entity lane = subLanes[i].m_SubLane;

                    if (lane == Entity.Null
                        || !EntityManager.Exists(lane)
                        || !EntityManager.HasComponent<Game.Net.Lane>(lane))
                    {
                        continue;
                    }

                    Game.Net.Lane graph = EntityManager.GetComponentData<Game.Net.Lane>(lane);

                    Claim(graph.m_StartNode, node, block);
                    Claim(graph.m_MiddleNode, node, block);
                    Claim(graph.m_EndNode, node, block);
                }
            }

            // Above everything the game handed out in this block, which is where its own counter
            // would have gone next. Only if the block is full at the top does this fall back to a
            // gap lower down.
            int highest = -1;

            for (int i = 0; i < m_UsedIndices.Length; i++)
            {
                if (m_UsedIndices[i])
                {
                    highest = i;
                }
            }

            for (int i = highest + 1; i < m_UsedIndices.Length; i++)
            {
                if (!m_UsedIndices[i])
                {
                    return block | i;
                }
            }

            for (int i = 0; i < m_UsedIndices.Length; i++)
            {
                if (!m_UsedIndices[i])
                {
                    return block | i;
                }
            }

            return -1;
        }

        /// <summary>
        /// The block of lane indices this junction's crossings live in, as a high byte, or -1.
        ///
        /// Read off the crossings themselves rather than worked out: whichever block the game put
        /// them in is the one a new crossing belongs in too, whatever the junction's shape.
        /// </summary>
        private int CrossingBlock(Entity node)
        {
            for (int i = 0; i < m_Crossings.Count; i++)
            {
                Entity lane = m_Crossings[i].m_Lane;

                if (!EntityManager.Exists(lane)
                    || !EntityManager.HasComponent<Game.Net.Lane>(lane))
                {
                    continue;
                }

                PathNode middle = EntityManager.GetComponentData<Game.Net.Lane>(lane).m_MiddleNode;

                if (middle.GetOwnerIndex() == node.Index)
                {
                    return middle.GetLaneIndex() & 0xFF00;
                }
            }

            return -1;
        }

        /// <summary>Marks a path node's place in this block as taken, if that is where it sits.</summary>
        private void Claim(PathNode pathNode, Entity node, int block)
        {
            if (pathNode.GetOwnerIndex() != node.Index)
            {
                return;
            }

            int index = pathNode.GetLaneIndex();

            if ((index & 0xFF00) == block)
            {
                m_UsedIndices[index & 0xFF] = true;
            }
        }

        /// <summary>
        /// Lays one diagonal between two corners.
        ///
        /// Everything about it is copied from a crossing already standing at this junction — the
        /// lane prefab, its flags, its signal group — so the diagonal is the same kind of thing as
        /// the crossings around it: the same paint, the same width rules, and green at the same
        /// moment in the light cycle.
        /// </summary>
        private Entity CreateDiagonal(Entity node, Corner a, Corner b, ushort laneIndex, int ordinal)
        {
            // Either corner's crossing will do as the pattern, so try both rather than giving up on
            // the first one that turns out to be unusable.
            if (!TryPickSource(a, b, out Crossing source))
            {
                return Entity.Null;
            }

            EntityArchetype archetype = EntityManager
                .GetComponentData<NetLaneArchetypeData>(source.m_Prefab)
                .m_NodeLaneArchetype;

            if (!archetype.Valid)
            {
                return Entity.Null;
            }

            Entity lane = EntityManager.CreateEntity(archetype);

            EntityManager.SetComponentData(lane, new PrefabRef { m_Prefab = source.m_Prefab });

            EntityManager.SetComponentData(lane, new Game.Net.Lane
            {
                m_StartNode = a.m_Node,
                m_MiddleNode = new PathNode(node, laneIndex),
                m_EndNode = b.m_Node
            });

            Bezier4x3 bezier = Diagonal(a.m_Position, b.m_Position);

            EntityManager.SetComponentData(lane, new Game.Net.Curve
            {
                m_Bezier = bezier,

                // Measured along the curve, not corner to corner. The renderer divides this by the
                // zebra mesh's own length to work out how many times to repeat it, so a length that
                // does not match the curve lays the stripes at the wrong spacing. Once the middle is
                // raised the two stop being the same number.
                m_Length = CurveLength(bezier)
            });

            EntityManager.SetComponentData(lane, new Game.Net.PedestrianLane
            {
                m_Flags = source.m_Flags
            });

            // Copied from a crossing already standing here rather than left at default. NodeLane
            // carries more than the width offset the width pass rewrites: m_SharedStartCount and
            // m_SharedEndCount feed BatchDataHelpers.BuildCurveParams, which is what tells the
            // shader how the painted band behaves towards each end. Nothing in the traced source
            // writes those counts, so what a correct crossing has in them is not something to
            // guess at — it is something to copy from one that is already drawing properly at this
            // junction.
            EntityManager.SetComponentData(lane, source.m_NodeLane);

            // No Owner, deliberately. An Owner would put this lane into the junction's SubLane
            // buffer, and the traffic light initialiser indexes that buffer by numbers taken from
            // the road composition — unguarded, in a Burst job. See CrosswalkAdded for the code.
            // The junction is recorded on the marker instead.
            EntityManager.AddComponent<CrosswalkAdded>(lane);
            EntityManager.AddComponentData(lane, new CrosswalkJunction
            {
                m_Version = CrosswalkJunction.kCurrentVersion,
                m_Junction = node,
                m_Ordinal = ordinal
            });

            // No LaneSignal either. A signal is only driven for lanes in the junction's SubLane
            // buffer, and this lane is deliberately not in it — a signal here would be set once and
            // never changed again, which is worse than none at all: a crossing frozen on red that
            // people queue at forever.
            //
            // The flags are left exactly as the neighbouring crossing's. In particular the diagonal
            // is NOT marked Unsafe: BatchInstanceSystem turns that flag into RequireSafe and then
            // skips every sub-mesh carrying it, which is how the game draws no zebra on an unmarked
            // crossing — so marking it would delete the paint, which is the whole visible point of
            // this feature. Crosswalk with no LaneSignal is a state the game makes for itself at
            // every junction that has no traffic lights.

            if (source.m_HasSeed
                && EntityManager.Exists(source.m_Lane)
                && EntityManager.HasComponent<PseudoRandomSeed>(source.m_Lane)
                && !EntityManager.HasComponent<PseudoRandomSeed>(lane))
            {
                // Added, not set: the seed is not part of the archetype — LaneSystem puts it on
                // afterwards, for the lane prefabs that ask for one — so a lane created from the
                // archetype alone has no component to write to.
                EntityManager.AddComponentData(
                    lane, EntityManager.GetComponentData<PseudoRandomSeed>(source.m_Lane));
            }

            return lane;
        }

        /// <summary>
        /// Picks a crossing at this corner whose lane prefab is safe to put on a new lane.
        ///
        /// A crossing that is already standing is not proof that its prefab can carry another one.
        /// A custom crossing asset that failed to import leaves a prefab entity behind that the game
        /// no longer knows: the crossings laid before it broke keep drawing from what is already in
        /// the render batches, but a lane created against it now is a reference to something that is
        /// not there — the exact shape of the fault that corrupted a save earlier in this mod's life
        /// (see NOTES.md, "never let a saved entity point at a prefab the game does not know").
        ///
        /// So the prefab has to still resolve to a managed asset, still carry a lane archetype, and
        /// still be one the catalogue recognises, before anything is built on it.
        /// </summary>
        private bool TryPickSource(Corner a, Corner b, out Crossing source)
        {
            if (Usable(a.m_Crossing, out source) || Usable(b.m_Crossing, out source))
            {
                return true;
            }

            if (m_ReportedPrefab.Add(m_Crossings[a.m_Crossing].m_Prefab))
            {
                Mod.Log.Warn(
                    $"{Mod.ModName}: no usable crossing lane at this corner to pattern a middle "
                    + "crossing on, so none was laid there — the crossings already standing use a "
                    + "lane prefab this mod cannot safely build another lane against");
            }

            return false;
        }

        private bool Usable(int index, out Crossing source)
        {
            source = m_Crossings[index];

            Entity prefab = source.m_Prefab;

            if (prefab == Entity.Null
                || !EntityManager.Exists(prefab)
                || !EntityManager.HasComponent<NetLaneArchetypeData>(prefab)
                || !EntityManager.HasComponent<NetLaneData>(prefab)
                || !EntityManager.HasComponent<PrefabData>(prefab))
            {
                return false;
            }

            if (m_PrefabSystem == null
                || !m_PrefabSystem.TryGetPrefab<PrefabBase>(prefab, out PrefabBase managed)
                || managed == null)
            {
                return false;
            }

            CrosswalkCatalog catalog = Mod.Catalog;

            return catalog == null || catalog.IsCrossingLane(prefab);
        }

        /// <summary>
        /// Marks one junction's added lanes for deletion.
        ///
        /// Found through the marker rather than through the junction's own lane list, because these
        /// lanes are deliberately not in that list — see CrosswalkAdded.
        /// </summary>
        private void RemoveLanes(Entity node)
        {
            if (node == Entity.Null || m_AddedLaneQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            NativeArray<Entity> lanes = m_AddedLaneQuery.ToEntityArray(Allocator.Temp);

            try
            {
                for (int i = 0; i < lanes.Length; i++)
                {
                    Entity lane = lanes[i];

                    if (!EntityManager.Exists(lane)
                        || EntityManager.HasComponent<Deleted>(lane)
                        || !EntityManager.HasComponent<CrosswalkJunction>(lane)
                        || EntityManager.GetComponentData<CrosswalkJunction>(lane).m_Junction != node)
                    {
                        continue;
                    }

                    // Deleted, not DestroyEntity: the game's own clean-up takes a lane out of the
                    // structures that refer to it before destroying it.
                    EntityManager.AddComponent<Deleted>(lane);
                }
            }
            finally
            {
                lanes.Dispose();
            }

            m_LiveCount.Remove(node);
        }

        /// <summary>
        /// Marks every lane this mod laid, anywhere in the city, for deletion.
        ///
        /// Only safe as the last step of a city-wide clear, once no junction is asking for diagonals
        /// any more — it does not check whose they are. It is here to catch the ones no tagged
        /// junction owns: left by an older version, or by a junction bulldozed while its lanes lived
        /// on.
        /// </summary>
        private void RemoveAllLanes()
        {
            if (m_AddedLaneQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            EntityManager.AddComponent(m_AddedLaneQuery, ComponentType.ReadWrite<Deleted>());
        }
    }
}
