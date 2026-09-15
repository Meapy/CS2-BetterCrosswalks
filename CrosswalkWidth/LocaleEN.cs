using System.Collections.Generic;
using Colossal;

namespace CrosswalkWidth
{
    public sealed class LocaleEN : IDictionarySource
    {
        private readonly CrosswalkWidthSetting m_Setting;

        public LocaleEN(CrosswalkWidthSetting setting)
        {
            m_Setting = setting;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(
            IList<IDictionaryEntryError> errors,
            Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "Better Crosswalks" },
                { m_Setting.GetOptionTabLocaleID(CrosswalkWidthSetting.SectionMain), "Main" },

                { m_Setting.GetOptionGroupLocaleID(CrosswalkWidthSetting.GroupWidth), "Crossings" },
                { m_Setting.GetOptionGroupLocaleID(CrosswalkWidthSetting.GroupMaintenance), "Maintenance" },

                {
                    m_Setting.GetOptionLabelLocaleID(nameof(CrosswalkWidthSetting.RemoveModDataFromCity)),
                    "Remove this mod's data from the city"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(CrosswalkWidthSetting.RemoveModDataFromCity)),
                    "Puts every crossing back to the width it was drawn at and forgets every " +
                    "per-junction width in this city.\n\n" +
                    "Do this, then save, before removing the mod, and the save is left exactly as " +
                    "it would have been if the mod had never run."
                },
                {
                    m_Setting.GetOptionWarningLocaleID(nameof(CrosswalkWidthSetting.RemoveModDataFromCity)),
                    "This forgets every per-junction crossing width in this city. It cannot be undone."
                },

                {
                    m_Setting.GetOptionLabelLocaleID(nameof(CrosswalkWidthSetting.Enabled)),
                    "Enable crossing resizing"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(CrosswalkWidthSetting.Enabled)),
                    "Turn this off to put every crossing in the city back to the width its asset " +
                    "author chose. It takes effect straight away."
                },

