using Avalonia.Media.Imaging;
using ImmichDesktop.App.Services;
using ImmichDesktop.Core;
using ImmichDesktop.Core.Api;

namespace ImmichDesktop.App.ViewModels;

/// <summary>
/// Memories at the top of the timeline, like the Immich web and mobile apps: photos from this day in
/// earlier years, and birthdays. A card opens that memory's photos like an album.
/// </summary>
public sealed partial class PhotosViewModel
{
    /// <summary>Height of the memories strip (it's the first row of the grid, so the rail needs it exactly).</summary>
    public const double MemoriesHeight = 236;

    List<MemoryCardViewModel> _memoryCards = [];

    /// <summary>Only the main timeline shows memories (not albums, search, the picker…).</summary>
    bool ShowsMemories => Page == LibraryPage.Timeline && Drill is null && !IsPicker;

    async Task LoadMemoriesAsync(ImmichClient client, CancellationToken ct)
    {
        foreach (var card in _memoryCards)
            card.Dispose();
        _memoryCards = [];
        if (!ShowsMemories)
            return;
        try
        {
            var memories = await client.GetMemoriesAsync(DateOnly.FromDateTime(Today()), ct);
            _memoryCards = memories.Select(m => new MemoryCardViewModel(this, m, Today().Year)).ToList();
            if (Thumbnails is { } loader)
                foreach (var card in _memoryCards)
                    _ = card.LoadCoverAsync(loader, CardDecodeWidth, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Info($"Memories unavailable: {ex.Message}");
        }
    }

    internal void OpenMemory(MemoryCardViewModel card) =>
        OpenDrill(new GridQuery(card.Title, (_, _) => Task.FromResult(BucketState.FromAssets(card.Memory.Assets))));
}

/// <summary>The strip of memory cards, shown as the first row of the timeline.</summary>
public sealed class MemoriesRowViewModel(IReadOnlyList<MemoryCardViewModel> cards)
{
    public IReadOnlyList<MemoryCardViewModel> Cards { get; } = cards;
}

/// <summary>One memory: "3 years ago" or "Sam's birthday", with its first photo as the cover.</summary>
public sealed class MemoryCardViewModel(PhotosViewModel owner, Memory memory, int thisYear) : LibraryCardViewModel(owner)
{
    public Memory Memory { get; } = memory;

    public override string Title => Memory switch
    {
        { IsBirthday: true, PersonName: { Length: > 0 } name } => Tr.T("{0}'s birthday", name),
        { IsBirthday: true } => Tr.T("Birthday"),
        { Year: { } year } when thisYear - year >= 1 => Tr.T("{0} ago", Format.Plural(thisYear - year, "year")),
        _ => Tr.T("On this day"),
    };

    public override string Subtitle => Format.Plural(Memory.Assets.Count, "item");

    protected override Task<Bitmap?> LoadAsync(ThumbnailLoader loader, int decodeWidth, CancellationToken ct) =>
        loader.LoadAsync(Memory.Assets[0].Id, AssetImageSize.Thumbnail, decodeWidth, ct);

    protected override void Open() => Owner.OpenMemory(this);
}
