using System;
using System.Collections.Generic;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;

namespace CrosswalkWidth.Systems
{
    /// <summary>
    /// Lines down either side of the zebra stripes — the border a "ladder" crossing has and a plain
    /// striped one does not.
    ///
    /// Nothing here draws anything, and nothing here creates a lane. The game already lays painted
    /// lines beside lanes, and it does it entirely from prefab data: a marking lane prefab carries
    /// <see cref="SecondaryLaneData"/> saying how it is placed, and every lane prefab that wants one
    /// beside it carries a <see cref="SecondaryNetLane"/> buffer naming it.
    /// <c>SecondaryLaneSystem</c> then walks every lane at every node and edge it is re-laying, and
    /// for each one that names a marking, lays that marking along the lane's edge:
    ///
    /// <code>
    /// curve = NetUtils.OffsetCurveLeftSmooth(laneCurve, -laneWidth * 0.5f - cutOffset);
    /// </code>
    ///
    /// where <c>laneWidth</c> is read as <c>NetLaneData.m_Width + NodeLane.m_WidthOffset</c> — the
    /// same two numbers this mod's width pass writes. So the lines sit on the edge of the painted
    /// band and follow it as the band is widened or narrowed, with nothing further to keep in step.
    ///
    /// All this catalogue does, then, is add one entry to the <see cref="SecondaryNetLane"/> buffer
    /// on each crossing lane prefab, and take it out again when the setting is turned off. Prefab
    /// entities are excluded from the city save (see NOTES.md, "prefab writes do not reach a city
    /// save"), so this leaves nothing behind in a save: the lanes the game lays from it are ordinary
    /// secondary lanes owned by their junction, created and destroyed with it like every other
    /// marking in the game.
    ///
    /// Two details of the game's flag matching decide what to write, and both were read out of
    /// <c>SecondaryLaneSystem.UpdateLanes</c> rather than guessed:
    ///
    /// - Every lane offers two "corners" to the matching, one for each side, and the side is
    ///   expressed as <see cref="SecondaryNetLaneFlags.Left"/> or
    ///   <see cref="SecondaryNetLaneFlags.Right"/>. An entry carrying both is matched by both, which
    ///   is how one entry becomes a line on either side.
    /// - <see cref="SecondaryNetLaneFlags.OneSided"/> is added to what a corner *requires* when the
    ///   lane has no neighbour alongside it, and to what it *forbids* when it has. A crossing runs
    ///   across the road with nothing beside it, so the entry must carry OneSided — and carrying it
    ///   also means that on the rare crossing that does find a neighbour, the game draws the shared
    ///   marking it would draw between any two lanes instead of two lines butted together.
    /// </summary>
    internal sealed class CrosswalkLineCatalog
    {
        /// <summary>The value the style setting holds when the mod is to choose for itself.</summary>
        public const string kAutomatic = "auto";

        /// <summary>
        /// The flags one entry is written with: matched by the corner on either side of the
        /// crossing, only where the crossing has no lane alongside it, and only where the crossing
        /// is one the game itself paints.
        ///
        /// <see cref="SecondaryNetLaneFlags.RequireSafe"/> is the last of those, and it is what
        /// keeps the lines off the crossings nobody can see. `LaneSystem` lays a crossing lane at
        /// every junction, whether or not the road asked for a painted crossing:
        ///
        /// <code>
        /// if (isCrosswalk)
        /// {
        ///     component3.m_Flags |= PedestrianLaneFlags.Crosswalk;
        ///     if ((startCompositionData.m_Flags.m_General &amp; CompositionFlags.General.Crosswalk) == 0)
        ///     {
        ///         component3.m_Flags |= PedestrianLaneFlags.Unsafe;
        ///         hasSignals = false;
        ///     }
        /// </code>
        ///
        /// `BatchInstanceSystem` skips every sub-mesh marked `RequireSafe` on an `Unsafe` lane,
        /// which is how an unmarked crossing draws no zebra at all — so without this flag the mod
        /// draws two lines across a road with nothing between them. `SecondaryLaneSystem` puts
        /// `RequireSafe` into what a corner *forbids* when its lane is unsafe, so an entry carrying
        /// it is matched only where the stripes are really painted.
        /// </summary>
        private const SecondaryNetLaneFlags kEntryFlags =
            SecondaryNetLaneFlags.Left | SecondaryNetLaneFlags.Right
            | SecondaryNetLaneFlags.OneSided | SecondaryNetLaneFlags.RequireSafe;

