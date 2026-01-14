using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yap.Models;

namespace Yap.Data;

public class ChatDbContext : DbContext
{
    public DbSet<Channel> Channels { get; set; } = null!;
    public DbSet<ChatMessage> Messages { get; set; } = null!;
    public DbSet<Reaction> Reactions { get; set; } = null!;
    public DbSet<PushSubscription> PushSubscriptions { get; set; } = null!;

    public ChatDbContext(DbContextOptions<ChatDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Channel configuration
        modelBuilder.Entity<Channel>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Type).HasConversion<int>();
            entity.HasIndex(c => new { c.Type, c.Name });
            entity.HasIndex(c => new { c.Participant1, c.Participant2 });

            entity.HasMany(c => c.Messages)
                  .WithOne(m => m.Channel)
                  .HasForeignKey(m => m.ChannelId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Ignore computed property
            entity.Ignore(c => c.IsDirectMessage);
        });

        // ChatMessage configuration
        modelBuilder.Entity<ChatMessage>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.HasIndex(m => m.ChannelId);
            entity.HasIndex(m => m.Timestamp);
            entity.HasIndex(m => new { m.ChannelId, m.Timestamp });

            // Store ImageUrls as JSON
            entity.Property(m => m.ImageUrls).HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>()
            );

            entity.HasMany(m => m.Reactions)
                  .WithOne(r => r.Message)
                  .HasForeignKey(r => r.MessageId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Ignore computed property
            entity.Ignore(m => m.HasImages);
        });

        // Reaction configuration
        modelBuilder.Entity<Reaction>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.HasIndex(r => new { r.MessageId, r.Emoji, r.Username }).IsUnique();
        });

        // PushSubscription configuration
        modelBuilder.Entity<PushSubscription>(entity =>
        {
            entity.HasKey(p => p.Endpoint);
            entity.Property(p => p.Endpoint).HasMaxLength(2048);
            entity.HasIndex(p => p.Username);
        });
    }
}
