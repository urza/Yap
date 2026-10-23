using System.Collections.Concurrent;
using SixLabors.ImageSharp;

namespace Yap.Services.Media;

/// <summary>
/// The pixel size of an uploaded picture or video poster, read from the file's header once and kept. The row
/// needs the size before the file loads: a picture without a box is 0 px tall until it arrives, and when such a
/// row sits at the top edge of the list it becomes the browser's scroll anchor and then grows below that edge,
/// which moves every row under it (the layout shift of 2026-10-23). Messages store bare URLs and the uploads of
/// every message before this change have no record of their size, so the file itself is the source. One read is
/// one header (ImageSharp Identify), and the answer never changes: an upload is never rewritten.
/// </summary>
public sealed class MediaSizes(UploadPaths uploads)
{
    private readonly ConcurrentDictionary<string, (int Width, int Height)?> _sizes = new();

    /// <summary>The size of the file behind an <c>/uploads/</c> URL, or null when there is no such file (the upload
    /// is gone, ffmpeg made no poster) or it is not a picture. A null is kept too: the file does not come back.</summary>
    public (int Width, int Height)? Of(string url) => _sizes.GetOrAdd(url, Read);

    private (int Width, int Height)? Read(string url)
    {
        const string prefix = "/uploads/";
        if (!url.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var relative = url[prefix.Length..];
        var query = relative.IndexOf('?');
        if (query >= 0) relative = relative[..query];
        var root = uploads.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.Ordinal) || !File.Exists(path)) return null;
        try
        {
            var info = Image.Identify(path);
            return info.Width > 0 && info.Height > 0 ? (info.Width, info.Height) : null;
        }
        catch (Exception)
        {
            return null;   // not a picture, or a damaged file: the row keeps its fallback box
        }
    }
}
