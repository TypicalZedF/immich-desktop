using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ImmichDesktop.Core.Privacy;

/// <summary>How likely a picture is to be each kind of image (they add up to 1).</summary>
public sealed record NsfwScores(float Drawing, float Hentai, float Neutral, float Porn, float Sexy)
{
    /// <summary>Nudity or sex, photographed or drawn.</summary>
    public float Explicit => Porn + Hentai;
}

/// <summary>
/// Spots nudity and sexual content with NSFWJS's MobileNetV2 model (Infinite Red, MIT license),
/// converted to run with ONNX Runtime. Runs entirely on this PC.
/// </summary>
public sealed class NsfwClassifier : IDisposable
{
    public const string ModelFile = "nsfw-mobilenet-v2.onnx";
    const int Size = 224;

    readonly InferenceSession _session;

    public NsfwClassifier(bool useGpu)
    {
        _session = OnDeviceModels.Open(ModelFile, useGpu, out var onGpu);
        OnGpu = onGpu;
    }

    public bool OnGpu { get; }

    public NsfwScores Classify(BgraImage image)
    {
        // The model takes the whole picture squeezed to 224×224, RGB from 0 to 1.
        var pixels = new float[Size * Size * 3];
        var input = new DenseTensor<float>(pixels, [1, Size, Size, 3]);
        AreaResize(image, Size, Size, (x, y, b, g, r) =>
        {
            var i = (y * Size + x) * 3;
            pixels[i] = r / 255f;
            pixels[i + 1] = g / 255f;
            pixels[i + 2] = b / 255f;
        });
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor("input", input)]);
        var p = results[0].AsTensor<float>().ToArray();
        return new NsfwScores(p[0], p[1], p[2], p[3], p[4]);
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
                    OnDeviceModels.Sample(image, (x + 0.5) * sx - 0.5, (y + 0.5) * sy - 0.5, out var b, out var g, out var r);
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

    public void Dispose() => _session.Dispose();
}
