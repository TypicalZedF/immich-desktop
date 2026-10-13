using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImmichDesktop.Core.Settings;

/// <summary>
/// Thread-safe settings persisted as JSON. Readers get a snapshot copy; writers go through <see cref="Update"/>.
/// </summary>
public sealed class SettingsStore
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    readonly string _path;
    readonly Lock _gate = new();
    readonly AppSettings _settings;

    public SettingsStore(string path)
    {
        _path = path;
        _settings = Load(path);
    }

    /// <summary>Raised after settings change, with a snapshot of the new settings. May fire on any thread.</summary>
    public event Action<AppSettings>? Changed;

    /// <summary>A snapshot copy; mutating it has no effect.</summary>
    public AppSettings Current
    {
        get
        {
            lock (_gate)
                return Clone(_settings);
        }
    }

    public void Update(Action<AppSettings> mutate)
    {
        AppSettings snapshot;
        lock (_gate)
        {
            mutate(_settings);
            Normalize(_settings);
            Save();
            snapshot = Clone(_settings);
        }

        try
        {
            Changed?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            Log.Error("Settings change handler failed", ex);
        }
    }

    static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json);
                if (settings is not null)
                {
                    Normalize(settings);
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Settings file is unreadable; starting fresh", ex);
            try { File.Copy(path, path + ".broken", overwrite: true); } catch { /* best effort */ }
        }

        return new AppSettings();
    }

    void Save()
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_settings, Json));
        File.Move(temp, _path, overwrite: true);
    }

    static void Normalize(AppSettings s)
    {
        s.MaxConcurrentUploads = Math.Clamp(s.MaxConcurrentUploads, 1, 8);
        s.UploadLimitKBps = Math.Max(0, s.UploadLimitKBps);
        s.RescanIntervalMinutes = Math.Clamp(s.RescanIntervalMinutes, 5, 24 * 60);
        s.Folders ??= [];
        foreach (var folder in s.Folders)
        {
            folder.ExcludePatterns ??= [];
            if (string.IsNullOrWhiteSpace(folder.AlbumName))
                folder.AlbumName = null;
        }
    }

    static AppSettings Clone(AppSettings s) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, Json), Json)!;
}
