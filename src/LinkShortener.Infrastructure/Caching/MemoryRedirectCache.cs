using System.Threading.Channels;
using LinkShortener.Application.Abstractions;

namespace LinkShortener.Infrastructure.Caching;

/// <summary>
/// Cache de redirecionamento em memória (MemoryRedirectCache) — fallback quando o Redis
/// não está configurado (desenvolvimento/testes). Mesma semântica de chave "dominio:shortcode".
/// </summary>
public sealed class MemoryRedirectCache : IRedirectCache
{
    private sealed record Entry(string OriginalUrl, DateTime? ExpiresAtUtc);

    private readonly Dictionary<string, Entry> _store = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    private static string BuildKey(string domain, string shortCode) =>
        $"{domain.ToLowerInvariant()}:{shortCode}";

    public Task<string?> TryGetOriginalUrlAsync(string domain, string shortCode, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_store.TryGetValue(BuildKey(domain, shortCode), out var entry))
            {
                if (entry.ExpiresAtUtc is null || entry.ExpiresAtUtc > DateTime.UtcNow)
                    return Task.FromResult<string?>(entry.OriginalUrl);

                _store.Remove(BuildKey(domain, shortCode));
            }
        }

        return Task.FromResult<string?>(null);
    }

    public Task WarmUpAsync(string domain, string shortCode, string originalUrl, TimeSpan? ttl, CancellationToken ct = default)
    {
        var expiresAt = ttl.HasValue ? DateTime.UtcNow.Add(ttl.Value) : (DateTime?)null;
        lock (_lock)
        {
            _store[BuildKey(domain, shortCode)] = new Entry(originalUrl, expiresAt);
        }

        return Task.CompletedTask;
    }

    public Task EvictAsync(string domain, string shortCode, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _store.Remove(BuildKey(domain, shortCode));
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Fila em memória baseada em Channel&lt;ClickEvent&gt; (unbounded) para contabilizar
/// cliques de forma assíncrona e em lote, sem escrever no banco a cada redirecionamento.
/// </summary>
public sealed class ChannelClickQueue : IClickEventPublisher, IClickEventConsumer
{
    private readonly Channel<ClickEvent> _channel =
        Channel.CreateUnbounded<ClickEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    public void Publish(ClickEvent clickEvent) => _channel.Writer.TryWrite(clickEvent);

    public async ValueTask<IReadOnlyList<ClickEvent>> ReadAllAvailableAsync(CancellationToken ct = default)
    {
        var batch = new List<ClickEvent>();
        while (await _channel.Reader.WaitToReadAsync(ct) && _channel.Reader.TryPeek(out _))
        {
            while (_channel.Reader.TryRead(out var click))
                batch.Add(click);
            break;
        }

        return batch;
    }

    public async ValueTask<IReadOnlyList<ClickEvent>> WaitForBatchAsync(TimeSpan maxWait, CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(maxWait);

        var batch = new List<ClickEvent>();
        try
        {
            // Espera pelo menos um evento ou o fim da janela de batch.
            await _channel.Reader.WaitToReadAsync(timeoutCts.Token);

            while (_channel.Reader.TryRead(out var click))
            {
                batch.Add(click);
                if (batch.Count >= 10_000) break; // teto por lote
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout do batch — devolve o que tiver (pode estar vazio).
            while (_channel.Reader.TryRead(out var click))
                batch.Add(click);
        }

        return batch;
    }
}
