using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ImmichDesktop.Core.Privacy;

/// <summary>A line of text found in a picture, where it is, and how sure the reader is (0–1).</summary>
public sealed record TextLine(string Text, float Confidence, int X, int Y, int Width, int Height);

/// <summary>
/// Reads text in pictures on this PC with PaddleOCR's PP-OCRv4 models (Apache 2.0, from RapidOCR):
/// one finds where text is, the other reads each line. Good enough to notice cards, IDs and
/// documents; it never leaves the PC.
/// </summary>
public sealed class PhotoTextReader : IDisposable
{
    public const string DetectModelFile = "text-detect.onnx";
    public const string ReadModelFile = "text-read.onnx";

    const float PixelThreshold = 0.3f;  // a pixel is "text"
    const float BoxThreshold = 0.5f;    // a region's average must be at least this
    const double Unclip = 1.6;          // regions come out tight; grow them like PaddleOCR does
    const int LineHeight = 48;          // the reader's input height
    const int MaxLines = 80;            // enough to judge a page; keeps scans quick

    readonly InferenceSession _detect;
    readonly InferenceSession _read;
    readonly string[] _characters;
    readonly int _maxSide;

    public PhotoTextReader(bool useGpu)
    {
        _detect = OnDeviceModels.Open(DetectModelFile, useGpu, out var detectOnGpu);
        _read = OnDeviceModels.Open(ReadModelFile, useGpu, out _);
        OnGpu = detectOnGpu;
        // The reader's alphabet is stored in the model; index 0 is "no character", the last a space.
        var alphabet = _read.ModelMetadata.CustomMetadataMap.TryGetValue("character", out var chars) ? chars.Split('\n') : [];
        _characters = ["", .. alphabet, " "];
        // Text detection is the expensive part; the GPU can afford a sharper look.
        _maxSide = OnGpu ? 1280 : 960;
    }

    public bool OnGpu { get; }

    public IReadOnlyList<TextLine> Read(BgraImage image)
    {
        var boxes = Detect(image);
        var lines = new List<TextLine>();
        foreach (var box in boxes.OrderByDescending(b => b.Width * b.Height).Take(MaxLines).OrderBy(b => b.Y).ThenBy(b => b.X))
        {
            if (box.Height > box.Width * 1.5)
                continue; // vertical text: rare in what we look for
            var (text, confidence) = ReadLine(image, box);
            if (text.Trim().Length > 0 && confidence >= 0.5f)
                lines.Add(new TextLine(text.Trim(), confidence, box.X, box.Y, box.Width, box.Height));
        }
        return lines;
    }

    readonly record struct Box(int X, int Y, int Width, int Height);

