namespace ImmichSplash.Core.Api;

/// <summary>One step of an edit Immich applies to the original photo (server side, to the full-quality file).</summary>
public abstract record ImageEdit(string Action);

/// <summary>Keeps a rectangle of the original photo (Immich always crops first, before turning or flipping).</summary>
public sealed record CropEdit(int X, int Y, int Width, int Height) : ImageEdit("crop");

/// <summary>Turns the photo clockwise: 90, 180 or 270 degrees.</summary>
public sealed record RotateEdit(int Angle) : ImageEdit("rotate");

/// <summary>Flips the photo: "horizontal" swaps left and right, "vertical" swaps top and bottom.</summary>
public sealed record MirrorEdit(string Axis) : ImageEdit("mirror");

/// <summary>Turns what the editor shows (turned, flipped and cropped on screen) into the steps Immich applies.</summary>
public static class ImageEditPlan
{
    /// <summary>
    /// The steps Immich applies: the crop (the server always does it first, on the original photo), then the
    /// turn, then the flips, in that order, which is how the editor draws them. The crop is chosen on the photo
    /// as it looks after turning and flipping, so it's mapped back onto the original here.
    /// </summary>
    /// <param name="width">The original photo's width in pixels (the right way up).</param>
    /// <param name="height">The original photo's height in pixels.</param>
    /// <param name="rotation">Clockwise, 0, 90, 180 or 270.</param>
    /// <param name="cropX">The crop on the turned and flipped photo, as fractions from 0 to 1.</param>
    public static List<ImageEdit> Build(int width, int height, int rotation, bool mirrorHorizontal, bool mirrorVertical,
        double cropX, double cropY, double cropWidth, double cropHeight)
    {
        var edits = new List<ImageEdit>();
        rotation = ((rotation % 360) + 360) % 360;

        var full = cropX <= 0.001 && cropY <= 0.001 && cropWidth >= 0.998 && cropHeight >= 0.998;
        if (!full && width > 0 && height > 0)
        {
            // Undo the flips (they come last), then the turn, to find the rectangle on the original.
            if (mirrorHorizontal)
                cropX = 1 - cropX - cropWidth;
            if (mirrorVertical)
                cropY = 1 - cropY - cropHeight;
            var (fx, fy, fw, fh) = rotation switch
            {
                90 => (cropY, 1 - cropX - cropWidth, cropHeight, cropWidth),
                180 => (1 - cropX - cropWidth, 1 - cropY - cropHeight, cropWidth, cropHeight),
                270 => (1 - cropY - cropHeight, cropX, cropHeight, cropWidth),
                _ => (cropX, cropY, cropWidth, cropHeight),
            };
            var x = Math.Clamp((int)Math.Round(fx * width), 0, width - 1);
            var y = Math.Clamp((int)Math.Round(fy * height), 0, height - 1);
            var cw = Math.Clamp((int)Math.Round(fw * width), 1, width - x);
            var ch = Math.Clamp((int)Math.Round(fh * height), 1, height - y);
            edits.Add(new CropEdit(x, y, cw, ch));
        }
        if (rotation != 0)
            edits.Add(new RotateEdit(rotation));
        if (mirrorHorizontal)
            edits.Add(new MirrorEdit("horizontal"));
        if (mirrorVertical)
            edits.Add(new MirrorEdit("vertical"));
        return edits;
    }
}
