using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ImmichSplash.Core.Privacy;

/// <summary>How sure the check is (0–1) that a picture shows nudity, or is revealing (underwear, swimwear).</summary>
public sealed record NudityScores(float Explicit, float Revealing);

/// <summary>
/// Finds exposed body parts with NudeNet's 320n detector (notAI-tech, AGPL-3.0, based on YOLOv8).
/// It looks for specific parts rather than judging the whole picture, so bare arms, legs or faces
/// don't count. Runs entirely on this PC.
/// </summary>
public sealed class NudeNetDetector : IDisposable
{
    public const string ModelFile = "nudenet-320n.onnx";
    const int Size = 320;

    // The model's classes, in its output order.
    const int FemaleGenitaliaCovered = 0, ButtocksExposed = 2, FemaleBreastExposed = 3, FemaleGenitaliaExposed = 4,
        AnusExposed = 6, MaleGenitaliaExposed = 14;
    static readonly int[] ExplicitClasses = [ButtocksExposed, FemaleBreastExposed, FemaleGenitaliaExposed, AnusExposed, MaleGenitaliaExposed];
    const int Classes = 18;

    readonly InferenceSession _session;

    public NudeNetDetector(bool useGpu)
    {
        _session = OnDeviceModels.Open(ModelFile, useGpu, out var onGpu);
        OnGpu = onGpu;
    }

    public bool OnGpu { get; }

    public NudityScores Detect(BgraImage image)
    {
        // Like NudeNet: shrink to fit 320×320 keeping the shape, centred on black, RGB from 0 to 1.
        var scale = Size / (double)Math.Max(image.Width, image.Height);
        var w = Math.Max(1, (int)Math.Round(image.Width * scale));
        var h = Math.Max(1, (int)Math.Round(image.Height * scale));
        int left = (Size - w) / 2, top = (Size - h) / 2;
        const int plane = Size * Size;
        var pixels = new float[3 * plane];
        OnDeviceModels.AreaResize(image, w, h, (x, y, b, g, r) =>
        {
            var i = (y + top) * Size + x + left;
            pixels[i] = r / 255f;
            pixels[plane + i] = g / 255f;
            pixels[2 * plane + i] = b / 255f;
        });
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor("images", new DenseTensor<float>(pixels, [1, 3, Size, Size]))]);
        // Output: [1, 4 box values + 18 class scores, candidate boxes]. Only the best score per class matters here.
        var output = results[0].AsTensor<float>();
        var candidates = output.Dimensions[2];
        var best = new float[Classes];
        for (var c = 0; c < Classes; c++)
            for (var j = 0; j < candidates; j++)
                best[c] = Math.Max(best[c], output[0, 4 + c, j]);
        return new NudityScores(ExplicitClasses.Max(c => best[c]), best[FemaleGenitaliaCovered]);
    }

    public void Dispose() => _session.Dispose();
}
