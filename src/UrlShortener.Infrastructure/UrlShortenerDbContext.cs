using Microsoft.EntityFrameworkCore;
using UrlShortener.Domain;

namespace UrlShortener.Infrastructure;

public class UrlShortenerDbContext(DbContextOptions<UrlShortenerDbContext> options) : DbContext(options)
{
    public DbSet<ShortUrl> ShortUrls => Set<ShortUrl>();
    public DbSet<ClickEvent> ClickEvents => Set<ClickEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ShortUrl>(entity =>
        {
            entity.HasKey(e => e.Code);
            entity.Property(e => e.Code).HasMaxLength(64);
            entity.Property(e => e.OriginalUrl).IsRequired().HasMaxLength(2048);
            entity.Property(e => e.IdempotencyKey).HasMaxLength(128);
            entity.HasIndex(e => e.IdempotencyKey).IsUnique().HasFilter("IdempotencyKey IS NOT NULL");
        });

        modelBuilder.Entity<ClickEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Code).IsRequired().HasMaxLength(64);
            entity.HasIndex(e => e.Code);
        });
    }
}
