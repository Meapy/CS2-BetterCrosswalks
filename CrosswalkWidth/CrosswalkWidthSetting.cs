using Colossal.IO.AssetDatabase;
using CrosswalkWidth.Systems;
using Game.Modding;
using Game.Settings;

namespace CrosswalkWidth
{
    /// <summary>
    /// Mod settings.
    ///
    /// Width is a percentage of whatever the asset author chose rather than an absolute figure,
    /// because crossings are not all the same to begin with — a side street's is narrow, a
    /// boulevard's is not — and one absolute value would flatten that. The floor and ceiling are
    /// there for the cases where a proportion is not what you want: "no crossing narrower than 4m"
    /// is a sensible rule, "every crossing exactly 4m" usually is not.
    /// </summary>
    [FileLocation("ModsSettings/CrosswalkWidth/CrosswalkWidth")]
    [SettingsUIGroupOrder(GroupWidth, GroupTool, GroupApply)]
    [SettingsUIShowGroupName(GroupWidth, GroupTool, GroupApply)]
    public sealed class CrosswalkWidthSetting : ModSetting
    {
        public const string SectionMain = "Main";

        public const string GroupWidth = "Width";
        public const string GroupTool = "Tool";
        public const string GroupApply = "Apply";

        public CrosswalkWidthSetting(IMod mod) : base(mod)
        {
        }

        // ---------------------------------------------------------------- width

        /// <summary>Master switch. Off restores every authored width.</summary>
        [SettingsUISection(SectionMain, GroupWidth)]
        public bool Enabled { get; set; } = true;

        /// <summary>Crossing width as a percentage of the asset's own.</summary>
        [SettingsUISlider(min = 25f, max = 400f, step = 5f, unit = "percentage")]
        [SettingsUISection(SectionMain, GroupWidth)]
        public int WidthPercentage { get; set; } = 200;

        /// <summary>Floor in metres, applied after the percentage. 0 disables it.</summary>
        [SettingsUISlider(min = 0f, max = 12f, step = 0.5f, unit = "floatSingleFraction")]
        [SettingsUISection(SectionMain, GroupWidth)]
        public float MinimumWidth { get; set; } = 0f;

        /// <summary>Ceiling in metres, applied after the percentage. 0 disables it.</summary>
        [SettingsUISlider(min = 0f, max = 20f, step = 0.5f, unit = "floatSingleFraction")]
        [SettingsUISection(SectionMain, GroupWidth)]
        public float MaximumWidth { get; set; } = 0f;

        // ---------------------------------------------------------------- per junction

        /// <summary>
        /// Starts the per-junction tool.
        ///
        /// Close the options panel afterwards; the tool is already running. Point at a junction,
        /// Page Up and Page Down change that junction's crossings, left click puts it back on the
        /// global width, right click leaves the tool.
        /// </summary>
        [SettingsUIButton]
        [SettingsUISection(SectionMain, GroupTool)]
        public bool StartCrosswalkTool
        {
            set
            {
                CrosswalkWidthSystem.RequestActivateTool();
            }
        }

        /// <summary>How much one Page Up or Page Down changes a junction.</summary>
        [SettingsUISlider(min = 5f, max = 100f, step = 5f, unit = "percentage")]
        [SettingsUISection(SectionMain, GroupTool)]
        public int ToolStepPercentage { get; set; } = 25;

        /// <summary>Forgets every per-junction width in the city.</summary>
        [SettingsUIButton]
        [SettingsUISection(SectionMain, GroupTool)]
        public bool ClearPerJunctionWidths
        {
            set
            {
                CrosswalkWidthSystem.RequestClearOverrides();
            }
        }

        // ---------------------------------------------------------------- apply

        /// <summary>Re-lays every crossing in the city at the current width.</summary>
        [SettingsUIButton]
        [SettingsUISection(SectionMain, GroupApply)]
        public bool ApplyToExistingCrossings
        {
            set
            {
                CrosswalkWidthSystem.RequestRefresh();
            }
        }

        /// <summary>Writes what the mod found to the game log.</summary>
        [SettingsUIButton]
        [SettingsUISection(SectionMain, GroupApply)]
        public bool LogDiscoveredCrossings
        {
            set
            {
                CrosswalkWidthSystem.RequestDump();
            }
        }

        /// <summary>
        /// One string covering every value that changes the geometry, so the system can tell in a
        /// single comparison whether it needs to write to the prefabs again.
        /// </summary>
        public string Signature => $"{Enabled}|{WidthPercentage}|{MinimumWidth}|{MaximumWidth}";

        public override void SetDefaults()
        {
            Enabled = true;
            WidthPercentage = 200;
            MinimumWidth = 0f;
            MaximumWidth = 0f;
            ToolStepPercentage = 25;
        }
    }
}
