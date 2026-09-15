using System.Collections.Generic;
using Colossal.IO.AssetDatabase;
using CrosswalkWidth.Systems;
using Game.Modding;
using Game.Settings;
using Game.UI.Localization;
using Game.UI.Widgets;

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
    [SettingsUIGroupOrder(GroupWidth, GroupMaintenance)]
    [SettingsUIShowGroupName(GroupWidth, GroupMaintenance)]
    public sealed class CrosswalkWidthSetting : ModSetting
    {
        public const string SectionMain = "Main";

        public const string GroupWidth = "Width";
        public const string GroupMaintenance = "Maintenance";

        public CrosswalkWidthSetting(IMod mod) : base(mod)
        {
        }

        // ---------------------------------------------------------------- width

        /// <summary>Master switch. Off restores every authored width.</summary>
        [SettingsUISection(SectionMain, GroupWidth)]
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Crossing width as a percentage of the asset's own.
        ///
        /// 150 out of the box. Half as wide again is enough to see the difference on a junction and
        /// to let people cross two abreast, while still sitting inside the paving the game laid —
        /// which a bolder default does not, on the narrower roads.
        /// </summary>
        [SettingsUISlider(min = 25f, max = 400f, step = 5f, unit = "percentage")]
        [SettingsUISection(SectionMain, GroupWidth)]
        public int WidthPercentage { get; set; } = 150;

        /// <summary>
        /// Middle crossings: on, and a switch to turn them off.
        ///
        /// On by default since 0.13.0, but it stays a switch, because this is the one part of the
        /// mod that creates crossings rather than resizing the game's own — and a crossing it
        /// creates joins the pedestrian network the simulation runs on. The fault behind the
        /// crashes is understood and fixed, but a switch that turns the whole thing off and clears
        /// it out of a city is worth keeping for the next time something is not.
        ///
        /// Turning it off also takes out any middle crossings already in the city, a few at a time,
        /// so a save that has them can be opened and cleaned without touching anything else.
        /// </summary>
        [SettingsUISection(SectionMain, GroupWidth)]
        public bool EnableMiddleCrossings { get; set; } = true;

        /// <summary>
        /// A line down either side of the zebra stripes — the border a "ladder" crossing has.
        ///
        /// Off out of the box. It is a change to how every crossing in the city looks, and unlike
        /// the width it is a matter of taste rather than of the crossing working better, so nobody
        /// gets it without asking.
        ///
        /// Turning it on or off hands the city's roads back to the game to be laid again, because a
        /// junction reads the crossing lane prefab once, when it is laid, and never again.
        /// </summary>
        [SettingsUISection(SectionMain, GroupWidth)]
        public bool EdgeLines { get; set; } = false;

        /// <summary>
        /// Which of the game's painted markings the lines are drawn with.
        ///
        /// Automatic picks the closest thing the game already has to a crossing's border — a stop
        /// line — and prefers one gated behind the same theme as the crossing itself, so a North
        /// American city gets the North American one. The list is here because that choice is made
        /// from prefab data rather than from anything a player can see, and a marking gated behind
        /// the wrong theme is refused by the game with nothing said about it: if the lines do not
        /// appear, this is the way out without a new build.
        /// </summary>
        [SettingsUIDropdown(typeof(CrosswalkWidthSetting), nameof(GetEdgeLineStyles))]
        [SettingsUISection(SectionMain, GroupWidth)]
        public string EdgeLineStyle { get; set; } = Systems.CrosswalkLineCatalog.kAutomatic;

        /// <summary>
        /// The markings that could serve, for the dropdown above.
        ///
        /// Static, and reached through <see cref="Mod.LineCatalog"/>, because this is how the game
        /// asks for a dropdown's contents and it hands nothing in. Empty before the prefabs load,
        /// which is why "Automatic" is always first and is what the setting defaults to.
        /// </summary>
        public static DropdownItem<string>[] GetEdgeLineStyles()
        {
            List<DropdownItem<string>> items = new List<DropdownItem<string>>
            {
                new DropdownItem<string>
                {
                    value = Systems.CrosswalkLineCatalog.kAutomatic,
                    displayName = LocalizedString.Value("Automatic")
                }
            };

            Systems.CrosswalkLineCatalog catalog = Mod.LineCatalog;

            if (catalog != null)
            {
                IReadOnlyList<Systems.CrosswalkLineCatalog.Candidate> candidates = catalog.Candidates;

                for (int i = 0; i < candidates.Count; i++)
                {
                    items.Add(new DropdownItem<string>
                    {
                        value = candidates[i].m_Name,
                        displayName = LocalizedString.Value(
                            $"{candidates[i].m_Name} ({candidates[i].m_Width:0.00}m)")
                    });
                }
            }

            return items.ToArray();
        }

        /// <summary>Floor in metres, applied after the percentage. 0 disables it.</summary>
        [SettingsUISlider(min = 0f, max = 12f, step = 0.5f, unit = "floatSingleFraction")]
        [SettingsUISection(SectionMain, GroupWidth)]
        public float MinimumWidth { get; set; } = 0f;

        /// <summary>Ceiling in metres, applied after the percentage. 0 disables it.</summary>
        [SettingsUISlider(min = 0f, max = 20f, step = 0.5f, unit = "floatSingleFraction")]
        [SettingsUISection(SectionMain, GroupWidth)]
        public float MaximumWidth { get; set; } = 0f;


        /// <summary>How much one press of the tool's narrower or wider button changes a crossing.</summary>
        [SettingsUISlider(min = 5f, max = 100f, step = 5f, unit = "percentage")]
        [SettingsUISection(SectionMain, GroupWidth)]
        public int ToolStepPercentage { get; set; } = 25;

        // ---------------------------------------------------------------- maintenance

        /// <summary>Re-lays every crossing in the city at the current width.</summary>
        [SettingsUIButton]
        [SettingsUISection(SectionMain, GroupMaintenance)]
        public bool ApplyToExistingCrossings
        {
            set
            {
                CrosswalkWidthSystem.RequestRefresh();
            }
        }

        /// <summary>
        /// Puts every crossing in the city back exactly where and how the game lays it.
        ///
        /// The undo button, and the one to reach for when something has gone wrong. It leaves the
        /// mod switched on, so the tool still works — it just starts again from a city that looks
        /// untouched.
        /// </summary>
        [SettingsUIButton]
        [SettingsUIConfirmation]
        [SettingsUISection(SectionMain, GroupMaintenance)]
        public bool ResetEverything
        {
            set
            {
                CrosswalkWidthSystem.RequestReset();
            }
        }

        /// <summary>Writes what the mod found to the game log.</summary>
        [SettingsUIButton]
        [SettingsUISection(SectionMain, GroupMaintenance)]
        public bool LogDiscoveredCrossings
        {
            set
            {
                CrosswalkWidthSystem.RequestDump();
            }
        }

        /// <summary>
        /// Takes every trace of this mod back out of the loaded city.
        ///
        /// For uninstalling cleanly. Save afterwards and the city has nothing of the mod's left in
        /// it — no per-junction widths, no widened crossings, nothing for a later session to have
        /// to understand.
        /// </summary>
        [SettingsUIButton]
        [SettingsUIConfirmation]
        [SettingsUISection(SectionMain, GroupMaintenance)]
        public bool RemoveModDataFromCity
        {
            set
            {
                CrosswalkWidthSystem.RequestPurge();
            }
        }

        /// <summary>
        /// One string covering every value that changes the geometry, so the system can tell in a
        /// single comparison whether it needs to write to the prefabs again.
        /// </summary>
        public string Signature =>
            $"{Enabled}|{WidthPercentage}|{MinimumWidth}|{MaximumWidth}|{EdgeLines}|{EdgeLineStyle}";

        public override void SetDefaults()
        {
            Enabled = true;
            EnableMiddleCrossings = true;
            EdgeLines = false;
            EdgeLineStyle = CrosswalkLineCatalog.kAutomatic;
            WidthPercentage = 150;
            MinimumWidth = 0f;
            MaximumWidth = 0f;
            ToolStepPercentage = 25;
        }
    }
}
