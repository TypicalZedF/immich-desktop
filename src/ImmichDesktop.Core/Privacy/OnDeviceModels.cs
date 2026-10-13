using Microsoft.ML.OnnxRuntime;

namespace ImmichDesktop.Core.Privacy;

/// <summary>A decoded picture: 8-bit BGRA pixels, row by row (what the app's image decoder produces).</summary>
public sealed record BgraImage(int Width, int Height, byte[] Pixels)
{
    public int Stride => Width * 4;
}

/// <summary>
/// Loads the on-device models that ship with the app. Everything runs on this PC: on Windows on the
/// graphics card when there is a usable one (DirectML, any DirectX 12 GPU), otherwise on the CPU.
/// Nothing is downloaded or sent anywhere.
/// </summary>
public static class OnDeviceModels
{
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "Models");

    public static string PathOf(string file) => Path.Combine(Directory, file);

    public static bool Available =>
        File.Exists(PathOf(NsfwClassifier.ModelFile)) && File.Exists(PathOf(PhotoTextReader.DetectModelFile)) && File.Exists(PathOf(PhotoTextReader.ReadModelFile));

    /// <summary>Opens a model, on the GPU if asked and possible. <paramref name="onGpu"/> says where it ended up.</summary>
    public static InferenceSession Open(string file, bool useGpu, out bool onGpu)
    {
        var path = PathOf(file);
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
