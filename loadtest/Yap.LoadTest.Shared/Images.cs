using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Yap.LoadTest.Shared;

/// <summary>
/// A photo-like JPEG of a random size: a gradient plus coloured blocks and noise, so it compresses like a real
/// picture (roughly 100 KB to 2 MB) and the server's thumbnail pass has real work.
/// </summary>
public static class Images
{
    public static byte[] RandomJpeg(Random rng)
    {
        var width = rng.Next(640, 2400);
        var height = (int)(width * rng.Next(55, 135) / 100.0);
        using var image = new Image<Rgb24>(width, height);
        var a = Color.FromRgb((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
        var b = Color.FromRgb((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
        image.Mutate(ctx =>
        {
            ctx.Fill(new LinearGradientBrush(new PointF(0, 0), new PointF(width, height), GradientRepetitionMode.None, new ColorStop(0, a), new ColorStop(1, b)));
            for (var i = 0; i < rng.Next(10, 40); i++)
            {
                var color = Color.FromRgba((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(80, 220));
                ctx.Fill(color, new RectangleF(rng.Next(width), rng.Next(height), rng.Next(20, width / 2), rng.Next(20, height / 2)));
            }
        });
        // Noise defeats the smooth-gradient compression and gives the file a photo's size.
        image.ProcessPixelRows(accessor =>
        {
            var noise = rng.Next(6, 24);
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x += 1 + rng.Next(3))
                {
                    var n = rng.Next(-noise, noise);
                    ref var p = ref row[x];
                    p.R = (byte)Math.Clamp(p.R + n, 0, 255); p.G = (byte)Math.Clamp(p.G + n, 0, 255); p.B = (byte)Math.Clamp(p.B + n, 0, 255);
                }
            }
        });
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = rng.Next(70, 95) });
        return ms.ToArray();
    }
}
