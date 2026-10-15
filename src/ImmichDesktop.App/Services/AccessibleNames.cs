using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace ImmichDesktop.App.Services;

/// <summary>
/// Gives buttons, switches and lists a name screen readers can announce (Narrator, Orca). Without it they
/// read out a type name like "StackPanel". The name is the text the control shows; for icon-only buttons
/// their tooltip ("Favorite", "Share"…); for a switch or list in a settings row, the row's title.
/// A name set on purpose in XAML is left alone; the ones set here follow the text when it changes.
/// </summary>
static class AccessibleNames
{
    /// <summary>The names this class set (so they can be updated, and names set on purpose aren't touched).</summary>
    static readonly ConditionalWeakTable<Control, string> Ours = new();

    public static void Start()
    {
        ToolTip.TipProperty.Changed.AddClassHandler<Control>((control, _) => Update(control));
        Control.LoadedEvent.AddClassHandler<Button>((button, _) => Update(button));
        Control.LoadedEvent.AddClassHandler<ComboBox>((combo, _) => Update(combo));
        Control.LoadedEvent.AddClassHandler<ListBox>((list, _) => Update(list));
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((text, _) =>
        {
            if (text.FindLogicalAncestorOfType<Button>() is { } button && Ours.TryGetValue(button, out var _))
                Update(button);
        });
    }

    static void Update(Control control)
    {
        // Parts of a control's own template (scroll bar arrows…) are named by the control.
        if (control.TemplatedParent is not null)
            return;
        var current = AutomationProperties.GetName(control);
        if (current is not null && !(Ours.TryGetValue(control, out var ours) && ours == current))
            return;
        var name = NameFor(control);
        if (name == current)
            return;
        AutomationProperties.SetName(control, name);
        if (name is null)
            Ours.Remove(control);
        else
            Ours.AddOrUpdate(control, name);
    }

    static string? NameFor(Control control)
    {
        var shown = control is ContentControl { Content: string s } ? s : Texts(control);
        if (!string.IsNullOrWhiteSpace(shown))
            return shown;
        if (ToolTip.GetTip(control) is string tip && !string.IsNullOrWhiteSpace(tip))
            return tip;
        // A switch or list beside a label (a settings row): the label says what it is.
        if (control is ToggleButton or ComboBox or ListBox && control.FindAncestorOfType<Grid>() is { } row)
        {
            var labels = row.GetLogicalDescendants().OfType<TextBlock>()
                .Where(t => !string.IsNullOrWhiteSpace(t.Text) && !t.GetLogicalAncestors().Contains(control)).ToList();
            var title = (labels.FirstOrDefault(t => t.Classes.Contains("title")) ?? labels.FirstOrDefault())?.Text;
            if (!string.IsNullOrWhiteSpace(title))
                return title;
        }
        return null;
    }

    /// <summary>The visible text inside a control's content, e.g. "Check now".</summary>
    static string? Texts(Control control)
    {
        if (control is ComboBox or ListBox)
            return null; // their text is the selected item, not what they are
        var parts = control.GetLogicalDescendants().OfType<TextBlock>()
            .Where(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text))
            .Select(t => t.Text!.Trim())
            .Distinct()
            .Take(3)
            .ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
}
