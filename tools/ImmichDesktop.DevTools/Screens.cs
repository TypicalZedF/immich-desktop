using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using ImmichDesktop.App.Services;
using ImmichDesktop.App.ViewModels;
using ImmichDesktop.App.Views;
using ImmichDesktop.Core.Settings;
using ImmichDesktop.Core.Sync;
using Material.Icons;

namespace ImmichDesktop.DevTools;

/// <summary>Renders every screen with realistic sample data, for design review.</summary>
static class Screens
{
    static string _root = "";

    public static void PrepareData()
    {
        _root = Path.Combine(Path.GetTempPath(), "immich-desktop-screens", Guid.NewGuid().ToString("N"));
        var data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        Environment.SetEnvironmentVariable("IMMICH_DESKTOP_DATA_DIR", data);

        var pictures = Directory.CreateDirectory(Path.Combine(_root, "Pictures")).FullName;
        var camera = Directory.CreateDirectory(Path.Combine(_root, "Phone Camera")).FullName;
        var videos = Directory.CreateDirectory(Path.Combine(_root, "Videos")).FullName;

        var settings = new AppSettings
        {
            ServerUrl = "https://photos.example.com/api/",
            WebUrl = "https://photos.example.com/",
            ServerVersion = "v3.3.0",
            UserId = "u1",
            UserName = "Alex Morgan",
            UserEmail = "alex@example.com",
            Folders =
            [
                new SyncFolder { Id = "pictures", Path = pictures },
                new SyncFolder { Id = "camera", Path = camera, AlbumName = "Phone", ExcludePatterns = ["Screenshots", "*.png"] },
                new SyncFolder { Id = "videos", Path = videos, IncludePhotos = false },
            ],
        };
        File.WriteAllText(Path.Combine(data, "settings.json"),
            JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
    }

    static void Seed()
    {
        var db = AppServices.Database;
        void Add(string folder, string dir, int done, int pending, int failed)
        {
            var files = Enumerable.Range(0, done + pending + failed)
                .Select(i => new ScannedFile(Path.Combine(_root, dir, $"IMG_{i:0000}.jpg"), folder, 1000 + i, 1000 + i))
                .ToList();
            db.Upsert(files);
            foreach (var f in files.Take(done)) db.MarkDone(f.Path, FileStatus.Uploaded, Guid.NewGuid().ToString());
            foreach (var f in files.Skip(done + pending)) db.MarkFailed(f.Path, "The file is too large for the server (check your reverse proxy upload limit).", DateTime.UtcNow.AddMinutes(30));
        }
        Add("pictures", "Pictures", 2814, 0, 0);
        Add("camera", "Phone Camera", 1203, 41, 2);
        Add("videos", "Videos", 96, 4, 0);

        var activity = AppServices.Activity;
        activity.Add(ActivityKind.Info, "Setup complete — backing up 3 folders");
        activity.Add(ActivityKind.Success, "Backed up 1,204 new files · 2,910 already on the server");
        activity.Add(ActivityKind.Warning, "Skipped notes.heic — file type not supported by the server", Path.Combine(_root, "Pictures", "notes.heic"));
        activity.Add(ActivityKind.Error, "Couldn't upload VID_2041.mp4: The file is too large for the server (check your reverse proxy upload limit).", Path.Combine(_root, "Phone Camera", "VID_2041.mp4"));
        foreach (var name in new[] { "IMG_4410.HEIC", "IMG_4411.HEIC", "PXL_20261011_174512.jpg", "VID_20261011_180233.mp4", "IMG_4412.HEIC" })
            activity.Add(ActivityKind.Uploaded, name, Path.Combine(_root, "Phone Camera", name));
    }

    static SyncStatus Uploading => new()
    {
        State = SyncState.Uploading,
        Counts = new StatusCounts(45, 4113, 0, 2, 0),
        RunTotal = 45,
        RunDone = 17,
        BytesPerSecond = 8.4 * 1024 * 1024,
        Active =
        [
            new ActiveUpload(Path.Combine(_root, "Phone Camera", "VID_20261011_180233.mp4"), 184_000_000, 121_000_000),
            new ActiveUpload(Path.Combine(_root, "Phone Camera", "PXL_20261011_174512.jpg"), 4_200_000, 1_100_000),
        ],
    };

    static SyncStatus Synced => new()
    {
        State = SyncState.Idle,
        Counts = new StatusCounts(0, 4113, 0, 0, 0),
        LastSync = DateTimeOffset.Now.AddMinutes(-3),
    };

    public static void Generate(string outDir)
    {
        AppServices.Initialize();
        Seed();

        foreach (var (variant, suffix) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
        {
            Application.Current!.RequestedThemeVariant = variant;

            // Onboarding: server, sign in, folders
            var main = new MainWindowViewModel();
            main.ShowOnboarding(resumeSignIn: false);
            var onboarding = (OnboardingViewModel)main.Content;
            Shot(main, outDir, $"01-welcome-{suffix}");

            onboarding.ServerHost = "photos.example.com";
            onboarding.ServerVersion = "v3.3.0";
            onboarding.OffersSso = true;
            onboarding.SsoButtonText = "Sign in with Authentik";
            onboarding.Step = 1;
            Shot(main, outDir, $"02-sign-in-{suffix}", height: 820);
            onboarding.IsWaitingForBrowser = true;
            Shot(main, outDir, $"02b-sso-waiting-{suffix}");
            onboarding.IsWaitingForBrowser = false;

            onboarding.Step = 2;
            onboarding.FolderChoices.Add(new FolderChoiceViewModel(Path.Combine(_root, "Pictures"), MaterialIconKind.ImageMultipleOutline) { IsSelected = true, Detail = "2,814 photos and videos" });
            onboarding.FolderChoices.Add(new FolderChoiceViewModel(Path.Combine(_root, "Videos"), MaterialIconKind.VideoOutline) { IsSelected = true, Detail = "100 photos and videos" });
            onboarding.FolderChoices.Add(new FolderChoiceViewModel(Path.Combine(_root, "Phone Camera"), MaterialIconKind.FolderOutline) { IsSelected = false, Detail = "1,246 photos and videos" });
            Shot(main, outDir, $"03-folders-{suffix}", height: 900);

            // Main app
            main.ShowShell();
            var shell = (ShellViewModel)main.Content;
            shell.HasStorage = true;
            shell.StoragePercent = 38;
            shell.StorageText = "682 GB of 1.8 TB used";
            var overview = (OverviewViewModel)shell.NavItems[0].ViewModel;
            overview.Apply(Uploading);
            Shot(main, outDir, $"10-overview-uploading-{suffix}", height: 860);
            overview.Apply(Synced);
            Shot(main, outDir, $"11-overview-synced-{suffix}", height: 860);

            shell.Navigate(ShellPage.Folders);
            ((FoldersViewModel)shell.NavItems[1].ViewModel).Folders[1].IsExpanded = true;
            Shot(main, outDir, $"20-folders-{suffix}", height: 900);

            shell.Navigate(ShellPage.Activity);
            Shot(main, outDir, $"30-activity-{suffix}", height: 860);

            shell.Navigate(ShellPage.Settings);
            Shot(main, outDir, $"40-settings-{suffix}", height: 1200);

            AppServices.Settings.Update(s => s.AppLock = AppLockMode.Password);
            main.Lock();
            Shot(main, outDir, $"50-locked-password-{suffix}");
            AppServices.Settings.Update(s => s.AppLock = AppLockMode.WindowsHello);
            main.ShowShell();
            main.Lock();
            Shot(main, outDir, $"51-locked-hello-{suffix}");
            AppServices.Settings.Update(s => s.AppLock = AppLockMode.Off);
            main.ShowShell(ShellPage.Settings);

            main.Dialog = new DialogViewModel("Stop backing up “Phone Camera”?",
                "Photos and videos already uploaded stay in Immich. New files in this folder won't be backed up anymore.",
                "Remove folder", "Cancel", true);
            Shot(main, outDir, $"41-dialog-{suffix}");
            main.Dispose();
        }
        Console.WriteLine($"Screenshots written to {Path.GetFullPath(outDir)}");
    }

    static void Shot(MainWindowViewModel vm, string dir, string name, int width = 1060, int height = 720)
    {
        var window = new MainWindow { DataContext = vm, Width = width, Height = height };
        window.Show();
        for (var i = 0; i < 30; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(dir, name + ".png"));
        window.DataContext = null;
        window.Close();
    }
}
