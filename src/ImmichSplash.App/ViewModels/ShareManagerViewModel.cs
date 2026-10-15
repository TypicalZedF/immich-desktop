using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Api;

namespace ImmichSplash.App.ViewModels;

/// <summary>One person an album is shared with.</summary>
public sealed partial class MemberRowViewModel : ObservableObject
{
    readonly ShareManagerViewModel _manager;

    public MemberRowViewModel(ShareManagerViewModel manager, AlbumMember member)
    {
        _manager = manager;
        Member = member;
        _role = manager.Roles.First(r => r.Value == member.Role);
    }

    public AlbumMember Member { get; }
    public string Initial => Member.Name.Length > 0 ? Member.Name[..1].ToUpperInvariant() : "?";
    public string Subtitle => Member.Email;

    Option<string> _role;

    /// <summary>Can look (Viewer) or add photos too (Editor). Changing it tells the server.</summary>
    public Option<string> Role
    {
        get => _role;
        set
        {
            if (value is null || value == _role)
                return;
            _role = value;
            OnPropertyChanged();
            _ = _manager.ChangeRoleAsync(this, value.Value);
        }
    }

    [RelayCommand]
    Task RemoveAsync() => _manager.RemoveAsync(this);
}

/// <summary>One public link to the album.</summary>
public sealed partial class LinkRowViewModel : ObservableObject
{
    readonly ShareManagerViewModel _manager;

    public LinkRowViewModel(ShareManagerViewModel manager, SharedLinkSummary link, string url)
    {
        _manager = manager;
        Link = link;
        Url = url;
        _allowDownload = link.AllowDownload;
    }

    public SharedLinkSummary Link { get; }
    public string Url { get; }

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Link.CreatedAt is { } created)
                parts.Add(Tr.T("Created {0}", Format.Relative(created)));
            parts.Add(Link.ExpiresAt is not { } expires ? Tr.T("Never expires")
                : Link.IsExpired ? Tr.T("Expired")
                : Tr.T("Expires {0}", expires.ToLocalTime().ToString("d", System.Globalization.CultureInfo.CurrentCulture)));
            return string.Join(" · ", parts);
        }
    }

    bool _allowDownload;

    /// <summary>Whether people with the link can download. Changing it tells the server.</summary>
    public bool AllowDownload
    {
        get => _allowDownload;
        set
        {
            if (value == _allowDownload)
                return;
            _allowDownload = value;
            OnPropertyChanged();
            _ = _manager.SetDownloadAsync(this, value);
        }
    }

    [RelayCommand]
    Task CopyAsync() => _manager.CopyLinkAsync(this);

    [RelayCommand]
    Task DeleteAsync() => _manager.DeleteLinkAsync(this);
}

/// <summary>
/// Who an album is shared with and how: the people (and what they may do), and its public links. Opened
/// from an album's menu, like the web app's "Share" dialog.
/// </summary>
public sealed partial class ShareManagerViewModel : ViewModelBase
{
    readonly PhotosViewModel _owner;
    readonly MainWindowViewModel _main;
    readonly AlbumInfo _album;
    readonly CancellationTokenSource _lifetime = new();
    string _baseUrl = "";

    public ShareManagerViewModel(PhotosViewModel owner, MainWindowViewModel main, AlbumInfo album)
    {
        _owner = owner;
        _main = main;
        _album = album;
        SelectedAddRole = Roles[0];
        _ = LoadAsync();
    }

    public string Title => _album.AlbumName;

    public IReadOnlyList<Option<string>> Roles { get; } =
    [
        new(Tr.T("Viewer"), "viewer"),
        new(Tr.T("Editor"), "editor"),
    ];

    public ObservableCollection<MemberRowViewModel> Members { get; } = [];
    public ObservableCollection<ServerUser> Candidates { get; } = [];
    public ObservableCollection<LinkRowViewModel> Links { get; } = [];

    [ObservableProperty]
    public partial string OwnerText { get; set; } = "";

    [ObservableProperty]
    public partial ServerUser? SelectedCandidate { get; set; }

