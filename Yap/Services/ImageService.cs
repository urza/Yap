using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace Yap.Services;

public class ImageService
{
    // Available sizes for different use cases
    public const int SizeMedium = 800;  // Gallery and single image preview
    public const int SizeLarge = 1600;  // Lightbox (caps very large uploads)

    private static readonly int[] AllSizes = [SizeMedium, SizeLarge];

    // All thumbnails are WebP for best compression
    private static readonly WebpEncoder WebpEncoder = new() { Quality = 90 };

    // Skip processing for small files (already optimized)
    private const int SmallFileThreshold = 500 * 1024; // 500 KB

    /// <summary>
    /// Generates all thumbnail sizes as WebP files.
    /// For small files (under 500KB), converts to WebP without resizing.
    /// </summary>
    public async Task GenerateThumbnailsAsync(string originalPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(originalPath)!;
            var filename = Path.GetFileNameWithoutExtension(originalPath);
            var originalExtension = Path.GetExtension(originalPath).ToLowerInvariant();
            var fileSize = new FileInfo(originalPath).Length;

            // For small files, convert to WebP without resizing
            var skipResizing = fileSize < SmallFileThreshold;

            using var image = await Image.LoadAsync(originalPath);

            // Apply EXIF orientation (rotate photos from cameras/phones correctly)
            image.Mutate(x => x.AutoOrient());

            var originalWidth = image.Width;

            foreach (var size in AllSizes)
            {
                // All thumbnails are WebP
                var thumbPath = Path.Combine(directory, $"{filename}_{size}px.webp");

                if (skipResizing || originalWidth <= size)
                {
                    // Just convert to WebP without resizing
                    await image.SaveAsync(thumbPath, WebpEncoder);
                }
                else
                {
                    // Use different resamplers based on original format:
                    // - Lanczos3 for photos (jpg/webp) - smooth gradients
                    // - Box for screenshots/graphics (png/gif) - preserves sharp edges
                    var sampler = originalExtension is ".png" or ".gif"
                        ? KnownResamplers.Box
                        : KnownResamplers.Lanczos3;

                    using var resized = image.Clone(x => x.Resize(new ResizeOptions
                    {
                        Size = new Size(size, 0), // 0 = maintain aspect ratio
                        Mode = ResizeMode.Max,
                        Sampler = sampler
                    }));

                    await resized.SaveAsync(thumbPath, WebpEncoder);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to generate thumbnails for {originalPath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the URL for a specific size variant (always WebP).
    /// Convention: /uploads/image.jpg -> /uploads/image_800px.webp
    /// </summary>
    public static string GetSizedUrl(string originalUrl, int size)
    {
        var lastDot = originalUrl.LastIndexOf('.');
        if (lastDot < 0) return originalUrl;

        return $"{originalUrl[..lastDot]}_{size}px.webp";
    }

    // Convenience methods for common sizes
    public static string GetMediumUrl(string originalUrl) => GetSizedUrl(originalUrl, SizeMedium);
    public static string GetLargeUrl(string originalUrl) => GetSizedUrl(originalUrl, SizeLarge);
}
