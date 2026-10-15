using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Api;

namespace ImmichSplash.App.ViewModels;

public sealed partial class PhotosViewModel
{
    // ---- Locked folder (needs the account's PIN, like the web and mobile apps) ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGrid), nameof(ShowLockedPrompt), nameof(ShowLockAction))]
    public partial bool IsUnlocked { get; set; }

    [ObservableProperty]
    public partial string Pin { get; set; } = "";

    [ObservableProperty]
    public partial string? LockedMessage { get; set; }

    [ObservableProperty]
    public partial bool CanEnterPin { get; set; }

    [ObservableProperty]
    public partial bool IsUnlocking { get; set; }

    async Task CheckLockedAsync()
    {
        if (Client is not { } client)
            return;
        IsLoading = false;
        LockedMessage = null;
        CanEnterPin = false;
        try
        {
            var status = await client.GetAuthStatusAsync(_lifetime.Token);
            if (!status.HasPin)
            {
                LockedMessage = Tr.T("Set up a PIN code in Immich first (Account settings → PIN code), then come back here.");
                return;
            }
            if (status.IsElevated)
            {
                IsUnlocked = true;
                await LoadGridAsync();
                return;
            }
            CanEnterPin = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ImmichApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
        {
            LockedMessage = Tr.T("The locked folder needs signing in with your password or SSO (an API key can't open it).");
        }
        catch (Exception ex)
        {
            LockedMessage = Tr.T("Couldn't check the locked folder. {0}", NetworkErrors.Describe(ex));
        }
    }

    [RelayCommand]
    async Task UnlockAsync()
    {
        if (Client is not { } client || Pin.Length == 0 || IsUnlocking)
            return;
        IsUnlocking = true;
        LockedMessage = null;
        try
        {
            await client.UnlockSessionAsync(Pin, CancellationToken.None);
            Pin = "";
            IsUnlocked = true;
            await LoadGridAsync();
        }
        catch (ImmichApiException ex) when ((int)ex.StatusCode is >= 400 and < 500)
        {
            LockedMessage = Tr.T("That PIN isn't right.");
        }
        catch (Exception ex)
        {
            LockedMessage = Tr.T("Couldn't unlock. {0}", NetworkErrors.Describe(ex));
        }
        finally
        {
            IsUnlocking = false;
        }
    }

    /// <summary>Locks the folder again (also when leaving the page).</summary>
    [RelayCommand]
    public async Task LockAsync()
    {
        if (!IsUnlocked)
            return;
        IsUnlocked = false;
        Drill = null;
        ClearSelection();
        ReleaseRows();
        Viewer = null;
        CanEnterPin = true;
        try
        {
            if (Client is { } client)
                await client.LockSessionAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Info($"Locking the session failed: {ex.Message}");
        }
    }

    /// <summary>Asks for the PIN (in a panel) and then runs <paramref name="then"/>; used to move photos into the locked folder.</summary>
    async Task<bool> EnsureUnlockedAsync()
    {
        if (Client is not { } client)
            return false;
        try
        {
            var status = await client.GetAuthStatusAsync(CancellationToken.None);
            if (status.IsElevated)
                return true;
            if (!status.HasPin)
            {
                ShowNotice(Tr.T("Set up a PIN code in Immich first (Account settings → PIN code), then come back here."));
                return false;
            }
        }
        catch (Exception ex)
        {
            ShowNotice(Tr.T("Couldn't check the locked folder. {0}", NetworkErrors.Describe(ex)));
            return false;
        }
        var panel = new PinPanelViewModel(this);
        Panel = panel;
        return await panel.Result;
    }
}