    [ObservableProperty]
    public partial Option<string> SelectedAddRole { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowContent))]
    public partial bool IsLoading { get; set; } = true;

    public bool ShowContent => !IsLoading;

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoMembers))]
    public partial int MemberCount { get; set; }

    public bool NoMembers => MemberCount == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoLinks))]
    public partial int LinkCount { get; set; }

    public bool NoLinks => LinkCount == 0;

    [ObservableProperty]
    public partial bool CanAddPerson { get; set; }

    partial void OnSelectedCandidateChanged(ServerUser? value) => CanAddPerson = value is not null;

    AppSettingsAccess Me => new();

    sealed class AppSettingsAccess
    {
        public string? UserId => AppServices.Settings.Current.UserId;
    }

    async Task LoadAsync()
    {
        if (AppServices.Account.Client is not { } client)
        {
            IsLoading = false;
            Error = Tr.T("Not signed in.");
            return;
        }
        try
        {
            var sharingTask = client.GetAlbumSharingAsync(_album.Id, _lifetime.Token);
            var usersTask = client.GetUsersAsync(_lifetime.Token);
            var linksTask = client.GetSharedLinksAsync(_lifetime.Token);
            var web = AppServices.Settings.Current.WebUrl ?? client.ApiUrl.ToString();
            _baseUrl = await client.GetShareBaseUrlAsync(web, _lifetime.Token);
            var sharing = await sharingTask;
            var users = await usersTask;
            var links = await linksTask;

            OwnerText = sharing.Owner is { } owner ? Tr.T("Owned by {0}", owner.Name) : "";
            Members.Clear();
            foreach (var member in sharing.Members)
                Members.Add(new MemberRowViewModel(this, member));
            MemberCount = Members.Count;

            var taken = sharing.Members.Select(m => m.UserId).Append(sharing.Owner?.UserId).Append(Me.UserId).ToHashSet();
            Candidates.Clear();
            foreach (var user in users.Where(u => !taken.Contains(u.Id)).OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase))
                Candidates.Add(user);

            Links.Clear();
            foreach (var link in links.Where(l => l.AlbumId == _album.Id))
                Links.Add(new LinkRowViewModel(this, link, $"{_baseUrl}/share/{link.Key}"));
            LinkCount = Links.Count;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("Could not load how the album is shared", ex);
            Error = Tr.T("Couldn't load the sharing details. {0}", NetworkErrors.Describe(ex));
        }
        finally
        {
            IsLoading = false;
        }
    }

    void Changed() => _owner.OnSharingChanged();

    [RelayCommand]
    async Task AddPersonAsync()
    {
        if (AppServices.Account.Client is not { } client || SelectedCandidate is not { } user)
            return;
        Error = null;
        try
        {
            await client.AddAlbumUsersAsync(_album.Id, [user.Id], SelectedAddRole.Value, CancellationToken.None);
            _owner.ShowNotice(Tr.T("Shared with {0}", user.Name));
            Changed();
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Error = Tr.T("Couldn't share with them. {0}", NetworkErrors.Describe(ex));
        }
    }

    internal async Task ChangeRoleAsync(MemberRowViewModel row, string role)
    {
        if (AppServices.Account.Client is not { } client)
            return;
        try
        {
            await client.SetAlbumUserRoleAsync(_album.Id, row.Member.UserId, role, CancellationToken.None);
            _owner.ShowNotice(role == "editor" ? Tr.T("{0} can now add photos", row.Member.Name) : Tr.T("{0} can now only view", row.Member.Name));
        }
        catch (Exception ex)
        {
            Error = Tr.T("Couldn't change that. {0}", NetworkErrors.Describe(ex));
            await LoadAsync();
        }
    }

    internal async Task RemoveAsync(MemberRowViewModel row)
    {
        if (AppServices.Account.Client is not { } client)
            return;
        if (!await _main.ConfirmAsync(Tr.T("Stop sharing with {0}?", row.Member.Name),
                Tr.T("They won't be able to see “{0}” anymore. The photos stay in your library.", _album.AlbumName), Tr.T("Remove"), danger: true))
            return;
        try
        {
            await client.RemoveAlbumUserAsync(_album.Id, row.Member.UserId, CancellationToken.None);
            _owner.ShowNotice(Tr.T("Stopped sharing with {0}", row.Member.Name));
            Changed();
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Error = Tr.T("Couldn't remove them. {0}", NetworkErrors.Describe(ex));
        }
    }

    [RelayCommand]
    async Task CreateLinkAsync()
    {
        if (AppServices.Account.Client is not { } client)
            return;
        Error = null;
        try
        {
            var link = await client.CreateSharedLinkAsync(null, _album.Id, allowDownload: true, expiresAt: null, CancellationToken.None);
            var url = $"{_baseUrl}/share/{link.Key}";
            await _main.CopyTextAsync(url);
            _owner.ShowNotice(Tr.T("Link created and copied"));
            Changed();
            await LoadAsync();
        }
        catch (ImmichApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            Error = Tr.T("Your account or API key isn't allowed to create shared links.");
        }
        catch (Exception ex)
        {
            Error = Tr.T("Couldn't create the link. {0}", NetworkErrors.Describe(ex));
        }
    }

    internal async Task CopyLinkAsync(LinkRowViewModel row)
    {
        await _main.CopyTextAsync(row.Url);
        _owner.ShowNotice(Tr.T("Link copied"));
    }

    internal async Task SetDownloadAsync(LinkRowViewModel row, bool allow)
    {
        if (AppServices.Account.Client is not { } client)
            return;
        try
        {
            await client.UpdateSharedLinkAsync(row.Link.Id, allow, CancellationToken.None);
            _owner.ShowNotice(allow ? Tr.T("People with the link can download") : Tr.T("People with the link can only look"));
        }
        catch (Exception ex)
        {
            Error = Tr.T("Couldn't change that. {0}", NetworkErrors.Describe(ex));
            await LoadAsync();
        }
    }

    internal async Task DeleteLinkAsync(LinkRowViewModel row)
    {
        if (AppServices.Account.Client is not { } client)
            return;
        if (!await _main.ConfirmAsync(Tr.T("Delete this link?"), Tr.T("Anyone who has it won't be able to open it anymore. The photos stay in Immich."),
                Tr.T("Delete link"), danger: true))
            return;
        try
        {
            await client.DeleteSharedLinkAsync(row.Link.Id, CancellationToken.None);
            _owner.ShowNotice(Tr.T("Link deleted"));
            Changed();
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Error = Tr.T("Couldn't delete the link. {0}", NetworkErrors.Describe(ex));
        }
    }

    [RelayCommand]
    void Close()
    {
        _lifetime.Cancel();
        _main.CloseOverlay(this);
    }
}
