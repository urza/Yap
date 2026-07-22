using Yap.Services;

namespace Yap.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin").RequireAdmin();

        group.MapGet("/generate-thumbnails", (ImageService imageService, IWebHostEnvironment env) =>
        {
            var uploadsPath = Path.Combine(env.WebRootPath, "uploads");
            if (!Directory.Exists(uploadsPath))
                return Results.Ok(new { message = "No uploads folder" });

            var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };

            var originalImages = Directory.GetFiles(uploadsPath)
                .Where(f => allowedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Where(f => !Path.GetFileNameWithoutExtension(f).EndsWith("_800px"))
                .Where(f => !Path.GetFileNameWithoutExtension(f).EndsWith("_1600px"))
                .ToList();

            var toProcess = originalImages
                .Where(f => !File.Exists(Path.Combine(uploadsPath, $"{Path.GetFileNameWithoutExtension(f)}_800px.webp")))
                .ToList();

            _ = Task.Run(async () =>
            {
                var processed = 0;
                var failed = 0;

                await Parallel.ForEachAsync(toProcess, new ParallelOptions { MaxDegreeOfParallelism = 12 },
                    async (imagePath, _) =>
                    {
                        try
                        {
                            await imageService.GenerateThumbnailsAsync(imagePath);
                            var count = Interlocked.Increment(ref processed);
                            Console.WriteLine($"[Thumbnails] {count}/{toProcess.Count}: {Path.GetFileName(imagePath)}");
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref failed);
                            Console.WriteLine($"[Thumbnails] Failed: {Path.GetFileName(imagePath)} - {ex.Message}");
                        }
                    });

                Console.WriteLine($"[Thumbnails] Complete: {processed} processed, {failed} failed");
            });

            return Results.Ok(new { message = "Processing started in background", toProcess = toProcess.Count, total = originalImages.Count });
        });
    }
}