    List<Box> Detect(BgraImage image)
    {
        var scale = Math.Min(1.0, _maxSide / (double)Math.Max(image.Width, image.Height));
        var w = Math.Max(32, (int)Math.Round(image.Width * scale / 32) * 32);
        var h = Math.Max(32, (int)Math.Round(image.Height * scale / 32) * 32);
        var pixels = new float[3 * h * w];
        var input = new DenseTensor<float>(pixels, [1, 3, h, w]);
        var plane = w * h;
        OnDeviceModels.AreaResize(image, w, h, (x, y, b, g, r) =>
        {
            // BGR order, scaled to -1..1 (how PaddleOCR feeds OpenCV images).
            var i = y * w + x;
            pixels[i] = b / 127.5f - 1;
            pixels[plane + i] = g / 127.5f - 1;
            pixels[2 * plane + i] = r / 127.5f - 1;
        });
        using var results = _detect.Run([NamedOnnxValue.CreateFromTensor(_detect.InputMetadata.Keys.First(), input)]);
        var map = results[0].AsTensor<float>();
        var mh = map.Dimensions[2];
        var mw = map.Dimensions[3];
        var prob = map.ToArray();

        // Text pixels, slightly thickened (as PaddleOCR does) so letters join into lines.
        var on = new bool[mw * mh];
        for (var y = 0; y < mh; y++)
            for (var x = 0; x < mw; x++)
            {
                var i = y * mw + x;
                if (prob[i] > PixelThreshold
                    || (x + 1 < mw && prob[i + 1] > PixelThreshold)
                    || (y + 1 < mh && prob[i + mw] > PixelThreshold)
                    || (x + 1 < mw && y + 1 < mh && prob[i + mw + 1] > PixelThreshold))
                    on[i] = true;
            }

        // Each connected region is a line (or word) of text.
        var boxes = new List<Box>();
        var seen = new bool[on.Length];
        var stack = new Stack<int>();
        var sx = image.Width / (double)mw;
        var sy = image.Height / (double)mh;
        for (var start = 0; start < on.Length; start++)
        {
            if (!on[start] || seen[start])
                continue;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = 0, maxY = 0, count = 0;
            double sum = 0;
            stack.Push(start);
            seen[start] = true;
            while (stack.Count > 0)
            {
                var i = stack.Pop();
                int x = i % mw, y = i / mw;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                sum += prob[i];
                count++;
                if (x > 0 && on[i - 1] && !seen[i - 1]) { seen[i - 1] = true; stack.Push(i - 1); }
                if (x + 1 < mw && on[i + 1] && !seen[i + 1]) { seen[i + 1] = true; stack.Push(i + 1); }
                if (y > 0 && on[i - mw] && !seen[i - mw]) { seen[i - mw] = true; stack.Push(i - mw); }
                if (y + 1 < mh && on[i + mw] && !seen[i + mw]) { seen[i + mw] = true; stack.Push(i + mw); }
            }
            var bw = maxX - minX + 1;
            var bh = maxY - minY + 1;
            if (Math.Min(bw, bh) < 3 || sum / count < BoxThreshold)
                continue;
            var grow = bw * bh * Unclip / (2.0 * (bw + bh));
            var x0 = Math.Max(0, (minX - grow) * sx);
            var y0 = Math.Max(0, (minY - grow) * sy);
            var x1 = Math.Min(image.Width, (maxX + 1 + grow) * sx);
            var y1 = Math.Min(image.Height, (maxY + 1 + grow) * sy);
            if (x1 - x0 >= 4 && y1 - y0 >= 4)
                boxes.Add(new Box((int)x0, (int)y0, (int)(x1 - x0), (int)(y1 - y0)));
        }
        return boxes;
    }

    (string Text, float Confidence) ReadLine(BgraImage image, Box box)
    {
        var width = Math.Clamp((int)Math.Ceiling(LineHeight * box.Width / (double)box.Height), 16, 1600);
        width = (width + 7) / 8 * 8;
        var pixels = new float[3 * LineHeight * width];
        var input = new DenseTensor<float>(pixels, [1, 3, LineHeight, width]);
        var plane = LineHeight * width;
        var crop = new BgraImage(box.Width, box.Height, CropPixels(image, box));
        OnDeviceModels.AreaResize(crop, width, LineHeight, (x, y, b, g, r) =>
        {
            var i = y * width + x;
            pixels[i] = b / 127.5f - 1;
            pixels[plane + i] = g / 127.5f - 1;
            pixels[2 * plane + i] = r / 127.5f - 1;
        });
        using var results = _read.Run([NamedOnnxValue.CreateFromTensor(_read.InputMetadata.Keys.First(), input)]);
        var output = results[0].AsTensor<float>();
        var steps = output.Dimensions[1];
        var classes = output.Dimensions[2];
        var probs = output.ToArray();

        // Greedy CTC decoding: best character per step, dropping blanks and repeats.
        var text = new System.Text.StringBuilder();
        float total = 0;
        var kept = 0;
        var previous = -1;
        for (var t = 0; t < steps; t++)
        {
            int best = 0;
            var bestP = float.MinValue;
            var row = t * classes;
            for (var c = 0; c < classes; c++)
                if (probs[row + c] > bestP)
                {
                    bestP = probs[row + c];
                    best = c;
                }
            if (best != 0 && best != previous && best < _characters.Length)
            {
                text.Append(_characters[best]);
                total += bestP;
                kept++;
            }
            previous = best;
        }
        return (text.ToString(), kept == 0 ? 0 : total / kept);
    }

    static byte[] CropPixels(BgraImage image, Box box)
    {
        var result = new byte[box.Width * box.Height * 4];
        for (var y = 0; y < box.Height; y++)
            Buffer.BlockCopy(image.Pixels, (box.Y + y) * image.Stride + box.X * 4, result, y * box.Width * 4, box.Width * 4);
        return result;
    }

    public void Dispose()
    {
        _detect.Dispose();
        _read.Dispose();
    }
}
