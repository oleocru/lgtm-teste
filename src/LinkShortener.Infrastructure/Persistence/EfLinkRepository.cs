using LinkShortener.Application.Abstractions;
using LinkShortener.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinkShortener.Infrastructure.Persistence;

/// <summary>Implementação EF Core do repositório (SQL Server em produção).</summary>
public sealed class EfLinkRepository : ILinkRepository
{
    private readonly LinkShortenerDbContext _db;

    public EfLinkRepository(LinkShortenerDbContext db) => _db = db;

    public async Task AddAsync(ShortenedLink link, CancellationToken ct = default)
    {
        _db.Links.Add(link);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<ShortenedLink?> FindByCodeAsync(string domain, string shortCode, CancellationToken ct = default)
        => await _db.Links
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Domain == domain && l.ShortCode == shortCode, ct);

    public async Task<bool> ShortCodeExistsAsync(string domain, string shortCode, CancellationToken ct = default)
        => await _db.Links.AnyAsync(l => l.Domain == domain && l.ShortCode == shortCode, ct);

    /// <summary>
    /// Aplica deltas de cliques acumulados no batch. Uma única round-trip:
    /// UPDATE ... SET ClickCount = ClickCount @delta via ExecuteUpdate por chave.
    /// </summary>
    public async Task ApplyClickDeltasAsync(IReadOnlyDictionary<string, long> deltasByCacheKey, CancellationToken ct = default)
    {
        if (deltasByCacheKey.Count == 0) return;

        foreach (var (cacheKey, delta) in deltasByCacheKey)
        {
            var separatorIndex = cacheKey.IndexOf(':');
            if (separatorIndex <= 0) continue;

            var domain = cacheKey[..separatorIndex];
            var shortCode = cacheKey[(separatorIndex + 1)..];

            await _db.Links
                .Where(l => l.Domain == domain && l.ShortCode == shortCode)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.ClickCount, l => l.ClickCount + delta), ct);
        }
    }
}
