using Avalonia;
using Avalonia.Platform;
using SkiaSharp;
using AvaloniaBitmap = Avalonia.Media.Imaging.Bitmap;

namespace ImmichDesktop.App.Services;

/// <summary>
/// Image work done on this PC: decoding the right way up at a sensible size, rotating, and cropping
/// profile pictures to the JPEG that is uploaded to Immich.
/// </summary>
public static class Images
{
    /// <summary>Side of the square profile picture uploaded to Immich, in pixels.</summary>
    public const int ProfileSize = 512;

    /// <summary>
    /// Decodes an image the right way up, no larger than <paramref name="maxSize"/> on its long side.
    /// Phone photos are often stored sideways with an EXIF "rotate me" tag; browsers honor it, so
    /// we must too or the picture shows up sideways.
    /// </summary>
    public static SKBitmap DecodeUpright(byte[] data, int maxSize)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(data))
                          ?? throw new InvalidDataException("This file isn't a picture we can open. Try a JPEG, PNG or WebP.");

        // Let the decoder shrink big photos while decoding (much cheaper than decoding full size).
        var size = codec.Info.Size;
        var desired = Math.Min(1f, maxSize / (float)Math.Max(size.Width, size.Height));
        if (desired < 1f)
        {
            var scaled = codec.GetScaledDimensions(desired);
            if (scaled.Width >= size.Width * desired && scaled.Height >= size.Height * desired)
                size = scaled;
        }

        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            throw new InvalidDataException("This picture couldn't be read. Try a JPEG, PNG or WebP.");
        }

        bitmap = Shrink(bitmap, maxSize);
        var upright = Reorient(bitmap, codec.EncodedOrigin);
        if (!ReferenceEquals(upright, bitmap))
            bitmap.Dispose();
        return upright;
    }

    /// <summary>A copy turned a quarter turn clockwise.</summary>
    public static SKBitmap RotateClockwise(SKBitmap source) => Reorient(source, SKEncodedOrigin.RightTop);

    /// <summary>Renders the <paramref name="crop"/> area (in source pixels) to a square JPEG for uploading.</summary>
    public static byte[] RenderCrop(SKBitmap source, SKRect crop, int size = ProfileSize)
    {
        using var output = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(output))
        using (var image = SKImage.FromBitmap(source))
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
        {
            canvas.Clear(SKColors.White); // JPEG has no transparency
            canvas.DrawImage(image, crop, new SKRect(0, 0, size, size), paint);
        }
        using var encoded = SKImage.FromBitmap(output).Encode(SKEncodedImageFormat.Jpeg, 92);
        return encoded.ToArray();
    }

    /// <summary>Encodes a picture as JPEG.</summary>
    public static byte[] ToJpeg(SKBitmap bitmap, int quality = 90)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return encoded.ToArray();
    }

    /// <summary>Decodes for display: upright, no larger than <paramref name="maxSize"/> on its long side.</summary>
    public static AvaloniaBitmap DecodeForDisplay(byte[] data, int maxSize)
    {
        using var bitmap = DecodeUpright(data, maxSize);
        return ToAvalonia(bitmap);
    }

    /// <summary>Copies a decoded picture into an Avalonia bitmap for display.</summary>
    public static AvaloniaBitmap ToAvalonia(SKBitmap bitmap) =>
        new(PixelFormat.Bgra8888, AlphaFormat.Premul, bitmap.GetPixels(),
            new PixelSize(bitmap.Width, bitmap.Height), new Vector(96, 96), bitmap.RowBytes);

    static SKBitmap Shrink(SKBitmap bitmap, int maxSize)
    {
        var longSide = Math.Max(bitmap.Width, bitmap.Height);
        if (longSide <= maxSize)
            return bitmap;
        var scale = maxSize / (double)longSide;
        var info = new SKImageInfo(Math.Max(1, (int)Math.Round(bitmap.Width * scale)), Math.Max(1, (int)Math.Round(bitmap.Height * scale)),
            SKColorType.Bgra8888, SKAlphaType.Premul);
        var smaller = bitmap.Resize(info, SKFilterQuality.High);
        if (smaller is null)
            return bitmap;
        bitmap.Dispose();
        return smaller;
    }

    /// <summary>Applies an EXIF orientation, returning the source itself when it's already upright.</summary>
    static SKBitmap Reorient(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft)
            return source;

        float w = source.Width, h = source.Height;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        // Maps a source pixel to where it belongs in the upright picture.
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),     // mirrored
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1), // upside down
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),   // mirrored upside down
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),       // mirrored, turned
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),     // turn clockwise
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1), // mirrored, turned
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),   // turn counter-clockwise
            _ => SKMatrix.Identity,
        };

        var info = new SKImageInfo(swap ? source.Height : source.Width, swap ? source.Width : source.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var result = new SKBitmap(info);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(source, 0, 0);
        return result;
    }
}