        /// <summary>A marking lane prefab the game already uses, and might be borrowed for this.</summary>
        internal sealed class Candidate
        {
            public Entity m_Prefab;
            public string m_Name;

            /// <summary>The line's thickness: a marking lane's width is across the line it draws.</summary>
            public float m_Width;

            /// <summary>How well the game's own use of it matches this one. See <see cref="RoleScore"/>.</summary>
            public int m_Score;

            /// <summary>Every way the game itself asks for this marking, for the log.</summary>
            public SecondaryNetLaneFlags m_Roles;

            /// <summary>
            /// The themes, road families and so on this marking is gated behind.
            ///
            /// <c>SecondaryLaneSystem.CheckRequirements</c> refuses a marking whose requirements the
            /// city does not meet, so a marking gated behind the wrong theme is laid nowhere at all
            /// — silently. Empty means it is always allowed.
            /// </summary>
            public readonly HashSet<Entity> m_Requirements = new HashSet<Entity>();

            public bool Themed => m_Requirements.Count != 0;
        }

        private readonly List<Candidate> m_Candidates = new List<Candidate>();
        private readonly Dictionary<Entity, Candidate> m_ByPrefab = new Dictionary<Entity, Candidate>();

        /// <summary>
        /// Crossing lane prefabs an entry has been written into, with the length their buffer had
        /// before the write.
        ///
        /// The length rather than the entry, because taking it out again has to be exact: nothing
        /// else appends to these buffers after <c>NetInitializeSystem</c> has built them, so
        /// trimming back to the recorded length restores the prefab to what the game made.
        /// </summary>
        private readonly Dictionary<Entity, int> m_Written = new Dictionary<Entity, int>();

        private readonly HashSet<Entity> m_Requirements = new HashSet<Entity>();

        /// <summary>Crossing lane prefabs already named in the log as unusable, so each is named once.</summary>
        private readonly HashSet<Entity> m_Reported = new HashSet<Entity>();

        public IReadOnlyList<Candidate> Candidates => m_Candidates;

        /// <summary>How many crossing lane prefabs currently carry an entry.</summary>
        public int WrittenCount => m_Written.Count;

        /// <summary>The names of the markings last written, for the log. Empty when none are.</summary>
        public string AppliedNames { get; private set; } = string.Empty;

        /// <summary>
        /// Finds every marking lane prefab the game lays beside or across a lane, and judges which
        /// of them would serve as a crossing's border.
        ///
        /// Structural, with no name matching, for the same reason the crossing search is: prefab
        /// names live inside the game's <c>.cok</c> archives, and a modded road's marking has a name
        /// nobody here can predict. Every marking in the game is reachable by walking the
        /// <see cref="SecondaryNetLane"/> buffers, because that is the only way the game itself
        /// finds them.
        /// </summary>
        public void Discover(EntityManager em, PrefabSystem prefabSystem, NativeArray<Entity> hosts)
        {
            m_Candidates.Clear();
            m_ByPrefab.Clear();

            for (int i = 0; i < hosts.Length; i++)
            {
                Entity host = hosts[i];

                if (!em.HasBuffer<SecondaryNetLane>(host))
                {
                    continue;
                }

                DynamicBuffer<SecondaryNetLane> entries = em.GetBuffer<SecondaryNetLane>(host, true);

                for (int j = 0; j < entries.Length; j++)
                {
                    Consider(em, prefabSystem, entries[j].m_Lane, entries[j].m_Flags);
                }
            }

            m_Candidates.Sort(CompareCandidates);
        }

