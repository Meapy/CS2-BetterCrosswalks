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

        /// <summary>
        /// The markings found that could draw a line down either side of a crossing.
        ///
        /// Shared for the same reason the crossing catalogue is, and for one more: the settings
        /// dropdown that lets the player pick between them is a static method on the settings class,
        /// with no route to a system.
        /// </summary>
        internal static Systems.CrosswalkLineCatalog LineCatalog { get; set; }

        /// <summary>The assembly version, so a log or a bug report says which build it came from.</summary>
        public static string Version =>
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        public void OnLoad(UpdateSystem updateSystem)
        {
            Log.Info($"{ModName}: OnLoad, version {Version}");

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

            // After the width pass, still in Modification4 and so still ahead of Modification4B —
            // where the game adds new lanes to their junction's SubLane buffer, works out what they
            // overlap and gives them a traffic light group. A diagonal laid here is picked up by
            // all three later in the same frame.
            updateSystem.UpdateAfter<CrosswalkScrambleSystem, CrosswalkOverrideSystem>(
                SystemUpdatePhase.Modification4);

            // And a second time, immediately before SecondaryLaneSystem, which is what lays the
            // lines down a crossing's sides.
            //
            // The width pass has to have run before it or the lines come out at the width the
            // crossing was laid at rather than the width this mod wants. In Modification4 it cannot
            // have: LaneSystem writes its new lanes through ModificationBarrier4, which plays back
            // at the *end* of that phase, so a junction's fresh crossings do not exist yet when the
            // pass above runs — it picks them up a frame later, by which time the lines are laid and
            // the junction is no longer tagged for the game to lay them again.
            //
            // Here they do exist, LaneReferencesSystem has already put them in their junction's lane
            // list, and nothing has drawn anything yet. The pass costs nothing when there is no work
            // — it collects an empty list and returns — and every write it makes is a comparison
            // first, so running it twice in a frame cannot do anything twice.
            updateSystem.UpdateBefore<CrosswalkOverrideSystem, Game.Net.SecondaryLaneSystem>(
                SystemUpdatePhase.Modification4B);

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
