using System.IO.Compression;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using ImmichDesktop.Core;

namespace ImmichDesktop.App.Services;

/// <summary>
/// A built-in world map (land and country borders from Natural Earth, public domain), so the map
/// always works without any online map service. Shapes are in Web Mercator coordinates from 0 to 1,
/// ready to be scaled to the current zoom.
/// </summary>
public static class WorldMap
{
    static readonly Lazy<(Geometry? Land, Geometry? Borders)> Shapes = new(Load);

    public static Geometry? Land => Shapes.Value.Land;
    public static Geometry? Borders => Shapes.Value.Borders;

    public const string Attribution = "Natural Earth";

    static (Geometry?, Geometry?) Load()
    {
        try
        {
            using var asset = AssetLoader.Open(new Uri("avares://ImmichDesktop/Assets/world-50m.bin.gz"));
            using var gzip = new GZipStream(asset, CompressionMode.Decompress);
            using var data = new MemoryStream();
            gzip.CopyTo(data);
            var reader = new Reader(data.ToArray());
            if (reader.Magic() != "IMWM1")
                return (null, null);
            double sx = reader.Double(), sy = reader.Double(), tx = reader.Double(), ty = reader.Double();
            Point Project(long x, long y)
            {
                var lon = x * sx + tx;
                var lat = Math.Clamp(y * sy + ty, -85.05112878, 85.05112878);
                var sin = Math.Sin(lat * Math.PI / 180);
                return new Point((lon + 180) / 360, 0.5 - Math.Log((1 + sin) / (1 - sin)) / (4 * Math.PI));
            }
            var land = Read(reader, Project, closed: true);
            var borders = Read(reader, Project, closed: false);
            return (land, borders);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not load the built-in world map", ex);
            return (null, null);
        }
    }

    static StreamGeometry Read(Reader reader, Func<long, long, Point> project, bool closed)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.SetFillRule(FillRule.EvenOdd); // lakes are holes in the land
        var count = reader.Int32();
        for (var i = 0; i < count; i++)
        {
            var points = (int)reader.VarInt();
            long x = 0, y = 0;
            for (var p = 0; p < points; p++)
            {
                x += reader.VarInt();
                y += reader.VarInt();
                var point = project(x, y);
                if (p == 0)
                    ctx.BeginFigure(point, isFilled: closed);
                else
                    ctx.LineTo(point);
            }
            if (points > 0)
                ctx.EndFigure(closed);
        }
        return geometry;
    }

    sealed class Reader(byte[] data)
    {
        int _pos;

        public string Magic()
        {
            var magic = System.Text.Encoding.ASCII.GetString(data, 0, 5);
            _pos = 5;
            return magic;
        }

        public double Double()
        {
            var value = BitConverter.ToDouble(data, _pos);
            _pos += 8;
            return value;
        }

        public int Int32()
        {
            var value = BitConverter.ToInt32(data, _pos);
            _pos += 4;
            return value;
        }

        /// <summary>Zigzag-encoded variable-length integer.</summary>
        public long VarInt()
        {
            ulong result = 0;
            var shift = 0;
            byte b;
            do
            {
                b = data[_pos++];
                result |= (ulong)(b & 0x7F) << shift;
                shift += 7;
            } while ((b & 0x80) != 0);
            return (long)(result >> 1) ^ -(long)(result & 1);
        }
    }
}
