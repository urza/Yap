using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yap.Models;

namespace Yap.Data;

public class ChatDbContext : DbContext
{
    public DbSet<User> Users { get; set; } = null!;
    public DbSet<Channel> Channels { get; set; } = null!;
    public DbSet<ChatMessage> Messages { get; set; } = null!;
    public DbSet<Reaction> Reactions { get; set; } = null!;
    public DbSet<ChannelReadState> ChannelReadStates { get; set; } = null!;
    public DbSet<PushSubscription> PushSubscriptions { get; set; } = null!;

    public ChatDbContext(DbContextOptions<ChatDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // User configuration
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.HasIndex(u => u.Username).IsUnique();
            entity.HasIndex(u => u.Token).IsUnique();
            entity.Property(u => u.Username).HasMaxLength(32);
            entity.Property(u => u.DisplayName).HasMaxLength(64);
            entity.Property(u => u.Token).HasMaxLength(64);
            entity.Property(u => u.ProfilePictureUrl).HasMaxLength(256);
            entity.Property(u => u.Bio).HasMaxLength(150);

            // Ignore computed property
            entity.Ignore(u => u.EffectiveDisplayName);
        });

        // Channel configuration
        modelBuilder.Entity<Channel>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Type).HasConversion<int>();
            entity.HasIndex(c => new { c.Type, c.Name });
            entity.HasIndex(c => new { c.Participant1Id, c.Participant2Id });

            entity.HasMany(c => c.Messages)
                  .WithOne(m => m.Channel)
                  .HasForeignKey(m => m.ChannelId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(c => c.ReadStates)
                  .WithOne(rs => rs.Channel)
                  .HasForeignKey(rs => rs.ChannelId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.Creator)
                  .WithMany()
                  .HasForeignKey(c => c.CreatedById)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(c => c.Participant1User)
                  .WithMany()
                  .HasForeignKey(c => c.Participant1Id)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(c => c.Participant2User)
                  .WithMany()
                  .HasForeignKey(c => c.Participant2Id)
                  .OnDelete(DeleteBehavior.SetNull);

            // Ignore computed property
            entity.Ignore(c => c.IsDirectMessage);
        });

        // ChatMessage configuration
        modelBuilder.Entity<ChatMessage>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.HasIndex(m => m.ChannelId);
            entity.HasIndex(m => m.UserId);
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

            entity.HasOne(m => m.User)
                  .WithMany()
                  .HasForeignKey(m => m.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Ignore computed property
            entity.Ignore(m => m.HasImages);
        });

        // Reaction configuration
        modelBuilder.Entity<Reaction>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.HasIndex(r => new { r.MessageId, r.Emoji, r.UserId }).IsUnique();

            entity.HasOne(r => r.User)
                  .WithMany()
                  .HasForeignKey(r => r.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // ChannelReadState configuration (composite primary key)
        modelBuilder.Entity<ChannelReadState>(entity =>
        {
            entity.HasKey(rs => new { rs.UserId, rs.ChannelId });

            entity.HasOne(rs => rs.User)
                  .WithMany()
                  .HasForeignKey(rs => rs.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
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
