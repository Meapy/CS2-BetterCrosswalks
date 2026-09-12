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
                    "Apply to existing crossings"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(CrosswalkWidthSetting.ApplyToExistingCrossings)),
                    "Checks every crossing in the city against the current settings.\n\n" +
                    "You should not need this: a change to any setting above is applied to the " +
                    "whole city straight away, crossings already standing included. It is here for " +
                    "the rare case where one gets missed."
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
                    "road pieces use it, to the game log. Use this if a junction looks untouched."
                }
            };
        }

        public void Unload()
        {
        }
    }
}
