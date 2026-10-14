using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ImmichDesktop.Core.Privacy;

/// <summary>
/// The thorough nudity check: CLIP ViT-B/32 (OpenAI, MIT license; the copy Immich's own smart search
/// uses) describes the picture, and LAION's small NSFW detector (MIT license) scores that description.
/// Unlike body-part detection it understands drawings, art and 3D game scenes. CLIP is a separate
/// one-time download (see <see cref="Download"/>); the detector ships with the app. Runs on this PC.
/// </summary>
public sealed class ClipNsfwClassifier : IDisposable
{
    public const string ModelFile = "clip-vit-b32-visual.onnx";
    public const string HeadFile = "nsfw-clip-b32-head.bin";
    const int Size = 224;
    static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];
    static readonly float[] Std = [0.26862954f, 0.26130258f, 0.27577711f];

    /// <summary>Where CLIP comes from: Immich's published copy, checked against its size and SHA-256.</summary>
    public static ModelDownload Download { get; } = new(
        new Uri("https://huggingface.co/immich-app/ViT-B-32__openai/resolve/main/visual/model.onnx"),
        Path.Combine(OnDeviceModels.DownloadedDirectory, ModelFile),
        351_613_724,
        "33a3df41ceef21acdf371af00f6dd0456ec1f9eba24d03a7720f9c3734e40859");

    readonly InferenceSession _session;
    readonly NsfwHead _head;

    public ClipNsfwClassifier(bool useGpu) : this(Download.Path, useGpu)
    {
    }

    public ClipNsfwClassifier(string modelPath, bool useGpu)
    {
        _head = NsfwHead.Load(OnDeviceModels.PathOf(HeadFile));
        _session = OnDeviceModels.OpenPath(modelPath, useGpu, out var onGpu);
        OnGpu = onGpu;
    }

    public bool OnGpu { get; }

    /// <summary>How likely the picture is NSFW (nudity, sex or revealing), 0–1.</summary>
    public float Classify(BgraImage image)
    {
        // CLIP's preprocessing: shortest side to 224, centre crop, normalised RGB.
        var side = Math.Min(image.Width, image.Height);
        var crop = Crop(image, (image.Width - side) / 2, (image.Height - side) / 2, side, side);
        const int plane = Size * Size;
        var pixels = new float[3 * plane];
        OnDeviceModels.AreaResize(crop, Size, Size, (x, y, b, g, r) =>
        {
            var i = y * Size + x;
            pixels[i] = (r / 255f - Mean[0]) / Std[0];
            pixels[plane + i] = (g / 255f - Mean[1]) / Std[1];
            pixels[2 * plane + i] = (b / 255f - Mean[2]) / Std[2];
        });
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor("image", new DenseTensor<float>(pixels, [1, 3, Size, Size]))]);
        return _head.Score(results[0].AsTensor<float>().ToArray());
    }

    static BgraImage Crop(BgraImage image, int x, int y, int width, int height)
    {
        if (x == 0 && y == 0 && width == image.Width && height == image.Height)
            return image;
        var pixels = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
            Buffer.BlockCopy(image.Pixels, (y + row) * image.Stride + x * 4, pixels, row * width * 4, width * 4);
        return new BgraImage(width, height, pixels);
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>
/// LAION's CLIP-based NSFW detector for ViT-B/32 (a 512 → 32 → 32 → 1 network), with its weights
/// exported by tools/models/laion_nsfw_head.py: normalisation mean and variance, then each layer's
/// kernel (inputs × outputs) and bias, as little-endian floats.
/// </summary>
public sealed class NsfwHead
{
    const int In = 512, Hidden = 32;
    readonly float[] _mean, _std, _k1, _b1, _k2, _b2, _k3;
    readonly float _b3;

    NsfwHead(float[] w)
    {
        var at = 0;
        float[] Take(int n) { var part = w[at..(at + n)]; at += n; return part; }
        _mean = Take(In);
        _std = Take(In).Select(v => MathF.Max(MathF.Sqrt(v), 1e-7f)).ToArray();
        _k1 = Take(In * Hidden);
        _b1 = Take(Hidden);
        _k2 = Take(Hidden * Hidden);
        _b2 = Take(Hidden);
        _k3 = Take(Hidden);
        _b3 = Take(1)[0];
        if (at != w.Length)
            throw new InvalidDataException("The NSFW detector file has an unexpected size.");
    }

    public static NsfwHead Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var w = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, w, 0, w.Length * 4);
        return new NsfwHead(w);
    }

    /// <summary>Scores a CLIP image embedding (normalised to unit length first, as LAION's were).</summary>
    public float Score(float[] embedding)
    {
        var length = MathF.Sqrt(embedding.Sum(v => v * v));
        var x = new float[In];
        for (var i = 0; i < In; i++)
            x[i] = (embedding[i] / length - _mean[i]) / _std[i];
        var h1 = Layer(x, _k1, _b1, Hidden);
        var h2 = Layer(h1, _k2, _b2, Hidden);
        var z = _b3;
        for (var i = 0; i < Hidden; i++)
            z += h2[i] * _k3[i];
        return 1 / (1 + MathF.Exp(-z));
    }

    static float[] Layer(float[] input, float[] kernel, float[] bias, int outputs)
    {
        var result = (float[])bias.Clone();
        for (var i = 0; i < input.Length; i++)
            for (var o = 0; o < outputs; o++)
                result[o] += input[i] * kernel[i * outputs + o];
        for (var o = 0; o < outputs; o++)
            result[o] = MathF.Max(result[o], 0); // ReLU
        return result;
    }
}

/// <summary>
/// A model that isn't bundled but downloaded once when asked for. Saved only after its size and
/// SHA-256 match, so a broken or tampered download is never used.
/// </summary>
public sealed record ModelDownload(Uri Source, string Path, long Bytes, string Sha256)
{
    public bool IsDownloaded => File.Exists(Path) && new FileInfo(Path).Length == Bytes;

    /// <param name="handler">For tests; normally the default.</param>
    public async Task DownloadAsync(IProgress<double>? progress, CancellationToken ct, HttpMessageHandler? handler = null)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".part";
        try
        {
            using (var http = new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: handler is null) { Timeout = Timeout.InfiniteTimeSpan })
            using (var response = await http.GetAsync(Source, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(temp);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 16];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    hash.AppendData(buffer, 0, read);
                    total += read;
                    progress?.Report(Math.Min(1, total / (double)Bytes));
                }
                if (total != Bytes || Convert.ToHexStringLower(hash.GetHashAndReset()) != Sha256)
                    throw new InvalidDataException(Loc.T("The download didn't match the published file. Try again later."));
            }
            File.Move(temp, Path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* best effort */ }
        }
    }

    public void Delete()
    {
        if (File.Exists(Path))
            File.Delete(Path);
    }
}
