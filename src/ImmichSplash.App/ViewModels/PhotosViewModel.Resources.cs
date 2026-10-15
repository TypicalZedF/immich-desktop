using ImmichSplash.App.Services;
using ImmichSplash.Core;

namespace ImmichSplash.App.ViewModels;

public sealed partial class PhotosViewModel
{
    // ---- Keeping memory in check while browsing ----

    /// <summary>When the app's memory climbs past this (scrolling a huge library fast), cached pictures are let go.</summary>
    const long MemoryCeilingBytes = 1400L * 1024 * 1024;

    /// <summary>Checks now and then that browsing hasn't piled up memory; frees what isn't on screen if it has.</summary>
    async Task WatchMemoryAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _lifetime.Token);
                if (Environment.WorkingSet < MemoryCeilingBytes || Thumbnails is not { } loader)
                    continue;
                Log.Info($"Memory is high ({Environment.WorkingSet / (1024 * 1024)} MB): freeing cached pictures");
                loader.TrimMemory();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
