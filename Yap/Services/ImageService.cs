using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace Yap.Services;

public class ImageService
{
    // Available sizes for different use cases
    public const int SizeSmall = 200;   // Gallery thumbnails (multi-image rows)
    public const int SizeMedium = 400;  // Single image preview
    public const int SizeLarge = 1600;  // Lightbox (caps very large uploads)

    private static readonly int[] AllSizes = [SizeSmall, SizeMedium, SizeLarge];

    /// <summary>
    /// Generates all thumbnail sizes for the given image file.
    /// If original is smaller than a target size, copies original to that size filename.
    /// </summary>
    public async Task GenerateThumbnailsAsync(string originalPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(originalPath)!;
            var filename = Path.GetFileNameWithoutExtension(originalPath);
            var extension = Path.GetExtension(originalPath);

            using var image = await Image.LoadAsync(originalPath);
            var originalWidth = image.Width;

            foreach (var size in AllSizes)
            {
                var thumbPath = Path.Combine(directory, $"{filename}_{size}px{extension}");

                if (originalWidth <= size)
                {
                    // Original is smaller than target - just copy original
                    File.Copy(originalPath, thumbPath, overwrite: true);
                }
                else
                {
                    // Resize to target size
                    var resized = image.Clone(x => x.Resize(new ResizeOptions
                    {
                        Size = new Size(size, 0), // 0 = maintain aspect ratio
                        Mode = ResizeMode.Max
                    }));

                    await resized.SaveAsync(thumbPath);
                    resized.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to generate thumbnails for {originalPath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the URL for a specific size variant.
    /// Convention: /uploads/image.jpg -> /uploads/image_400px.jpg
    /// </summary>
    public static string GetSizedUrl(string originalUrl, int size)
    {
        var lastDot = originalUrl.LastIndexOf('.');
        if (lastDot < 0) return originalUrl;

        return $"{originalUrl[..lastDot]}_{size}px{originalUrl[lastDot..]}";
    }

    // Convenience methods for common sizes
    public static string GetSmallUrl(string originalUrl) => GetSizedUrl(originalUrl, SizeSmall);
    public static string GetMediumUrl(string originalUrl) => GetSizedUrl(originalUrl, SizeMedium);
    public static string GetLargeUrl(string originalUrl) => GetSizedUrl(originalUrl, SizeLarge);
}