        /// <summary>
        /// Judges one marking and records it if it could serve.
        ///
        /// Three of the four refusals are about what the game would do with it rather than how it
        /// looks, and each of them is a way this feature would fail invisibly:
        ///
        /// - Without <see cref="SecondaryLaneData"/> the marking is not a marking at all, and
        ///   <c>SecondaryLaneSystem</c> reads that component through an unguarded lookup inside a
        ///   Burst job. Naming one that has not got it takes the process down with no stacktrace.
        /// - A non-zero <see cref="SecondaryLaneData.m_Flags"/> means the marking is cut short
        ///   wherever the lane it borders overlaps traffic. That is right for a lane divider and
        ///   ruinous here: a crossing overlaps every car lane it crosses, so the line would be cut
        ///   away to almost nothing.
        /// - A non-zero <see cref="SecondaryLaneData.m_Spacing"/> means the marking is not a line
        ///   along the lane but a row of pieces laid *between* two lanes, at intervals. With only
        ///   one lane there is no second curve to lay them between.
        /// </summary>
        private void Consider(
            EntityManager em, PrefabSystem prefabSystem, Entity lane, SecondaryNetLaneFlags flags)
        {
            if (lane == Entity.Null || !em.Exists(lane))
            {
                return;
            }

            if (m_ByPrefab.TryGetValue(lane, out Candidate known))
            {
                known.m_Roles |= flags;
                known.m_Score = Math.Max(known.m_Score, RoleScore(flags));
                return;
            }

            if (!em.HasComponent<SecondaryLaneData>(lane)
                || !em.HasComponent<NetLaneData>(lane)
                || !em.HasComponent<NetLaneArchetypeData>(lane)
                || !em.HasComponent<PrefabData>(lane))
            {
                return;
            }

            SecondaryLaneData placement = em.GetComponentData<SecondaryLaneData>(lane);

            if (placement.m_Flags != 0 || placement.m_Spacing > 0.1f)
            {
                return;
            }

            NetLaneData laneData = em.GetComponentData<NetLaneData>(lane);

            if ((laneData.m_Flags & LaneFlags.Secondary) == 0)
            {
                return;
            }

            Candidate candidate = new Candidate
            {
                m_Prefab = lane,
                m_Width = laneData.m_Width,
                m_Roles = flags,
                m_Score = RoleScore(flags),
                m_Name = prefabSystem != null
                    && prefabSystem.TryGetPrefab<PrefabBase>(lane, out PrefabBase managed)
                    && managed != null
                        ? managed.name
                        : lane.ToString()
            };

            if (em.HasBuffer<ObjectRequirementElement>(lane))
            {
                DynamicBuffer<ObjectRequirementElement> requirements =
                    em.GetBuffer<ObjectRequirementElement>(lane, true);

                for (int i = 0; i < requirements.Length; i++)
                {
                    if (requirements[i].m_Requirement != Entity.Null)
                    {
                        candidate.m_Requirements.Add(requirements[i].m_Requirement);
                    }
                }
            }

            m_ByPrefab.Add(lane, candidate);
            m_Candidates.Add(candidate);
        }

        /// <summary>
        /// Whether the game's own use of a marking says it is an unbroken line.
        ///
        /// Solid or broken is a property of the mesh, which cannot be read from here — but the game
        /// says it another way, in the flags that decide where a marking is allowed:
        ///
        /// - `RequireForbidPassing` is a lane line drawn only where passing is forbidden, which is
        ///   what a solid line means on a road. Its opposite, `RequireAllowPassing`, is the broken
        ///   one. These are the thin lines, and a thin line is what a crossing's border wants.
        /// - `Crossing | RequireStop` is a stop line: solid, but authored as a bar to be seen from a
        ///   car, so several times thicker than a border should be.
        /// - `RequireYield` is a give-way line, which is broken into triangles or squares.
        ///
        /// Everything unmarked by any of those is an unknown, ranked between the two.
        /// </summary>
        private static int RoleScore(SecondaryNetLaneFlags flags)
        {
            if ((flags & SecondaryNetLaneFlags.RequireForbidPassing) != 0
                || (flags & (SecondaryNetLaneFlags.Crossing | SecondaryNetLaneFlags.RequireStop))
                    == (SecondaryNetLaneFlags.Crossing | SecondaryNetLaneFlags.RequireStop))
            {
                return 2;   // known solid
            }

            if ((flags & (SecondaryNetLaneFlags.RequireYield | SecondaryNetLaneFlags.RequireAllowPassing)) != 0)
            {
                return 0;   // known broken
            }

            return 1;
        }

