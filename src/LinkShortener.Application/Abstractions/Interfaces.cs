using LinkShortener.Domain.Entities;

namespace LinkShortener.Application.Abstractions;

/// <summary>Repositório de metadados no banco relacional (SQL Server via EF Core).</summary>
public interface ILinkRepository
{
    Task AddAsync(ShortenedLink link, CancellationToken ct = default);
    Task<ShortenedLink?> FindByCodeAsync(string domain, string shortCode, CancellationToken ct = default);
    Task<bool> ShortCodeExistsAsync(string domain, string shortCode, CancellationToken ct = default);

    /// <summary>Aplica deltas de cliques acumulados em lote (usado pelo ClickCounterService).</summary>
    Task ApplyClickDeltasAsync(IReadOnlyDictionary<string, long> deltasByCacheKey, CancellationToken ct = default);
}

/// <summary>
/// Cache de alta performance (Redis via StackExchange.Redis).
/// Chave composta "dominio:shortcode" -> OriginalUrl. Coração da latência < 10ms.
/// </summary>
public interface IRedirectCache
{
    Task<string?> TryGetOriginalUrlAsync(string domain, string shortCode, CancellationToken ct = default);
    Task WarmUpAsync(string domain, string shortCode, string originalUrl, TimeSpan? ttl, CancellationToken ct = default);
    Task EvictAsync(string domain, string shortCode, CancellationToken ct = default);
}

/// <summary>Produtor assíncrono de eventos de clique (Channel&lt;T&gt; em memória / Redis Streams).</summary>
public interface IClickEventPublisher
{
    void Publish(ClickEvent clickEvent);
}

/// <summary>Consumidor dos eventos de clique, processados em lote para não gargalar o redirecionamento.</summary>
public interface IClickEventConsumer
{
    ValueTask<IReadOnlyList<ClickEvent>> ReadAllAvailableAsync(CancellationToken ct = default);
    ValueTask<IReadOnlyList<ClickEvent>> WaitForBatchAsync(TimeSpan maxWait, CancellationToken ct = default);
}

public readonly record struct ClickEvent(string Domain, string ShortCode, DateTimeOffset TimestampUtc);

/// <summary>Gera identificadores curtos únicos (Base62) sem loops de busca no banco.</summary>
public interface IShortCodeGenerator
{
    /// <summary>
    /// Gera um short code único para o domínio. Implementações devem garantir unicidade
    /// (ex.: contador sequencial codificado em Base62, com verificação de colisão rara).
    /// </summary>
    Task<string> GenerateAsync(string domain, CancellationToken ct = default);
}

/// <summary>Configuração de domínio/URL base usada para montar o link encurtado retornado.</summary>
public interface IBaseUrlProvider
{
    string GetBaseUrl(string requestHost);
}
