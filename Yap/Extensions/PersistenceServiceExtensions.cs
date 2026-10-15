using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Yap.Configuration;
using Yap.Services.Persistence;

namespace Yap.Extensions;

public static class PersistenceServiceExtensions
{
    /// <summary>
    /// Registers the DbContext factory when persistence is enabled, and the writer always.
    /// </summary>
    public static IServiceCollection AddChatPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<PersistenceSettings>(
            configuration.GetSection(PersistenceSettings.SectionName));

        var settings = configuration
            .GetSection(PersistenceSettings.SectionName)
            .Get<PersistenceSettings>() ?? new PersistenceSettings();

        if (settings.Enabled)
        {
            // A pooled factory: the singleton services create a context per use and give it back.
            services.AddPooledDbContextFactory<ChatDbContext>(options =>
                options.UseSqlite(settings.ConnectionStrings.SQLite));
        }

        // The writer is always registered. Without a DbContext factory its queue is a no-op.
        services.AddSingleton<PersistenceWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<PersistenceWriter>());

        return services;
    }

    /// <summary>
    /// Applies pending migrations when persistence is enabled. The startup loads run from Program.cs, in dependency order.
    /// </summary>
    public static async Task InitializePersistenceAsync(this IServiceProvider services)
    {
        var settings = services.GetRequiredService<IOptions<PersistenceSettings>>().Value;
        if (!settings.Enabled) return;

        // SQLite creates the file, not its folder.
        var filePath = settings.ConnectionStrings.SQLite.Replace("Data Source=", "");
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var dbFactory = services.GetRequiredService<IDbContextFactory<ChatDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }
}
