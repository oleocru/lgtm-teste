using LinkShortener.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinkShortener.Infrastructure.Persistence;

/// <summary>DbContext do banco relacional (SQL Server) que guarda os metadados dos links.</summary>
public class LinkShortenerDbContext : DbContext
{
    public LinkShortenerDbContext(DbContextOptions<LinkShortenerDbContext> options)
        : base(options)
    {
    }

    public DbSet<ShortenedLink> Links => Set<ShortenedLink>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var link = modelBuilder.Entity<ShortenedLink>();

        link.ToTable("ShortenedLinks");
        link.HasKey(l => l.Id);
        link.Property(l => l.Id).UseIdentityColumn();

        link.Property(l => l.ShortCode).HasMaxLength(16).IsRequired();
        link.Property(l => l.Domain).HasMaxLength(253).IsRequired();
        link.Property(l => l.OriginalUrl).HasMaxLength(2048).IsRequired();

        // Índice único composto: suporte a múltiplos domínios (dominio:shortcode).
        link.HasIndex(l => new { l.Domain, l.ShortCode }).IsUnique();
        link.HasIndex(l => l.CreatedAtUtc);

        // Propriedades com setters privados — mapeadas explicitamente.
        link.Ignore(nameof(ShortenedLink.BuildCacheKey));
    }
}
