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