        /// <summary>
        /// Best first: a line known to be solid, then the thinnest, then by name so it is stable.
        ///
        /// Thinnest within the rank rather than across it, because a broken line is wrong at any
        /// thickness. Within the solid ones thinness is the whole question: a stop line is authored
        /// to be read from a car and is several times the width a crossing's border should be, and
        /// on a crossing it reads as a second band of paint rather than an edge.
        /// </summary>
        private static int CompareCandidates(Candidate a, Candidate b)
        {
            if (a.m_Score != b.m_Score)
            {
                return b.m_Score.CompareTo(a.m_Score);
            }

            if (Math.Abs(a.m_Width - b.m_Width) > 0.001f)
            {
                return a.m_Width.CompareTo(b.m_Width);
            }

            return string.CompareOrdinal(a.m_Name, b.m_Name);
        }

        /// <summary>
        /// Writes an entry into every crossing lane prefab, so the game lays a line down either side
        /// of every crossing. Returns how many prefabs were written to.
        ///
        /// Idempotent: whatever was written before is taken out first, so changing the style or
        /// re-running a discovery pass cannot leave two markings stacked on one crossing.
        /// </summary>
        public int Apply(EntityManager em, IEnumerable<Entity> crossingLanes, string preferred)
        {
            Restore(em);

            if (m_Candidates.Count == 0 || crossingLanes == null)
            {
                return 0;
            }

            Candidate pinned = Named(preferred);
            HashSet<string> used = new HashSet<string>();

            foreach (Entity crossing in crossingLanes)
            {
                if (crossing == Entity.Null || !em.Exists(crossing))
                {
                    continue;
                }

                if (!em.HasBuffer<SecondaryNetLane>(crossing))
                {
                    // Only a lane prefab declaring its own SecondaryLane component is built without
                    // this buffer, and a crossing lane is not one. Adding it would be a structural
                    // change to a prefab the game built, which is not worth the risk for a line.
                    if (m_Reported.Add(crossing))
                    {
                        Mod.Log.Info(
                            $"{Mod.ModName}: a crossing lane prefab has no marking list, so it gets "
                            + "no lines down its sides");
                    }

                    continue;
                }

                Candidate choice = pinned ?? PickFor(em, crossing);

                if (choice == null)
                {
                    continue;
                }

                DynamicBuffer<SecondaryNetLane> entries = em.GetBuffer<SecondaryNetLane>(crossing);

                m_Written[crossing] = entries.Length;

                entries.Add(new SecondaryNetLane
                {
                    m_Lane = choice.m_Prefab,
                    m_Flags = kEntryFlags
                });

                used.Add(choice.m_Name);
            }

            AppliedNames = string.Join(", ", new List<string>(used).ToArray());

            return m_Written.Count;
        }

        /// <summary>Takes every entry this catalogue wrote back out. Returns how many prefabs were cleared.</summary>
        public int Restore(EntityManager em)
        {
            int cleared = 0;

            foreach (KeyValuePair<Entity, int> entry in m_Written)
            {
                Entity crossing = entry.Key;

                if (!em.Exists(crossing) || !em.HasBuffer<SecondaryNetLane>(crossing))
                {
                    continue;
                }

                DynamicBuffer<SecondaryNetLane> entries = em.GetBuffer<SecondaryNetLane>(crossing);

                if (entries.Length > entry.Value)
                {
                    entries.RemoveRange(entry.Value, entries.Length - entry.Value);
                    cleared++;
                }
            }

            m_Written.Clear();
            AppliedNames = string.Empty;

            return cleared;
        }

