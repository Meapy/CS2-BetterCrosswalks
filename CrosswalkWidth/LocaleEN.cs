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
                { m_Setting.GetSettingsLocaleID(), "Crosswalk Width" },
                { m_Setting.GetOptionTabLocaleID(CrosswalkWidthSetting.SectionMain), "Main" },

                { m_Setting.GetOptionGroupLocaleID(CrosswalkWidthSetting.GroupWidth), "Width" },
                { m_Setting.GetOptionGroupLocaleID(CrosswalkWidthSetting.GroupApply), "Existing crossings" },

                {
                    m_Setting.GetOptionLabelLocaleID(nameof(CrosswalkWidthSetting.Enabled)),
                    "Enable crossing resizing"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(CrosswalkWidthSetting.Enabled)),
                    "Turn this off to put every crossing back to the width its asset author chose. " +
                    "Crossings already laid keep their current width until you reload the save or " +
                    "press Apply to existing crossings."
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
                    m_Setting.GetOptionLabelLocaleID(nameof(CrosswalkWidthSetting.ApplyToExistingCrossings)),
                    "Apply to existing crossings"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(CrosswalkWidthSetting.ApplyToExistingCrossings)),
                    "Lays every crossing in the city again at the current width, without reloading.\n\n" +
                    "It hands every node and junction back to the game's own lane pipeline in one " +
                    "go, so expect a pause on a large city."
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
