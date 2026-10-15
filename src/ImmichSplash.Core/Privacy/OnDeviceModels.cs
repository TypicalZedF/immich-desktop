using Microsoft.ML.OnnxRuntime;

namespace ImmichSplash.Core.Privacy;

/// <summary>A decoded picture: 8-bit BGRA pixels, row by row (what the app's image decoder produces).</summary>
public sealed record BgraImage(int Width, int Height, byte[] Pixels)
{
    public int Stride => Width * 4;
}

/// <summary>
/// Loads the on-device models. Everything runs on this PC: on Windows on the graphics card when there
/// is a usable one (DirectML, any DirectX 12 GPU), otherwise on the CPU. The models ship with the app,
/// except the optional thorough nudity check's, which is downloaded once when you turn it on. No
/// photo or result is ever sent anywhere.
/// </summary>
public static class OnDeviceModels
{
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "Models");

    public static string PathOf(string file) => Path.Combine(Directory, file);

    /// <summary>Optional models downloaded when asked for (kept with the app's data, not the install).</summary>
    public static string DownloadedDirectory => Path.Combine(AppPaths.DataDir, "Models");

    /// <summary>
    /// Official Windows builds of ONNX Runtime can report usage events to Microsoft through
    /// Windows' diagnostics. They never include pictures, but this check promises that nothing
    /// leaves the PC, so it's switched off before any model is loaded.
    /// </summary>
    static readonly Lazy<bool> TelemetryOff = new(() =>
    {
        try
        {
            OrtEnv.Instance().DisableTelemetryEvents();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Couldn't switch off ONNX Runtime telemetry", ex);
            return false;
        }
    });

    /// <summary>True once ONNX Runtime's usage telemetry is confirmed off.</summary>
    public static bool TelemetryDisabled => TelemetryOff.Value;

    /// <summary>A bundled model file: its size and SHA-256, so anyone can check it's the published one.</summary>
    public sealed record ModelFile(string File, string FullPath, long Bytes, string Sha256);

    static readonly string[] Bundled = [NudeNetDetector.ModelFile, ClipNsfwClassifier.HeadFile, PhotoTextReader.DetectModelFile, PhotoTextReader.ReadModelFile];

    /// <summary>The bundled models, plus the thorough check's model once it's downloaded.</summary>
    public static IReadOnlyList<ModelFile> Files()
    {
        var paths = Bundled.Select(PathOf).ToList();
        if (ClipNsfwClassifier.Download.IsDownloaded)
            paths.Add(ClipNsfwClassifier.Download.Path);
        return paths.Select(path =>
            {
                var file = Path.GetFileName(path);
                if (!File.Exists(path))
                    return new ModelFile(file, path, 0, "");
                using var stream = File.OpenRead(path);
                return new ModelFile(file, path, stream.Length, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)));
            })
            .ToList();
    }

    public static bool Available => Bundled.All(file => File.Exists(PathOf(file)));

    /// <summary>Opens a model, on the GPU if asked and possible. <paramref name="onGpu"/> says where it ended up.</summary>
    public static InferenceSession Open(string file, bool useGpu, out bool onGpu) => OpenPath(PathOf(file), useGpu, out onGpu);

    /// <summary>Opens a model file at a full path (e.g. a downloaded one).</summary>
    public static InferenceSession OpenPath(string path, bool useGpu, out bool onGpu)
    {
        var file = Path.GetFileName(path);
        _ = TelemetryOff.Value;
        if (useGpu && OperatingSystem.IsWindows())
        {
            try
            {
                var gpu = new SessionOptions
                {
                    // DirectML's requirements.
                    EnableMemoryPattern = false,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                };
                gpu.AppendExecutionProvider_DML(0);
                var session = new InferenceSession(path, gpu);
                onGpu = true;
                return session;
            }
            catch (Exception ex)
            {
                Log.Info($"GPU not available for {file}, using the CPU: {ex.Message}");
            }
        }
        var cpu = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            // Leave most of the PC free: this runs in the background.
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 4),
            InterOpNumThreads = 1,
        };
        onGpu = false;
        return new InferenceSession(path, cpu);
    }

    internal delegate void PixelSink(int x, int y, float b, float g, float r);

    /// <summary>
    /// Resizes by averaging every source pixel under each target pixel (shrinking with plain
    /// sampling would skip most of a big photo and see a noisy version of it).
    /// </summary>
    internal static void AreaResize(BgraImage image, int width, int height, PixelSink sink)
    {
        var sx = image.Width / (double)width;
        var sy = image.Height / (double)height;
        if (sx <= 1.5 && sy <= 1.5)
        {
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    Sample(image, (x + 0.5) * sx - 0.5, (y + 0.5) * sy - 0.5, out var b, out var g, out var r);
                    sink(x, y, b, g, r);
                }
            return;
        }
        var p = image.Pixels;
        var stride = image.Stride;
        for (var y = 0; y < height; y++)
        {
            var y0 = (int)(y * sy);
            var y1 = Math.Max(y0 + 1, Math.Min(image.Height, (int)((y + 1) * sy)));
            for (var x = 0; x < width; x++)
            {
                var x0 = (int)(x * sx);
                var x1 = Math.Max(x0 + 1, Math.Min(image.Width, (int)((x + 1) * sx)));
                long b = 0, g = 0, r = 0;
                for (var yy = y0; yy < y1; yy++)
                {
                    var row = yy * stride;
                    for (var xx = x0; xx < x1; xx++)
                    {
                        var i = row + xx * 4;
                        b += p[i];
                        g += p[i + 1];
                        r += p[i + 2];
                    }
                }
                var n = (float)((y1 - y0) * (x1 - x0));
                sink(x, y, b / n, g / n, r / n);
            }
        }
    }

    /// <summary>Bilinear sample of a BGRA image at (x, y) in source pixels; writes B, G, R as 0–255.</summary>
    internal static void Sample(BgraImage image, double x, double y, out float b, out float g, out float r)
    {
        x = Math.Clamp(x, 0, image.Width - 1);
        y = Math.Clamp(y, 0, image.Height - 1);
        var x0 = (int)x;
        var y0 = (int)y;
        var x1 = Math.Min(x0 + 1, image.Width - 1);
        var y1 = Math.Min(y0 + 1, image.Height - 1);
        var fx = (float)(x - x0);
        var fy = (float)(y - y0);
        var p = image.Pixels;
        var s = image.Stride;
        int i00 = y0 * s + x0 * 4, i10 = y0 * s + x1 * 4, i01 = y1 * s + x0 * 4, i11 = y1 * s + x1 * 4;
        float Mix(int c) =>
            (p[i00 + c] * (1 - fx) + p[i10 + c] * fx) * (1 - fy) + (p[i01 + c] * (1 - fx) + p[i11 + c] * fx) * fy;
        b = Mix(0);
        g = Mix(1);
        r = Mix(2);
    }
}
