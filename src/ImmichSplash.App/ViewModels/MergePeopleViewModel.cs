using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Api;

namespace ImmichSplash.App.ViewModels;

/// <summary>Someone the face can be merged into, on the merge dialog's list.</summary>
public sealed partial class MergeChoiceViewModel(PersonInfo person) : ObservableObject, IDisposable
{
    public PersonInfo Person { get; } = person;
    public string Title => Person.Name.Length > 0 ? Person.Name : Tr.T("Unnamed");

    [ObservableProperty]
    public partial Bitmap? Cover { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    internal async Task LoadCoverAsync(ThumbnailLoader loader, CancellationToken ct)
    {
        try
        {
            Cover = await loader.LoadPersonAsync(Person.Id, 96, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Info($"Face thumbnail failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Cover?.Dispose();
        Cover = null;
    }
}

/// <summary>
/// Merges a face into another person (the same person found twice): every photo of one becomes the other's.
/// Immich can't split them again, so the dialog says so before the button does it.
/// </summary>
public sealed partial class MergePeopleViewModel : ViewModelBase, IDisposable
{
    readonly PhotosViewModel _owner;
    readonly MainWindowViewModel _main;
    readonly List<MergeChoiceViewModel> _all;
    readonly CancellationTokenSource _lifetime = new();

    public MergePeopleViewModel(PhotosViewModel owner, MainWindowViewModel main, PersonInfo source, IEnumerable<PersonInfo> people, ThumbnailLoader? loader)
    {
        _owner = owner;
        _main = main;
        Source = new MergeChoiceViewModel(source);
        // Named people first: merging a stray face into someone you know is the usual case.
        _all = people.Where(p => p.Id != source.Id)
            .OrderByDescending(p => p.Name.Length > 0).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new MergeChoiceViewModel(p)).ToList();
        if (loader is not null)
        {
            _ = Source.LoadCoverAsync(loader, _lifetime.Token);
            foreach (var choice in _all)
                _ = choice.LoadCoverAsync(loader, _lifetime.Token);
        }
        Filter();
    }

    public MergeChoiceViewModel Source { get; }
    public string Title => Source.Person.Name.Length > 0 ? Tr.T("Merge “{0}” with…", Source.Person.Name) : Tr.T("Merge this face with…");

    public ObservableCollection<MergeChoiceViewModel> Choices { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    partial void OnSearchTextChanged(string value) => Filter();

    void Filter()
    {
        var text = SearchText.Trim();
        Choices.Clear();
        foreach (var choice in _all.Where(c => text.Length == 0 || c.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase)))
            Choices.Add(choice);
        IsEmpty = Choices.Count == 0;
    }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTarget), nameof(Warning), nameof(CanMerge))]
    public partial MergeChoiceViewModel? Target { get; set; }

    public bool HasTarget => Target is not null;

    public string Warning => Target is null ? "" : Tr.T(
        "All photos of {0} will be {1}'s. This can't be undone: Immich can't split them apart again.",
        Source.Person.Name.Length > 0 ? $"“{Source.Person.Name}”" : Tr.T("this face"),
        Target.Person.Name.Length > 0 ? $"“{Target.Person.Name}”" : Tr.T("the other face"));

    [RelayCommand]
    void Pick(MergeChoiceViewModel choice)
    {
        foreach (var c in _all)
            c.IsSelected = ReferenceEquals(c, choice);
        Target = choice;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMerge))]
    public partial bool IsSaving { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public bool CanMerge => Target is not null && !IsSaving;

    [RelayCommand]
    async Task MergeAsync()
    {
        if (Target is not { } target || IsSaving || AppServices.Account.Client is not { } client)
            return;
        IsSaving = true;
        Error = null;
        try
        {
            await client.MergePeopleAsync(target.Person.Id, [Source.Person.Id], CancellationToken.None);
            _owner.OnPeopleMerged(Source.Person, target.Person);
            _main.CloseOverlay(this);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not merge people", ex);
            Error = Tr.T("Couldn't merge them. {0}", NetworkErrors.Describe(ex));
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    void Cancel() => _main.CloseOverlay(this);

    public void Dispose()
    {
        _lifetime.Cancel();
        Source.Dispose();
        foreach (var choice in _all)
            choice.Dispose();
    }
}
