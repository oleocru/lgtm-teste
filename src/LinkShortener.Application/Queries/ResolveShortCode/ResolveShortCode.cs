using LinkShortener.Application.Abstractions;
using MediatR;

namespace LinkShortener.Application.Queries.ResolveShortCode;

/// <summary>
/// CQRS Query: resolver um short code para redirecionamento (caminho crítico de leitura).
/// Ordem: Redis (cache hit) -> SQL Server (cache miss, repovoa Redis) -> NotFound.
/// </summary>
public sealed record ResolveShortCodeQuery(
    string Domain,
    string ShortCode,
    bool PermanentRedirect = false) : IRequest<ResolveShortCodeResult>;

public enum ResolveStatus
{
    Success,
    NotFound,
    ExpiredOrInactive
}

public sealed record ResolveShortCodeResult(
    ResolveStatus Status,
    string? OriginalUrl,
    long ClickCount,
    bool FromCache);

public sealed class ResolveShortCodeHandler
{
    private readonly IRedirectCache _cache;
    private readonly ILinkRepository _repository;
    private readonly IClickEventPublisher _clicks;
    private readonly TimeProvider _timeProvider;

    /// <summary>TTL padrão do cache para entradas repovadas via cache miss.</summary>
    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromHours(12);

    public ResolveShortCodeHandler(
        IRedirectCache cache,
        ILinkRepository repository,
        IClickEventPublisher clicks,
        TimeProvider timeProvider)
    {
        _cache = cache;
        _repository = repository;
        _clicks = clicks;
        _timeProvider = timeProvider;
    }

    public async Task<ResolveShortCodeResult> Handle(
        ResolveShortCodeQuery request,
        CancellationToken cancellationToken)
    {
        var domain = request.Domain.Trim().ToLowerInvariant();
        var shortCode = request.ShortCode;

        // 1. Cache Hit: busca imediata no Redis usando a chave "dominio:shortcode".
        var cachedUrl = await _cache.TryGetOriginalUrlAsync(domain, shortCode, cancellationToken);

        if (cachedUrl is not null)
        {
            // Clique contabilizado de forma assíncrona (Channel<T>) — nunca bloqueia o redirect.
            PublishClick(domain, shortCode);
            return new ResolveShortCodeResult(ResolveStatus.Success, cachedUrl, 0, FromCache: true);
        }

        // 2. Cache Miss: busca no banco relacional e repovoa o Redis.
        var link = await _repository.FindByCodeAsync(domain, shortCode, cancellationToken);

        if (link is null)
            return new ResolveShortCodeResult(ResolveStatus.NotFound, null, 0, FromCache: false);

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        if (!link.IsActive || link.IsExpired(nowUtc))
        {
            // Remove do cache para não servir links inválidos em futuras requisições.
            await _cache.EvictAsync(domain, shortCode, cancellationToken);
            return new ResolveShortCodeResult(ResolveStatus.ExpiredOrInactive, null, link.ClickCount, FromCache: false);
        }

        TimeSpan? ttl = link.ExpiresAtUtc.HasValue
            ? link.ExpiresAtUtc.Value - nowUtc
            : DefaultCacheTtl;

        await _cache.WarmUpAsync(domain, shortCode, link.OriginalUrl, ttl, cancellationToken);

        PublishClick(domain, shortCode);
        return new ResolveShortCodeResult(ResolveStatus.Success, link.OriginalUrl, link.ClickCount, FromCache: false);
    }

    private void PublishClick(string domain, string shortCode) =>
        _clicks.Publish(new ClickEvent(domain, shortCode, _timeProvider.GetUtcNow()));
}
