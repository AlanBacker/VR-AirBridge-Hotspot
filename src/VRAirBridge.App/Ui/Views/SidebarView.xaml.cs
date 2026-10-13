using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VRAirBridge.App.Ui.Views;

/// <summary>Right-hand settings sidebar (search, advanced switch, collapsible sections).</summary>
public partial class SidebarView : UserControl
{
    public SidebarView() => InitializeComponent();

    /// <summary>Expands the hotspot section and puts the caret in the hotspot name box.</summary>
    internal void FocusHotspotName()
    {
        HotspotCard.IsExpanded = true;
        HotspotCard.BringIntoView();
        SsidBox.BringIntoView();
        SsidBox.Focus();
        Keyboard.Focus(SsidBox);
        SsidBox.SelectAll();
    }
}