                {
                    m_Setting.GetOptionLabelLocaleID(nameof(CrosswalkWidthSetting.WidthPercentage)),
                    "Crossing width"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(CrosswalkWidthSetting.WidthPercentage)),
                    "A percentage of the width the crossing was drawn with, so a side street keeps " +
                    "a modest crossing and a boulevard keeps a generous one. 100% is the base game.\n\n" +
                    "A crossing is a lane laid across the road, and this is that lane's width — the " +
                    "depth of the painted band in the direction traffic travels. Widening it paints " +
                    "more of the junction and gives people room to cross side by side instead of " +
                    "in single file."
                },

                {
                    m_Setting.GetOptionLabelLocaleID(nameof(CrosswalkWidthSetting.MinimumWidth)),
                    "Minimum width (metres)"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(CrosswalkWidthSetting.MinimumWidth)),
                    "No crossing narrower than this, whatever the percentage works out at. Useful " +
                    "for lifting the tightest junctions without inflating everything else. 0 turns " +
                    "it off."
                },

                {
                    m_Setting.GetOptionLabelLocaleID(nameof(CrosswalkWidthSetting.MaximumWidth)),
                    "Maximum width (metres)"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(CrosswalkWidthSetting.MaximumWidth)),
                    "Caps the result, so the widest crossings do not swallow a small junction. " +
                    "0 turns it off."
                },

                {
                    m_Setting.GetOptionLabelLocaleID(nameof(CrosswalkWidthSetting.EnableMiddleCrossings)),
                    "Crossings through the middle"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(CrosswalkWidthSetting.EnableMiddleCrossings)),
                    "Junctions where four or more roads meet get crossings corner to corner "
                    + "through the middle — a scramble crossing. The crossing tool edits and "
                    + "removes them like any other crossing.\n\n"
                    + "This is the only part of the mod that creates crossings rather than "
                    + "resizing the ones the game laid, so it has a switch of its own. Turning it "
                    + "off also clears any that are already in the city, a few at a time."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(CrosswalkWidthSetting.EdgeLines)),
                    "Lines down the sides"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(CrosswalkWidthSetting.EdgeLines)),
                    "Paints a solid line down either side of the zebra stripes, so the crossing "
                    + "reads as a bordered band rather than a row of loose bars. The lines sit on "
                    + "the edge of the band and follow it, so a crossing you widen takes its "
                    + "borders with it.\n\n"
                    + "The lines are the game's own road markings, laid by the game's own marking "
                    + "system — except on the crossings this mod adds through the middle, which it "
                    + "lays itself — so they match whatever the city's roads are painted with. They "
                    + "are drawn only where the game paints a crossing — never on the unmarked ones, "
                    + "and never where there is no pavement to step onto, which is what a bridge "
                    + "or an elevated road usually has. Crossings through the middle of a junction "
                    + "get them too, and theirs follow the moment you resize one.\n\n"
                    + "Changing this, or the width above, hands the city's roads back to the game "
                    + "to be laid again, which takes a moment on a large city — and so does "
                    + "resizing a crossing with the tool, for that one junction."
                },

                {
                    m_Setting.GetOptionLabelLocaleID(nameof(CrosswalkWidthSetting.EdgeLineStyle)),
                    "Line style"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(CrosswalkWidthSetting.EdgeLineStyle)),
                    "Which marking the lines are drawn with. Automatic takes the closest thing the "
                    + "game already has — a stop line — and prefers one belonging to the same theme "
                    + "as the crossing itself.\n\n"
                    + "Pick one by hand if the lines do not appear or do not look right. A marking "
                    + "belonging to a theme your city is not using is refused by the game without "
                    + "saying so, and \"List crossings in the log\" writes out everything found "
                    + "here, with its thickness and what the game itself uses it for."
                },

                {
                    m_Setting.GetOptionLabelLocaleID(nameof(CrosswalkWidthSetting.ToolStepPercentage)),
                    "Tool step size"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(CrosswalkWidthSetting.ToolStepPercentage)),
                    "How much one press of the crossing tool's narrower or wider button changes "
                    + "the crossing you have selected.\n\n"
                    + "The tool itself opens from the button at the top left of the screen. Click "
                    + "a junction, then a crossing on it, and five rings appear: either end swings "
                    + "that end up or down the road, the middle slides the whole crossing towards "
                    + "or away from the junction, and either side pulls it wider or narrower. "
                    + "Nothing you do to one crossing touches its neighbours, and everything set "
                    + "there is saved with the city."
                },

                {
                    m_Setting.GetOptionLabelLocaleID(nameof(CrosswalkWidthSetting.ApplyToExistingCrossings)),
                    "Apply to existing crossings and lines"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(CrosswalkWidthSetting.ApplyToExistingCrossings)),
                    "Checks every crossing in the city against the current settings, and lays the " +
                    "lines down their sides again.\n\n" +
                    "A crossing's width reaches the whole city the moment you change it, so you " +
                    "should not need this for that. The lines are different: they are laid by the " +
                    "game when a junction is built and they are saved with your city, so a junction " +
                    "keeps the lines it was built with until something rebuilds it. Press this " +
                    "after updating the mod, or if a line is somewhere it should not be.\n\n" +
                    "Expect a pause on a large city: every road is handed back to the game to be " +
                    "laid again."
                },

                {
                    m_Setting.GetOptionLabelLocaleID(nameof(CrosswalkWidthSetting.ResetEverything)),
                    "Put every crossing back to normal"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(CrosswalkWidthSetting.ResetEverything)),
                    "Returns every crossing in the city to exactly where and how the game lays it: " +
                    "the width goes back to 100%, every junction you have set by hand is forgotten, " +
                    "every crossing you have moved goes back, and the roads are handed to the game " +
                    "to be shaped again.\n\n" +
                    "The mod stays on, so you can start again from something that looks untouched. " +
                    "Expect a pause while the city is rebuilt."
                },
                {
                    m_Setting.GetOptionWarningLocaleID(nameof(CrosswalkWidthSetting.ResetEverything)),
                    "This forgets every crossing you have widened or moved in this city, and cannot be undone."
                },

                {
                    m_Setting.GetOptionLabelLocaleID(nameof(CrosswalkWidthSetting.LogDiscoveredCrossings)),
                    "List crossings in the log"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(CrosswalkWidthSetting.LogDiscoveredCrossings)),
                    "Writes every crossing lane the mod found, its width before and after, and which " +
                    "road pieces use it, to the game log. Use this if a junction looks untouched.\n\n" +
                    "It also lists the markings that could draw lines down a crossing's sides, and " +
                    "every pedestrian lane standing in the city grouped by what it was drawn from — " +
                    "how many are marked crossings, how many are not, and whether each has any " +
                    "paint at all. That is the list to read if a line turns up somewhere it should " +
                    "not."
                }
            };
        }

        public void Unload()
        {
        }
    }
}
