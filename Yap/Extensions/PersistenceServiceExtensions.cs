using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Yap.Configuration;
using Yap.Data;
using Yap.Services;

namespace Yap.Extensions;

public static class PersistenceServiceExtensions
{
    /// <summary>
    /// Adds chat persistence services based on configuration.
    /// When enabled, registers DbContext and ChatPersistenceService.
    /// When disabled, registers a no-op ChatPersistenceService.
    /// </summary>
    public static IServiceCollection AddChatPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Bind configuration
        services.Configure<PersistenceSettings>(
            configuration.GetSection(PersistenceSettings.SectionName));

        var settings = configuration
            .GetSection(PersistenceSettings.SectionName)
            .Get<PersistenceSettings>() ?? new PersistenceSettings();

        if (settings.Enabled)
        {
            // Get connection string based on provider
            var connectionString = settings.Provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase)
                ? settings.ConnectionStrings.Postgres
                : settings.ConnectionStrings.SQLite;

            // Register pooled DbContextFactory for singleton services (like ChatService)
            // Pooled factory is singleton-compatible and more efficient
            services.AddPooledDbContextFactory<ChatDbContext>(options =>
            {
                if (settings.Provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
                {
                    options.UseNpgsql(connectionString);
                }
                else
                {
                    options.UseSqlite(connectionString);
                }
            });
        }

        // Always register ChatPersistenceService (it handles enabled/disabled internally)
        services.AddSingleton<ChatPersistenceService>();

        return services;
    }

    /// <summary>
    /// Applies pending migrations and initializes data if persistence is enabled.
    /// </summary>
    public static async Task InitializePersistenceAsync(this IServiceProvider services)
    {
        var settings = services.GetRequiredService<IOptions<PersistenceSettings>>().Value;

        if (!settings.Enabled)
        {
            return;
        }

        // Apply migrations using factory
        var dbFactory = services.GetRequiredService<IDbContextFactory<ChatDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        // Ensure Data directory exists for SQLite
        if (settings.Provider.Equals("SQLite", StringComparison.OrdinalIgnoreCase))
        {
            var dbPath = settings.ConnectionStrings.SQLite;
            if (dbPath.Contains("Data Source="))
            {
                var filePath = dbPath.Replace("Data Source=", "");
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
            }
        }

        // Apply migrations if they exist, otherwise create database from model
        var appliedMigrations = await db.Database.GetAppliedMigrationsAsync();
        var pendingMigrations = await db.Database.GetPendingMigrationsAsync();

        if (appliedMigrations.Any() || pendingMigrations.Any())
        {
            // Migrations exist - use migration system
            await db.Database.MigrateAsync();
        }
        else
        {
            // No migrations defined - create database directly from model (dev scenario)
            await db.Database.EnsureCreatedAsync();
        }

        // Initialize ChatService with data from DB
        var chatService = services.GetRequiredService<ChatService>();
        await chatService.InitializeAsync();
    }
}
