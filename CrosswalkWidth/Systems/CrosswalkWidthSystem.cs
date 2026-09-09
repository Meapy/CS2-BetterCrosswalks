using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

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
        private static bool s_ActivateToolRequested;
        private static bool s_ClearOverridesRequested;

        private CrosswalkCatalog m_Catalog;
        private PrefabSystem m_PrefabSystem;
        private Game.Tools.ToolSystem m_ToolSystem;
        private CrosswalkPickerToolSystem m_PickerTool;
        private CrosswalkOverrideSystem m_OverrideSystem;

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
        /// Start the per-junction tool. Requested rather than done directly because the settings
        /// panel runs on the UI thread and switching the active tool is simulation-side work.
        /// </summary>
        public static void RequestActivateTool()
        {
            s_ActivateToolRequested = true;
        }

        /// <summary>Forget every per-junction width. Called from the settings button.</summary>
        public static void RequestClearOverrides()
        {
            s_ClearOverridesRequested = true;
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            m_Catalog = new CrosswalkCatalog();
            Mod.Catalog = m_Catalog;

            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_Catalog.PrefabSystem = m_PrefabSystem;
            m_ToolSystem = World.GetOrCreateSystemManaged<Game.Tools.ToolSystem>();
            m_PickerTool = World.GetOrCreateSystemManaged<CrosswalkPickerToolSystem>();
            m_OverrideSystem = World.GetOrCreateSystemManaged<CrosswalkOverrideSystem>();

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
        }

        protected override void OnUpdate()
        {
            CrosswalkWidthSetting settings = Mod.Settings;

            if (settings == null)
            {
                return;
            }

            bool settingsChanged = settings.Signature != m_AppliedSignature;

            if (!settingsChanged && !s_RefreshRequested && !s_DumpRequested
                && !s_ActivateToolRequested && !s_ClearOverridesRequested)
            {
                return;
            }

            if (s_ActivateToolRequested)
            {
                s_ActivateToolRequested = false;
                m_ToolSystem.activeTool = m_PickerTool;
                Mod.Log.Info($"{Mod.ModName}: per-junction tool active");
            }

            if (s_ClearOverridesRequested)
            {
                s_ClearOverridesRequested = false;
                int cleared = m_OverrideSystem.ClearAllOverrides();
                Mod.Log.Info($"{Mod.ModName}: cleared {cleared} per-junction widths");
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
            // NodeLane and NetLaneData are both serialized, so a save written after this mod is
            // removed must not carry a crossing width nothing will maintain.
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
            // because that is the only form of it the renderer scales the paint by. This call is
            // the repair: a save made while an earlier version was widening the shared prefabs
            // carries those figures, and would otherwise be scaled a second time.
            m_Catalog.Restore(EntityManager);

            m_OverrideSystem.RequestFullPass();

            Mod.Log.Info(
                settings.Enabled
                    ? $"{Mod.ModName}: {settings.WidthPercentage}% over {m_Catalog.LaneCount} crossing lane "
                        + $"prefabs named by {m_Catalog.CrossingPieceCount} crossing pieces"
                    : $"{Mod.ModName}: disabled, {m_Catalog.LaneCount} crossing lanes back to authored width");
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
        /// Hands every node and edge back to the game's lane pipeline so its crossings are laid
        /// again at the new width.
        ///
        /// Nothing about the composition changes — crossing width lives on the lane prefab, not in
        /// NetCompositionCrosswalk, which carries only the span and the lane reference. So unlike
        /// widening a road's cross-section, this needs no composition rebuild and cannot leave a
        /// composition half-updated.
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
