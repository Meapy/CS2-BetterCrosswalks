using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using CrosswalkWidth.Systems;
using Game;
using Game.Modding;
using Game.SceneFlow;

namespace CrosswalkWidth
{
    /// <summary>
    /// Entry point. See NOTES.md for the traced game behaviour each decision rests on.
    /// </summary>
    public sealed class Mod : IMod
    {
        public const string ModName = "CrosswalkWidth";

        public static readonly ILog Log = LogManager.GetLogger(ModName);

        public static CrosswalkWidthSetting Settings { get; private set; }

        /// <summary>
        /// The crossing lanes found at load. Shared because the per-junction systems need the
        /// authored widths, and there is exactly one catalogue per session.
        /// </summary>
        internal static Systems.CrosswalkCatalog Catalog { get; set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            Log.Info($"{ModName}: OnLoad");

            Settings = new CrosswalkWidthSetting(this);
            Settings.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(Settings));

            AssetDatabase.global.LoadSettings(ModName, Settings, new CrosswalkWidthSetting(this));

            // First in Modification1, so anything this system tags is picked up by the net systems
            // later in the same frame's Modification phases and cleaned up at the end of it.
            updateSystem.UpdateBefore<CrosswalkWidthSystem>(SystemUpdatePhase.Modification1);

            // After LaneSystem, in its own phase. LaneSystem writes each crossing lane's PrefabRef
            // from the composition whenever it re-lays a node, so a per-junction override has to be
            // applied after it or it is overwritten within the frame.
            updateSystem.UpdateAfter<CrosswalkOverrideSystem, Game.Net.LaneSystem>(
                SystemUpdatePhase.Modification4);

            // Tools live in their own phase, alongside the game's.
            updateSystem.UpdateAt<CrosswalkPickerToolSystem>(SystemUpdatePhase.ToolUpdate);

            // Publishes the toolbar button's state and takes its clicks.
            updateSystem.UpdateAt<CrosswalkToolUISystem>(SystemUpdatePhase.UIUpdate);

            Log.Info($"{ModName}: system registered");
        }

        public void OnDispose()
        {
            Log.Info($"{ModName}: OnDispose");

            if (Settings != null)
            {
                Settings.UnregisterInOptionsUI();
                Settings = null;
            }
        }
    }
}
