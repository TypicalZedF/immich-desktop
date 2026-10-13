using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ImmichDesktop.Core;

namespace ImmichDesktop.App.Services;

/// <summary>
/// The signed-in user's avatar: their Immich profile picture, or initials on their Immich avatar color.
/// Changing it here changes it in Immich too.
/// </summary>
public sealed class ProfileService
{
    // Same palette as the Immich web app's user avatars.
    static readonly Dictionary<string, string> AvatarColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["primary"] = "#4250AF",
        ["pink"] = "#F472B6",
        ["red"] = "#EF4444",
        ["yellow"] = "#EAB308",
        ["blue"] = "#3B82F6",
        ["green"] = "#16A34A",
        ["purple"] = "#9333EA",
        ["orange"] = "#EA580C",
        ["gray"] = "#4B5563",
        ["amber"] = "#D97706",
    };

    public Bitmap? Picture { get; private set; }
    public IBrush Color { get; private set; } = new SolidColorBrush(Avalonia.Media.Color.Parse("#4250AF"));

    /// <summary>Raised on the UI thread when the picture or color changes.</summary>
    public event Action? Changed;

    public async Task RefreshAsync()
    {
        if (AppServices.Account.Client is not { } client)
            return;
        try
        {
            var me = await client.GetMeAsync(CancellationToken.None);
            Bitmap? picture = null;
            if (me.HasProfileImage && await client.GetProfileImageAsync(me.Id, CancellationToken.None) is { } bytes)
            {
                picture = await Task.Run(() =>
                {
                    using var stream = new MemoryStream(bytes);
                    return Bitmap.DecodeToWidth(stream, 160, BitmapInterpolationMode.HighQuality);
                });
            }

            var color = me.AvatarColor is { } name && AvatarColors.TryGetValue(name, out var hex) ? hex : "#4250AF";
            Dispatcher.UIThread.Post(() =>
            {
                Picture = picture;
                Color = new SolidColorBrush(Avalonia.Media.Color.Parse(color));
                Changed?.Invoke();
            });
        }
        catch (Exception ex)
        {
            // e.g. an API key without user/profile permissions: keep the initials.
            Log.Info($"Profile picture unavailable: {ex.Message}");
        }
    }

    public async Task SetPictureAsync(string filePath)
    {
        if (AppServices.Account.Client is not { } client)
            return;
        await client.SetProfileImageAsync(filePath, CancellationToken.None);
        await RefreshAsync();
    }

    public async Task RemovePictureAsync()
    {
        if (AppServices.Account.Client is not { } client)
            return;
        await client.RemoveProfileImageAsync(CancellationToken.None);
        await RefreshAsync();
    }

    public void Clear()
    {
        Picture = null;
        Changed?.Invoke();
    }
}