        /// <summary>
        /// The marking to border one crossing lane prefab with, or <c>Entity.Null</c> if there is
        /// none to be had.
        ///
        /// For the middle crossings, which the game's own marking system cannot reach — they are in
        /// no junction's lane list, which is the whole reason they are safe — so the mod has to lay
        /// their lines itself and needs the same answer this catalogue gives everywhere else.
        /// </summary>
        public Entity Choose(EntityManager em, Entity crossingPrefab, string preferred)
        {
            if (m_Candidates.Count == 0)
            {
                return Entity.Null;
            }

            Candidate choice = Named(preferred);

            if (choice == null && crossingPrefab != Entity.Null && em.Exists(crossingPrefab))
            {
                choice = PickFor(em, crossingPrefab);
            }

            return choice != null ? choice.m_Prefab : Entity.Null;
        }

        /// <summary>The candidate with this name, or null for "automatic" and for a name that has gone.</summary>
        public Candidate Named(string name)
        {
            if (string.IsNullOrEmpty(name) || name == kAutomatic)
            {
                return null;
            }

            for (int i = 0; i < m_Candidates.Count; i++)
            {
                if (string.Equals(m_Candidates[i].m_Name, name, StringComparison.Ordinal))
                {
                    return m_Candidates[i];
                }
            }

            return null;
        }

        /// <summary>
        /// The best marking for one crossing lane prefab.
        ///
        /// Theme is the whole reason this is per crossing rather than one choice for the city. A
        /// crossing lane is itself a themed variant — a North American city lays "NA Crosswalk
        /// Lane 2" where the road declared "Crosswalk Lane 2" — and markings are gated the same way,
        /// by <see cref="ObjectRequirementElement"/>. A marking gated behind a theme the city is not
        /// using is refused by <c>CheckRequirements</c> and laid nowhere, with nothing said about
        /// it. So the first choice is a marking that is gated behind something this crossing is
        /// gated behind too: whichever theme lays this crossing lays that marking with it.
        ///
        /// Failing that, a marking with no requirements at all, which is always allowed. Failing
        /// that, the best one there is, on the grounds that a line that might not appear is worth
        /// more than certainly none.
        /// </summary>
        private Candidate PickFor(EntityManager em, Entity crossing)
        {
            m_Requirements.Clear();

            if (em.HasBuffer<ObjectRequirementElement>(crossing))
            {
                DynamicBuffer<ObjectRequirementElement> requirements =
                    em.GetBuffer<ObjectRequirementElement>(crossing, true);

                for (int i = 0; i < requirements.Length; i++)
                {
                    if (requirements[i].m_Requirement != Entity.Null)
                    {
                        m_Requirements.Add(requirements[i].m_Requirement);
                    }
                }
            }

            if (m_Requirements.Count != 0)
            {
                for (int i = 0; i < m_Candidates.Count; i++)
                {
                    if (Shares(m_Candidates[i]))
                    {
                        return m_Candidates[i];
                    }
                }
            }

            for (int i = 0; i < m_Candidates.Count; i++)
            {
                if (!m_Candidates[i].Themed)
                {
                    return m_Candidates[i];
                }
            }

            return m_Candidates[0];
        }

        private bool Shares(Candidate candidate)
        {
            foreach (Entity requirement in candidate.m_Requirements)
            {
                if (m_Requirements.Contains(requirement))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>What the mod found to draw lines with, as lines for the game log.</summary>
        public IEnumerable<string> Describe()
        {
            if (m_Candidates.Count == 0)
            {
                yield return "no marking lane prefabs found, so crossings can have no lines down "
                    + "their sides";
                yield break;
            }

            yield return $"{m_Candidates.Count} markings could serve as a crossing's side lines, "
                + $"currently written into {m_Written.Count} crossing lane prefabs"
                + (string.IsNullOrEmpty(AppliedNames) ? string.Empty : $" as {AppliedNames}");

            for (int i = 0; i < m_Candidates.Count && i < 20; i++)
            {
                Candidate candidate = m_Candidates[i];

                yield return $"  {candidate.m_Name}: {candidate.m_Width:0.00}m thick, "
                    + $"rank {candidate.m_Score}, used by the game as {candidate.m_Roles}"
                    + (candidate.Themed
                        ? $", gated behind {candidate.m_Requirements.Count} requirement(s)"
                        : ", never gated");
            }

            if (m_Candidates.Count > 20)
            {
                yield return $"  ... and {m_Candidates.Count - 20} more";
            }
        }
    }
}
