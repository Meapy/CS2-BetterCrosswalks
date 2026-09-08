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
    /// Applies the configured crossing width to the crossing lane prefabs, and — on request —
    /// re-lays the crossings of a city that is already built.
    ///
    /// The prefab half is cheap and safe. A lane's width is read when the lane is laid, so writing
    /// it before anything is laid means every crossing built afterwards is right, with no further
    /// work. That is what OnGameLoadingComplete does, and why changing the setting and reloading
    /// always works.
    ///
    /// The city half is one structural change: nodes and edges already standing hold lanes that
    /// were laid at the old width, and LaneSystem only revisits a node when something marks it
    /// updated. So the "apply now" path tags every node and edge and lets the game's own lane
    /// pipeline lay the crossings again.
    ///
    /// Registered first in Modification1 so the tag is in place before the net systems run in the
    /// Modification phases of the same frame. CleanUpSystem strips it at the end of that frame —
    /// PrepareCleanUpSystem runs last in MainLoop, after the Modification phases nested inside it —
    /// so each tagged entity is processed exactly once.
    /// </summary>
    public partial class CrosswalkWidthSystem : GameSystemBase
    {
        private static bool s_RefreshRequested;
        private static bool s_DumpRequested;

        private CrosswalkCatalog m_Catalog;
        private PrefabSystem m_PrefabSystem;

        private EntityQuery m_CrossingPieceQuery;
        private EntityQuery m_PieceLaneQuery;
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

        protected override void OnCreate()
        {
            base.OnCreate();

            m_Catalog = new CrosswalkCatalog();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            // NetPieceCrosswalk puts NetCrosswalkData on the piece prefab entity, so this query is
            // exactly "every piece that declares a pedestrian crossing".
            m_CrossingPieceQuery = GetEntityQuery(ComponentType.ReadOnly<NetCrosswalkData>());

            m_PieceLaneQuery = GetEntityQuery(ComponentType.ReadOnly<NetPieceLane>());

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

            if (!settingsChanged && !s_RefreshRequested && !s_DumpRequested)
            {
                return;
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
            // NetLaneData is serialized, so a save written after this mod is removed must not carry
            // a scaled crossing width.
            try
            {
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

            if (!settings.Enabled)
            {
                m_Catalog.Restore(EntityManager);
                Mod.Log.Info($"{Mod.ModName}: disabled, {m_Catalog.LaneCount} crossing lanes restored");
                return;
            }

            m_Catalog.Apply(
                EntityManager,
                settings.WidthPercentage / 100f,
                settings.MinimumWidth,
                settings.MaximumWidth);

            Mod.Log.Info(
                $"{Mod.ModName}: applied {settings.WidthPercentage}% to {m_Catalog.LaneCount} crossing lane prefabs "
                + $"named by {m_Catalog.CrossingPieceCount} crossing pieces");
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

            int count = m_NetQuery.CalculateEntityCount();

            EntityManager.AddComponent(m_NetQuery, ComponentType.ReadWrite<Updated>());

            Mod.Log.Info($"{Mod.ModName}: re-laid {count} net segments and nodes");
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
