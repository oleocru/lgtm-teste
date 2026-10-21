using System.Security.Cryptography;
using LinkShortener.Application.Abstractions;
using LinkShortener.Application.Common;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace LinkShortener.Infrastructure;

/// <summary>
/// Gera short codes Base62 a partir de um contador sequencial distribuído (Redis INCR),
/// o que garante unicidade sem loops de busca no banco. Em cada incremento:
///   code = Base62(counter XOR hashMix) com padding mínimo de 6 caracteres.
/// Se não houver Redis (dev/test), usa Interlocked.Increment local + fallback de colisão
/// com verificação única no repositório (rara, custo aceitável fora de produção).
/// </summary>
public sealed class SequentialBase62Generator : IShortCodeGenerator
{
    private const int MinLength = 6;
    private const string CounterKey = "ls:counter";

    private readonly IConnectionMultiplexer? _redis;
    private readonly ILinkRepository _repository;
    private readonly ILogger<SequentialBase62Generator> _logger;
    private long _localCounter;

    public SequentialBase62Generator(
        ILinkRepository repository,
        ILogger<SequentialBase62Generator> logger,
        IConnectionMultiplexer? redis = null)
    {
        _repository = repository;
        _logger = logger;
        _redis = redis;
    }

    /// <summary>Construtor para uso como singleton: a checagem de colisão é resolvida sob demanda via factory.</summary>
    public SequentialBase62Generator(
        Func<string, string, CancellationToken, Task<bool>> shortCodeExists,
        ILogger<SequentialBase62Generator> logger,
        IConnectionMultiplexer? redis = null)
    {
        _repository = null!;
        _shortCodeExistsOverride = shortCodeExists;
        _logger = logger;
        _redis = redis;
    }

    private readonly Func<string, string, CancellationToken, Task<bool>>? _shortCodeExistsOverride;

    public async Task<string> GenerateAsync(string domain, CancellationToken ct = default)
    {
        var sequence = await NextSequenceAsync(ct);

        // Mistura bits do contador com um hash determinístico para "embaralhar" códigos
        // sequenciais (evita enumerabilidade e colisões previsíveis) mantendo injetividade
        // dentro do espaço de 2^64 via multiplicação por ímpar grande (bijection em mod 2^64).
        var mixed = Mix(sequence);
        var code = Pad(Base62.Encode(mixed));

        // Colisão é improvável mas tratada: tenta até N vezes com novo incremento.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (!await ExistsAsync(domain, code, ct))
                return code;

            _logger.LogWarning("Colisão de shortcode {Code} em {Domain}; regenerando.", code, domain);
            sequence = await NextSequenceAsync(ct);
            code = Pad(Base62.Encode(Mix(sequence)));
        }

        throw new InvalidOperationException($"Não foi possível gerar um short code único para o domínio '{domain}'.");
    }

    private async Task<bool> ExistsAsync(string domain, string code, CancellationToken ct)
    {
        if (_shortCodeExistsOverride is not null)
            return await _shortCodeExistsOverride(domain, code, ct);

        return await _repository.ShortCodeExistsAsync(domain, code, ct);
    }

    private async Task<long> NextSequenceAsync(CancellationToken ct)
    {
        if (_redis is not null)
        {
            try
            {
                var db = _redis.GetDatabase();
                return (long)await db.StringIncrementAsync(CounterKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Redis indisponível para contador; usando sequência local.");
            }
        }

        return Interlocked.Increment(ref _localCounter);
    }

    /// <summary>Bijection modular: multiply by odd constant + xor-shift (revogável, barata).</summary>
    private static ulong Mix(long sequence)
    {
        unchecked
        {
            var x = (ulong)sequence + 0x9E3779B97F4A7C15UL; // golden ratio 64-bit offset
            x *= 0xC2B2AE3D27D4EB4FUL;                      // ímpar -> bijeção em mod 2^64
            x ^= x >> 29;
            x *= 0xFF51AFD7ED558CCDUL;                      // ímpar
            x ^= x >> 32;
            return x;
        }
    }

    private static string Pad(string code) =>
        code.Length >= MinLength ? code : code.PadLeft(MinLength, 'a');
}
