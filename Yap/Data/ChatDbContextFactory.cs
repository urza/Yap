using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Yap.Data;

/// <summary>
/// Design-time factory for EF Core migrations.
/// Used by 'dotnet ef migrations add' command.
/// </summary>
public class ChatDbContextFactory : IDesignTimeDbContextFactory<ChatDbContext>
{
    public ChatDbContext CreateDbContext(string[] args)
    {
        // Check if Postgres provider is specified
        var usePostgres = args.Any(a => a.Contains("postgres", StringComparison.OrdinalIgnoreCase));

        var optionsBuilder = new DbContextOptionsBuilder<ChatDbContext>();

        if (usePostgres)
        {
            optionsBuilder.UseNpgsql("Host=localhost;Database=yap;Username=yap;Password=yap");
        }
        else
        {
            // Default to SQLite
            optionsBuilder.UseSqlite("Data Source=Data/yap.db");
        }

        return new ChatDbContext(optionsBuilder.Options);
    }
}
