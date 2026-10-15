namespace ImmichSplash.Core.Api;

/// <summary>One step of an edit Immich applies to the original photo (server side, to the full-quality file).</summary>
public abstract record ImageEdit(string Action);

/// <summary>Keeps a rectangle of the photo, in pixels of the photo as it is at this point of the edit.</summary>
public sealed record CropEdit(int X, int Y, int Width, int Height) : ImageEdit("crop");

/// <summary>Turns the photo clockwise: 90, 180 or 270 degrees.</summary>
public sealed record RotateEdit(int Angle) : ImageEdit("rotate");

/// <summary>Flips the photo: "horizontal" swaps left and right, "vertical" swaps top and bottom.</summary>
public sealed record MirrorEdit(string Axis) : ImageEdit("mirror");

/// <summary>Turns what the editor shows (turned, flipped and cropped on screen) into the steps Immich applies.</summary>
public static class ImageEditPlan
{
    /// <summary>
    /// The steps in the order they are applied: turn, flip, then crop. The crop is described on the photo as
    /// it looks after turning and flipping, which is what you see on screen.
    /// </summary>
    /// <param name="width">The original photo's width in pixels.</param>
    /// <param name="height">The original photo's height in pixels.</param>
    /// <param name="rotation">Clockwise, 0, 90, 180 or 270.</param>
    /// <param name="cropX">The crop on the turned photo, as fractions from 0 to 1.</param>
    public static List<ImageEdit> Build(int width, int height, int rotation, bool mirrorHorizontal, bool mirrorVertical,
        double cropX, double cropY, double cropWidth, double cropHeight)
    {
        var edits = new List<ImageEdit>();
        rotation = ((rotation % 360) + 360) % 360;
        if (rotation != 0)
            edits.Add(new RotateEdit(rotation));
        if (mirrorHorizontal)
            edits.Add(new MirrorEdit("horizontal"));
        if (mirrorVertical)
            edits.Add(new MirrorEdit("vertical"));

        var (w, h) = rotation is 90 or 270 ? (height, width) : (width, height);
        var full = cropX <= 0.001 && cropY <= 0.001 && cropWidth >= 0.998 && cropHeight >= 0.998;
        if (!full && w > 0 && h > 0)
        {
            var x = Math.Clamp((int)Math.Round(cropX * w), 0, w - 1);
            var y = Math.Clamp((int)Math.Round(cropY * h), 0, h - 1);
            var cw = Math.Clamp((int)Math.Round(cropWidth * w), 1, w - x);
            var ch = Math.Clamp((int)Math.Round(cropHeight * h), 1, h - y);
            edits.Add(new CropEdit(x, y, cw, ch));
        }
        return edits;
    }
}
