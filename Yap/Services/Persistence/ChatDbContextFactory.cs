using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Yap.Services.Persistence;

/// <summary>The design-time factory for <c>dotnet ef migrations add</c>: the context without a running app.</summary>
public class ChatDbContextFactory : IDesignTimeDbContextFactory<ChatDbContext>
{
    public ChatDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ChatDbContext>().UseSqlite("Data Source=Data/yap.db").Options);
}
