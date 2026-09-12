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
        private PrefabSystem m_PrefabSystem;
        private CrosswalkOverrideSystem m_OverrideSystem;

        private CrosswalkScrambleSystem m_ScrambleSystem;

        private EntityQuery m_CrossingPieceQuery;
        private EntityQuery m_PieceLaneQuery;
        private EntityQuery m_CompositionCrosswalkQuery;
        private EntityQuery m_NetQuery;

        private string m_AppliedSignature;




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

            DiscoverAndApply();

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
                DiscoverAndApply();
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
            }
            catch (System.Exception e)
            {
                // Teardown order is not ours to rely on; a world that is already gone is not a
                // reason to take the shutdown down with it.
                Mod.Log.Warn($"{Mod.ModName}: could not restore authored widths on shutdown: {e.Message}");
            }

            base.OnDestroy();
        }

        private void DiscoverAndApply()
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

            m_OverrideSystem.RequestFullPass();

            Mod.Log.Info(
                settings.Enabled
                    ? $"{Mod.ModName}: {settings.WidthPercentage}% over {m_Catalog.LaneCount} crossing lane "
                        + $"prefabs named by {m_Catalog.CrossingPieceCount} crossing pieces"
                    : $"{Mod.ModName}: disabled, {m_Catalog.LaneCount} crossing lanes back to authored width");
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

            m_Catalog.Restore(EntityManager);
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
        /// Checks every crossing in the city against the current settings.
        ///
        /// The name is left over from when this did hand every node and edge back to the lane
        /// pipeline. It does not any more, and the button it sits behind is close to redundant: a
        /// settings change already requests this same sweep. It stays because a sweep on demand
        /// costs nothing and is the obvious thing to reach for if a crossing is ever missed.
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

            Mod.Log.Info($"{Mod.ModName}: re-checking every crossing in the city");
        }

        private void Dump()
        {
            foreach (string line in m_Catalog.Describe(EntityManager, NameOf))
            {
                Mod.Log.Info($"{Mod.ModName}: {line}");
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
