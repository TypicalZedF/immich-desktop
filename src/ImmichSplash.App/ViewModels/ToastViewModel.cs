using CommunityToolkit.Mvvm.ComponentModel;
using Material.Icons;

namespace ImmichSplash.App.ViewModels;

public enum ToastKind { Success, Info, Error }

/// <summary>A small message that appears over the page and fades away ("Copied to clipboard", "Edits applied"…).</summary>
public sealed partial class ToastViewModel(string text, ToastKind kind, bool isProgress) : ObservableObject
{
    [ObservableProperty]
    public partial string Text { get; set; } = text;

    public ToastKind Kind { get; } = kind;

    /// <summary>A step in something that's still going on ("Downloading 3 of 10…"): replaced by the next step, not stacked.</summary>
    public bool IsProgress { get; } = isProgress;

    public bool IsError => Kind == ToastKind.Error;

    public MaterialIconKind Icon => IsProgress ? MaterialIconKind.Loading
        : Kind switch
        {
            ToastKind.Success => MaterialIconKind.CheckCircleOutline,
            ToastKind.Error => MaterialIconKind.AlertCircleOutline,
            _ => MaterialIconKind.InformationOutline,
        };

    public bool Spins => IsProgress;
}
