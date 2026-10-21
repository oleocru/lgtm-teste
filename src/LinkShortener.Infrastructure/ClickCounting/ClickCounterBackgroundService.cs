using LinkShortener.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinkShortener.Infrastructure.ClickCounting;

/// <summary>
/// BackgroundService que consome a fila de cliques (Channel&lt;T&gt;) e grava os deltas
/// no banco em lote (batch), evitando uma escrita por redirecionamento.
/// Estratégia: aguarda até 'BatchWindow' ou acumular 'MaxBatchSize' eventos, agrega
/// por chave "dominio:shortcode" e aplica UPDATE ... ClickCount = ClickCount + delta.
/// </summary>
public sealed class ClickCounterBackgroundService : BackgroundService
{
    private static readonly TimeSpan BatchWindow = TimeSpan.FromSeconds(5);
    private const int MaxBatchSize = 10_000;

    private readonly IClickEventConsumer _consumer;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ClickCounterBackgroundService> _logger;

    public ClickCounterBackgroundService(
        IClickEventConsumer consumer,
        IServiceScopeFactory scopeFactory,
        ILogger<ClickCounterBackgroundService> logger)
    {
        _consumer = consumer;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ClickCounterBackgroundService iniciado (janela de batch: {Window}).", BatchWindow);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var clicks = await _consumer.WaitForBatchAsync(BatchWindow, stoppingToken);
                if (clicks.Count == 0) continue;

                // Agrega deltas por chave composta dominio:shortcode.
                var deltas = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var click in clicks)
                {
                    var key = $"{click.Domain.ToLowerInvariant()}:{click.ShortCode}";
                    deltas.TryGetValue(key, out var current);
                    deltas[key] = current + 1;
                }

                using var scope = _scopeFactory.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<ILinkRepository>();

                await repository.ApplyClickDeltasAsync(deltas, stoppingToken);

                _logger.LogDebug("Batch de {Count} cliques aplicado em {Keys} chaves.", clicks.Count, deltas.Count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Nunca derruba o serviço por falha transitória do banco.
                _logger.LogError(ex, "Falha ao aplicar batch de cliques. Tentando novamente.");
                await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
            }
        }

        // Flush final: grava o que ficou na fila antes do shutdown.
        try
        {
            var remaining = await _consumer.ReadAllAvailableAsync(CancellationToken.None);
            if (remaining.Count > 0)
            {
                var deltas = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var click in remaining)
                {
                    var key = $"{click.Domain.ToLowerInvariant()}:{click.ShortCode}";
                    deltas.TryGetValue(key, out var current);
                    deltas[key] = current + 1;
                }

                using var scope = _scopeFactory.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<ILinkRepository>();
                await repository.ApplyClickDeltasAsync(deltas, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha no flush final de cliques.");
        }
    }

    // Reservado para testes/ajuste fino; o teto real é aplicado no ChannelClickQueue.
    internal const int BatchLimit = MaxBatchSize;
}
