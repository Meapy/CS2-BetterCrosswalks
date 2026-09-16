using Colossal.UI.Binding;
using Game.Tools;
using Game.UI;

namespace CrosswalkWidth.Systems
{
    /// <summary>
    /// The managed half of the toolbar button and its panel.
    ///
    /// Everything the frontend needs is published from here rather than worked out in the UI: the
    /// frontend cannot see the tool system, the selected entity or the override component, and a
    /// binding fed from the system that already knows the answer is far more robust than trying to
    /// reconstruct it from undocumented prop shapes.
    ///
    /// The bindings are deliberately small — a bool for whether the tool is running, a bool for
    /// whether a junction is selected, and the selected junction's width as a percentage — plus
    /// four triggers the buttons call. Anything more would be state duplicated in two places.
    /// </summary>
    public partial class CrosswalkToolUISystem : UISystemBase
    {
        private const string kGroup = "crosswalkWidth";

        private ToolSystem m_ToolSystem;
        private CrosswalkPickerToolSystem m_PickerTool;

        private ValueBinding<bool> m_ToolActive;
        private ValueBinding<bool> m_HasSelection;
        private ValueBinding<int> m_SelectedPercent;
        private ValueBinding<int> m_GlobalPercent;
        private ValueBinding<int> m_CrossingNumber;
        private ValueBinding<int> m_CrossingCount;
        private ValueBinding<bool> m_CanScramble;
        private ValueBinding<bool> m_HasScramble;
        private ValueBinding<bool> m_CanRemove;
        private ValueBinding<bool> m_PaintHidden;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_PickerTool = World.GetOrCreateSystemManaged<CrosswalkPickerToolSystem>();

            AddBinding(m_ToolActive = new ValueBinding<bool>(kGroup, "toolActive", false));
            AddBinding(m_HasSelection = new ValueBinding<bool>(kGroup, "hasSelection", false));
            AddBinding(m_SelectedPercent = new ValueBinding<int>(kGroup, "selectedPercent", 100));
            AddBinding(m_GlobalPercent = new ValueBinding<int>(kGroup, "globalPercent", 150));
            AddBinding(m_CrossingNumber = new ValueBinding<int>(kGroup, "crossingNumber", 0));
            AddBinding(m_CrossingCount = new ValueBinding<int>(kGroup, "crossingCount", 0));
            AddBinding(m_CanScramble = new ValueBinding<bool>(kGroup, "canScramble", false));
            AddBinding(m_HasScramble = new ValueBinding<bool>(kGroup, "hasScramble", false));
            AddBinding(m_CanRemove = new ValueBinding<bool>(kGroup, "canRemove", false));
            AddBinding(m_PaintHidden = new ValueBinding<bool>(kGroup, "paintHidden", false));

            AddBinding(new TriggerBinding(kGroup, "toggleTool", ToggleTool));
            AddBinding(new TriggerBinding(kGroup, "widen", () => m_PickerTool.AdjustSelected(1)));
            AddBinding(new TriggerBinding(kGroup, "narrow", () => m_PickerTool.AdjustSelected(-1)));
            AddBinding(new TriggerBinding(kGroup, "resetSelected", m_PickerTool.ResetSelected));
            AddBinding(new TriggerBinding(kGroup, "resetJunction", m_PickerTool.ResetJunction));
            AddBinding(new TriggerBinding(kGroup, "applyToJunction", m_PickerTool.ApplyToWholeJunction));
            AddBinding(new TriggerBinding(kGroup, "toggleScramble", m_PickerTool.ToggleScramble));
            AddBinding(new TriggerBinding(kGroup, "removeSelected", m_PickerTool.RemoveSelected));
            AddBinding(new TriggerBinding(kGroup, "toggleHidePaint", m_PickerTool.ToggleHidePaint));
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();

            bool active = m_ToolSystem.activeTool == m_PickerTool;
            m_ToolActive.Update(active);

            bool hasSelection = active
                && m_PickerTool.selectedNode != Unity.Entities.Entity.Null
                && m_PickerTool.selectedCrossingNumber > 0;

            m_HasSelection.Update(hasSelection);

            // Read from the junction rather than the crossing: the button is about the junction as
            // a whole, and it stays usable when the tool has a junction but no crossing picked.
            bool onJunction = active && m_PickerTool.selectedNode != Unity.Entities.Entity.Null;

            m_CanRemove.Update(hasSelection && m_PickerTool.selectedIsAdded);
            m_PaintHidden.Update(hasSelection && m_PickerTool.selectedPaintHidden);
            m_CanScramble.Update(onJunction && m_PickerTool.canScramble);
            m_HasScramble.Update(onJunction && m_PickerTool.hasScramble);
            m_CrossingCount.Update(m_PickerTool.crossingCount);
            m_CrossingNumber.Update(m_PickerTool.selectedCrossingNumber);

            if (hasSelection)
            {
                m_SelectedPercent.Update(m_PickerTool.SelectedPercent());
            }

            CrosswalkWidthSetting settings = Mod.Settings;

            if (settings != null)
            {
                m_GlobalPercent.Update(settings.WidthPercentage);
            }
        }

        /// <summary>
        /// Starts the tool, or puts the default tool back if it is already running — so the toolbar
        /// button behaves like every other tool button in the game.
        /// </summary>
        private void ToggleTool()
        {
            if (m_ToolSystem.activeTool == m_PickerTool)
            {
                m_ToolSystem.activeTool = World.GetOrCreateSystemManaged<DefaultToolSystem>();
                return;
            }

            m_ToolSystem.activeTool = m_PickerTool;
        }
    }
}
