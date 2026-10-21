using LinkShortener.Application.Abstractions;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace LinkShortener.Infrastructure.Caching;

/// <summary>
/// Cache de redirecionamento em Redis (StackExchange.Redis).
/// Chave: "ls:{dominio}:{shortcode}" -> valor: OriginalUrl.
/// TTL padrão para evitar chaves eternas; links com expiração usam TTL igual ao tempo restante.
/// Falhas de rede nunca derrubam o redirect: degradamos para o banco (cache-aside resiliente).
/// </summary>
public sealed class RedisRedirectCache : IRedirectCache
{
    private const string KeyPrefix = "ls:";
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(12);

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisRedirectCache> _logger;

    public RedisRedirectCache(IConnectionMultiplexer redis, ILogger<RedisRedirectCache> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    private static string BuildKey(string domain, string shortCode) =>
        $"{KeyPrefix}{domain.ToLowerInvariant()}:{shortCode}";

    public async Task<string?> TryGetOriginalUrlAsync(string domain, string shortCode, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            var value = await db.StringGetAsync(BuildKey(domain, shortCode));
            return value.IsNullOrEmpty ? null : value.ToString();
        }
        catch (Exception ex)
        {
            // Redis indisponível: cache miss forçado -> caminho do banco.
            _logger.LogWarning(ex, "Falha no Redis ao resolver {Domain}:{ShortCode}. Degradando para o banco.", domain, shortCode);
            return null;
        }
    }

    public async Task WarmUpAsync(string domain, string shortCode, string originalUrl, TimeSpan? ttl, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            await db.StringSetAsync(BuildKey(domain, shortCode), originalUrl, ttl ?? DefaultTtl);
        }
        catch (Exception ex)
        {
            // Warm-up é best-effort: o cache miss posterior repovoa a chave.
            _logger.LogWarning(ex, "Falha no warm-up do Redis para {Domain}:{ShortCode}.", domain, shortCode);
        }
    }

    public async Task EvictAsync(string domain, string shortCode, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            await db.KeyDeleteAsync(BuildKey(domain, shortCode));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao remover chave do Redis para {Domain}:{ShortCode}.", domain, shortCode);
        }
    }
}
