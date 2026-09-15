using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CrosswalkWidth.Systems
{
    /// <summary>
    /// Finds the crossing lane prefabs, keeps them at their authored widths, and tells
    /// CrosswalkOverrideSystem when the answer may have changed for the whole city.
    ///
    /// It used to widen the prefabs themselves. That is where the mod went wrong for several
    /// versions: a wider prefab does spread the people out, but the painted zebra is scaled by
    /// NodeLane.m_WidthOffset, which stays at zero when the prefab moves, so the crossing behaved
    /// wider without ever looking it. Width is now applied per crossing, on the lane, and this
    /// system's remaining job on the prefab side is the opposite one — putting a width back that an
    /// older version may have written into a save.
    ///
    /// Registered first in Modification1, before the net systems that run later in the same frame.
    /// </summary>
    public partial class CrosswalkWidthSystem : GameSystemBase
    {
        private static bool s_RefreshRequested;
        private static bool s_DumpRequested;
        private static bool s_PurgeRequested;
        private static bool s_ResetRequested;

        private CrosswalkCatalog m_Catalog;
        private CrosswalkLineCatalog m_LineCatalog;
        private PrefabSystem m_PrefabSystem;
        private CrosswalkOverrideSystem m_OverrideSystem;

        private CrosswalkScrambleSystem m_ScrambleSystem;

        private EntityQuery m_CrossingPieceQuery;
        private EntityQuery m_PieceLaneQuery;
        private EntityQuery m_CompositionCrosswalkQuery;
        private EntityQuery m_MarkingHostQuery;
        private EntityQuery m_NetQuery;

        private string m_AppliedSignature;

        /// <summary>
        /// What the side lines were last written as, so a change can be told from a repeat.
        ///
        /// Turning them on or off changes what the game lays at a junction, and a junction is only
        /// laid again when something asks for it — so the change has to be followed by handing the
        /// city's roads back to the pipeline. That is expensive, and doing it when nothing has
        /// actually changed would make every unrelated settings change pause the game.
        /// </summary>
        private string m_AppliedLineStyle;

        /// <summary>
        /// The width settings the side lines were last laid against.
        ///
        /// The lines are laid from the crossing's width by the game, once, when the junction is
        /// laid. So a change to the global width moves every crossing in the city and leaves every
        /// line where it was — and the only way to catch them up is to hand the roads back. Watched
        /// separately from the rest of the signature so that a setting with nothing to do with
        /// width, the tool's step size say, does not lay a city again for nothing.
        /// </summary>
        private string m_AppliedLineWidths;




        /// <summary>Re-lay the crossings of the loaded city. Called from the settings button.</summary>
        public static void RequestRefresh()
        {
            s_RefreshRequested = true;
        }

        /// <summary>List what the mod found, to the game log.</summary>
        public static void RequestDump()
        {
            s_DumpRequested = true;
        }

        /// <summary>
        /// Take everything this mod has put into the loaded city back out again, and switch the mod
        /// off so it does not immediately put it back. For uninstalling cleanly.
        /// </summary>
        public static void RequestPurge()
        {
            s_PurgeRequested = true;
        }

        /// <summary>
        /// Put every crossing in the city back exactly where and how the game lays it, and set the
        /// width back to 100%.
        ///
        /// The undo button. Unlike the purge it leaves the mod switched on, so the tool still works
        /// and a fresh start can be made from a city that looks untouched.
        /// </summary>
        public static void RequestReset()
        {
            s_ResetRequested = true;
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            m_Catalog = new CrosswalkCatalog();
            Mod.Catalog = m_Catalog;

            m_LineCatalog = new CrosswalkLineCatalog();
            Mod.LineCatalog = m_LineCatalog;

            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_Catalog.PrefabSystem = m_PrefabSystem;
            m_OverrideSystem = World.GetOrCreateSystemManaged<CrosswalkOverrideSystem>();
            m_ScrambleSystem = World.GetOrCreateSystemManaged<CrosswalkScrambleSystem>();

            // NetPieceCrosswalk puts NetCrosswalkData on the piece prefab entity, so this query is
            // exactly "every piece that declares a pedestrian crossing".
            m_CrossingPieceQuery = GetEntityQuery(ComponentType.ReadOnly<NetCrosswalkData>());

            m_PieceLaneQuery = GetEntityQuery(ComponentType.ReadOnly<NetPieceLane>());

            // Compositions carry the crossing lane the game actually lays, which is not always the
            // one the piece declared.
            m_CompositionCrosswalkQuery = GetEntityQuery(ComponentType.ReadOnly<NetCompositionCrosswalk>());

            // Every lane prefab that names a painted marking to be laid beside or across it. That
            // buffer is the only place the game's own markings can be found, since prefab names
            // live inside the .cok archives.
            m_MarkingHostQuery = GetEntityQuery(ComponentType.ReadOnly<SecondaryNetLane>());

            m_NetQuery = GetEntityQuery(new EntityQueryDesc
            {
                Any = new[]
                {
                    ComponentType.ReadOnly<Game.Net.Node>(),
                    ComponentType.ReadOnly<Game.Net.Edge>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });
        }

        /// <summary>
        /// Prefabs are loaded once, on the way to the main menu, and stay for the session. Writing
        /// the widths here means a save that loads afterwards lays its crossings at the right size
        /// in the first place, rather than being re-laid after the fact.
        /// </summary>
        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);

            DiscoverAndApply(mayRelay: false);

            // Before anything else touches the city: put right any crossing left pointing at a
            // prefab the game does not know. The pre-release versions that cloned lane prefabs
            // could leave those behind, and a save that carries one has been seen to fail on the
            // load after that — in an unrelated system, with nothing in the message to connect it
            // to crossings.
            if (mode == GameMode.Game)
            {
                try
                {
                    m_OverrideSystem.RepairLanePrefabs();
                }
                catch (System.Exception e)
                {
                    Mod.Log.Warn($"{Mod.ModName}: could not check the crossing lane prefabs: {e.Message}");
                }

            }
        }

        protected override void OnUpdate()
        {
            CrosswalkWidthSetting settings = Mod.Settings;

            if (settings == null)
            {
                return;
            }

            bool settingsChanged = settings.Signature != m_AppliedSignature;

            // Compositions are created as the player builds, not all at load, so a road type used
            // for the first time arrives with its authored crossing offsets. Watching the count is
            // enough to notice: the buffer is only ever added to.

            if (!settingsChanged && !s_RefreshRequested && !s_DumpRequested
                && !s_PurgeRequested && !s_ResetRequested)
            {
                return;
            }

            if (s_ResetRequested)
            {
                s_ResetRequested = false;
                ResetToDefaults(settings);
            }

            if (s_PurgeRequested)
            {
                s_PurgeRequested = false;

                // Switched off first. Otherwise the pass that runs a moment later sees the mod
                // still enabled and puts the widths straight back, which would make the
                // button look broken and, worse, leave data in a city the player is about to save
                // for the last time.
                settings.Enabled = false;

                // Crossings this mod laid go before the widths do. They are entities of the mod's
                // own making, and a purge that left them standing would leave the city carrying
                // exactly the thing the button promises to take out.
                int scrambles = m_ScrambleSystem.RemoveAll();
                int cleared = m_OverrideSystem.PurgeFromCity();

                // The side lines go here rather than being left to the settings change below,
                // because the whole promise of this button is that the city is clean the moment it
                // returns. The re-lay that takes them off the crossings already standing comes with
                // the settings change on the next update.
                settings.EdgeLines = false;
                m_AppliedLineStyle = null;

                if (m_LineCatalog.Restore(EntityManager) > 0)
                {
                    // The lines a crossing is already carrying were laid from the prefab when its
                    // junction last was; taking the entry out does not reach back to them.
                    RelayNets();
                }

                Mod.Log.Info(
                    $"{Mod.ModName}: removed this mod's data from the city — {cleared} junction "
                    + $"widths forgotten, {scrambles} junctions' middle crossings taken out, every "
                    + "crossing back to its authored width. Save now and the city carries nothing "
                    + "of this mod's.");
            }

            if (settingsChanged)
            {
                // Crossings laid after this point pick it up immediately. Ones already standing do
                // not, until the button below is pressed or the save is reloaded.
                DiscoverAndApply(mayRelay: true);
            }

            if (s_DumpRequested)
            {
                s_DumpRequested = false;
                Dump();
            }

            if (s_RefreshRequested)
            {
                s_RefreshRequested = false;
                RelayCrossings();
            }
        }

        protected override void OnDestroy()
        {
            // NodeLane is written into the city save, so a save made after this mod is gone must
            // not carry a crossing width nothing will maintain. (NetLaneData is not — prefab
            // entities are excluded from the save, which only records a prefab ID table — but the
            // widths are put back anyway so a session that continues without the mod is clean.)
            try
            {
                if (m_OverrideSystem != null)
                {
                    m_OverrideSystem.RestoreAuthoredWidths();
                }

                if (m_Catalog != null && m_Catalog.LaneCount > 0)
                {
                    m_Catalog.Restore(EntityManager);
                }

                // The entry naming the side lines goes too. It reaches no save either, but a
                // session that carries on without this mod should have the game's own prefabs back
                // exactly as the game built them.
                if (m_LineCatalog != null)
                {
                    m_LineCatalog.Restore(EntityManager);
                }
            }
            catch (System.Exception e)
            {
                // Teardown order is not ours to rely on; a world that is already gone is not a
                // reason to take the shutdown down with it.
                Mod.Log.Warn($"{Mod.ModName}: could not restore authored widths on shutdown: {e.Message}");
            }

            base.OnDestroy();
        }

        /// <summary>
        /// <paramref name="mayRelay"/> allows the city's roads to be handed back to the lane
        /// pipeline if the side lines have been turned on or off, which is the only way a junction
        /// already standing picks the change up.
        ///
        /// False on the load path on purpose. A city loads by laying every lane in it, and the
        /// prefabs were written at the main menu, before that — so the crossings come out of the
        /// load already right, and re-laying them would be a whole city's work for no change. It is
        /// also the worst possible moment to ask for one (see NOTES.md, "do nothing during a load").
        /// </summary>
        private void DiscoverAndApply(bool mayRelay)
        {
            CrosswalkWidthSetting settings = Mod.Settings;

            if (settings == null)
            {
                return;
            }

            Discover();

            if (m_Catalog.LaneCount == 0)
            {
                // Called before the prefabs exist, most likely. The signature is deliberately left
                // unrecorded so the next update tries again — the query is empty until the prefabs
                // are there, so retrying costs nothing.
                return;
            }

            m_AppliedSignature = settings.Signature;

            // The prefabs are put back to their authored widths and then left alone. Width is
            // applied per crossing by CrosswalkOverrideSystem, on the lane rather than the prefab,
            // because that is the only form of it the renderer scales the paint by. This call
            // undoes what earlier versions of this mod wrote into the shared prefabs during a
            // session; those writes never reached a save, but they do outlive a mod restart.
            m_Catalog.Restore(EntityManager);

            ApplyEdgeLines(settings, mayRelay);

            m_OverrideSystem.RequestFullPass();

            Mod.Log.Info(
                settings.Enabled
                    ? $"{Mod.ModName}: {settings.WidthPercentage}% over {m_Catalog.LaneCount} crossing lane "
                        + $"prefabs named by {m_Catalog.CrossingPieceCount} crossing pieces"
                    : $"{Mod.ModName}: disabled, {m_Catalog.LaneCount} crossing lanes back to authored width");
        }

        /// <summary>
        /// Puts the lines down either side of the zebra stripes in, or takes them out.
        ///
        /// The work is one entry written into each crossing lane prefab; everything after that is
        /// the game's own — see CrosswalkLineCatalog. What is left here is only deciding when a
        /// junction already standing has to be laid again to notice, which is whenever the answer
        /// has changed from what was last written.
        /// </summary>
        private void ApplyEdgeLines(CrosswalkWidthSetting settings, bool mayRelay)
        {
            bool wanted = settings.Enabled && settings.EdgeLines;
            string style = wanted ? settings.EdgeLineStyle ?? CrosswalkLineCatalog.kAutomatic : null;

            string widths =
                $"{settings.Enabled}|{settings.WidthPercentage}|{settings.MinimumWidth}|{settings.MaximumWidth}";

            bool widthsMoved = wanted && widths != m_AppliedLineWidths;

            m_AppliedLineWidths = widths;

            // This runs on every settings change, most of which have nothing to do with the lines.
            // Only a change of answer is worth a line in the log or a whole city laid again, and
            // "what was asked for" is the comparison — a failure to deliver it is reported once, at
            // the moment it is asked for, rather than on every slider afterwards.
            bool changed = style != m_AppliedLineStyle;

            if (wanted)
            {
                // PaintedLanePrefabs, not LanePrefabs. The catalogue also holds the "may not walk
                // here" substitutes, because they are laid where a crossing would be and have to be
                // sized like one — but they paint no stripes, so bordering them draws two lines
                // across a road with nothing between them.
                int written = m_LineCatalog.Apply(EntityManager, m_Catalog.PaintedLanePrefabs, style);

                if (changed)
                {
                    Mod.Log.Info(
                        written > 0
                            ? $"{Mod.ModName}: lines down either side of the zebra stripes on "
                                + $"{written} crossing lane prefabs, drawn with "
                                + m_LineCatalog.AppliedNames
                            : $"{Mod.ModName}: no marking the game lays could serve as a crossing's "
                                + "side lines, so none were added — \"List crossings in the log\" "
                                + "says what was considered");
                }
            }
            else
            {
                int cleared = m_LineCatalog.Restore(EntityManager);

                if (cleared > 0)
                {
                    Mod.Log.Info(
                        $"{Mod.ModName}: side lines taken off {cleared} crossing lane prefabs");
                }
            }

            if (!changed && !widthsMoved)
            {
                return;
            }

            m_AppliedLineStyle = style;

            // A crossing already standing has the lines the game gave it when its junction was last
            // laid, and nothing re-reads the prefab in between. This is the one change in this mod
            // that really does need the city handed back.
            if (mayRelay)
            {
                RelayNets();
            }
        }



        /// <summary>
        /// Forgets every crossing this mod has changed, puts them back where the game lays them, and
        /// returns the width to 100%.
        ///
        /// The re-lay at the end matters as much as the rest: junction geometry is computed and then
        /// kept, so a city that has been reshaped stays reshaped until something asks for it to be
        /// worked out again. Handing every node and edge back to the pipeline is what returns the
        /// roads themselves to normal, not just the paint.
        /// </summary>
        private void ResetToDefaults(CrosswalkWidthSetting settings)
        {
            int scrambles = m_ScrambleSystem.RemoveAll();
            int cleared = m_OverrideSystem.ClearAllOverrides();

            settings.WidthPercentage = 100;
            settings.MinimumWidth = 0f;
            settings.MaximumWidth = 0f;
            settings.EdgeLines = false;

            m_Catalog.Restore(EntityManager);

            // "Back where the game lays it" includes the lines down the sides, which the game does
            // not lay. The re-lay below is what takes them off the crossings already standing.
            m_LineCatalog.Restore(EntityManager);
            m_AppliedLineStyle = null;

            m_OverrideSystem.RequestFullPass();

            RelayNets();

            Mod.Log.Info(
                $"{Mod.ModName}: reset — {cleared} junction widths forgotten, {scrambles} junctions' "
                + "middle crossings taken out, every crossing back where the game lays it, width "
                + "back to 100%");
        }

        private bool RelayNets()
        {
            if (m_NetQuery.IsEmptyIgnoreFilter)
            {
                return false;   // no city yet; the debt stands until there is one
            }

            int count = m_NetQuery.CalculateEntityCount();

            EntityManager.AddComponent(m_NetQuery, ComponentType.ReadWrite<Updated>());

            Mod.Log.Info($"{Mod.ModName}: reshaping {count} junctions and roads to suit");

            return true;
        }

        private void Discover()
        {
            if (m_CrossingPieceQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            NativeArray<Entity> pieces = m_CrossingPieceQuery.ToEntityArray(Allocator.Temp);

            try
            {
                m_Catalog.Discover(EntityManager, pieces);
            }
            finally
            {
                pieces.Dispose();
            }

            if (!m_CompositionCrosswalkQuery.IsEmptyIgnoreFilter)
            {
                NativeArray<Entity> compositions = m_CompositionCrosswalkQuery.ToEntityArray(Allocator.Temp);

                try
                {
                    m_Catalog.DiscoverFromCompositions(EntityManager, compositions);
                }
                finally
                {
                    compositions.Dispose();
                }
            }

            if (!m_MarkingHostQuery.IsEmptyIgnoreFilter)
            {
                NativeArray<Entity> hosts = m_MarkingHostQuery.ToEntityArray(Allocator.Temp);

                try
                {
                    m_LineCatalog.Discover(EntityManager, m_PrefabSystem, hosts);
                }
                finally
                {
                    hosts.Dispose();
                }
            }

            if (m_PieceLaneQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            NativeArray<Entity> withLanes = m_PieceLaneQuery.ToEntityArray(Allocator.Temp);

            try
            {
                m_Catalog.NotePieceLanes(EntityManager, withLanes);
            }
            finally
            {
                withLanes.Dispose();
            }
        }

        /// <summary>
        /// Checks every crossing in the city against the current settings, and — if the side lines
        /// are on — hands the roads back so the game lays those again too.
        ///
        /// The second half is what the button is really for now. A crossing's width lives on the
        /// crossing, so a sweep is enough to put that right. The lines beside it do not: they are
        /// separate lanes that the game laid once, from the prefab as it stood at the time, and
        /// **they are saved with the city**. So a city carries whatever lines it was laid with until
        /// each junction is laid again — which is why a build that changes where the lines belong
        /// appears to do nothing on a city that already has them, while a junction the player
        /// happens to edit comes out right.
        ///
        /// That is the same trap as NOTES.md, "a lane the mod saved is a lane an old version wrote",
        /// with the twist that these lanes are the game's rather than this mod's: nothing here can
        /// find them, and nothing here should try. Handing the roads back is the whole remedy, and
        /// it belongs on a button rather than on load — a whole city re-laid without being asked for
        /// is exactly the automatic tidying that once made a save unopenable.
        /// </summary>
        private void RelayCrossings()
        {
            if (m_NetQuery.IsEmptyIgnoreFilter)
            {
                Mod.Log.Info($"{Mod.ModName}: no roads loaded, nothing to re-lay");
                return;
            }

            // A width change no longer needs the lanes laid again — the width lives on the lane
            // that is already there, so the whole job is to visit every crossing once. Re-laying
            // was the old route and it destroyed and rebuilt every lane at every junction in the
            // city to change one number on each crossing.
            m_OverrideSystem.RequestFullPass();

            CrosswalkWidthSetting settings = Mod.Settings;

            if (settings != null && settings.Enabled && settings.EdgeLines)
            {
                RelayNets();

                Mod.Log.Info(
                    $"{Mod.ModName}: re-checking every crossing in the city, and laying the lines "
                    + "down their sides again");

                return;
            }

            Mod.Log.Info($"{Mod.ModName}: re-checking every crossing in the city");
        }

        private void Dump()
        {
            foreach (string line in m_Catalog.Describe(EntityManager, NameOf))
            {
                Mod.Log.Info($"{Mod.ModName}: {line}");
            }

            // Listed whether or not the side lines are switched on. When they are and nothing is
            // drawn, this is the list to read: a marking gated behind a theme the city is not using
            // is refused by the game silently, and the style setting is how to pick another.
            foreach (string line in m_LineCatalog.Describe())
            {
                Mod.Log.Info($"{Mod.ModName}: {line}");
            }

            // And what is actually standing in the city, which is the only thing that settles a
            // crossing drawing no stripes. The prefab lists above say what the mod found; this says
            // what the game did with it.
            try
            {
                foreach (string line in m_OverrideSystem.DescribeLaidCrossings())
                {
                    Mod.Log.Info($"{Mod.ModName}: {line}");
                }
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"{Mod.ModName}: could not list the crossings in the city: {e.Message}");
            }
        }

        private string NameOf(Entity entity)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity))
            {
                return "<none>";
            }

            return m_PrefabSystem.TryGetPrefab<PrefabBase>(entity, out PrefabBase prefab) && prefab != null
                ? prefab.name
                : entity.ToString();
        }
    }
}
