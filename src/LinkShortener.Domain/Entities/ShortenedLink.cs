namespace LinkShortener.Domain.Entities;

/// <summary>
/// Entidade raiz que representa um link encurtado.
/// Guarda os metadados persistidos no banco relacional:
/// URL original, short code (Base62), domínio, datas e contador de cliques.
/// </summary>
public class ShortenedLink
{
    public long Id { get; private set; }

    /// <summary>Identificador curto único (Base62, 6 a 8 caracteres).</summary>
    public string ShortCode { get; private set; } = default!;

    /// <summary>Domínio associado ao short code (suporte a domínios customizados). Chave Redis: "dominio:shortcode".</summary>
    public string Domain { get; private set; } = default!;

    public string OriginalUrl { get; private set; } = default!;

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime? ExpiresAtUtc { get; private set; }

    /// <summary>Contador de cliques incrementado de forma assíncrona (batch) pelo ClickCounterService.</summary>
    public long ClickCount { get; private set; }

    public bool IsActive { get; private set; } = true;

    // Construtor privado exigido pelo EF Core para materialização.
    private ShortenedLink() { }

    public static ShortenedLink Create(
        string shortCode,
        string domain,
        string originalUrl,
        DateTime createdAtUtc,
        DateTime? expiresAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(shortCode))
            throw new ArgumentException("ShortCode não pode ser vazio.", nameof(shortCode));
        if (string.IsNullOrWhiteSpace(originalUrl))
            throw new ArgumentException("OriginalUrl não pode ser vazia.", nameof(originalUrl));

        return new ShortenedLink
        {
            ShortCode = shortCode,
            Domain = string.IsNullOrWhiteSpace(domain) ? "localhost" : domain.Trim().ToLowerInvariant(),
            OriginalUrl = originalUrl.Trim(),
            CreatedAtUtc = createdAtUtc,
            ExpiresAtUtc = expiresAtUtc,
            ClickCount = 0,
            IsActive = true
        };
    }

    public bool IsExpired(DateTime nowUtc) =>
        ExpiresAtUtc.HasValue && ExpiresAtUtc.Value <= nowUtc;

    /// <summary>Aplica um delta de cliques vindo do processamento em lote.</summary>
    public void ApplyClickDelta(long delta)
    {
        if (delta < 0) throw new ArgumentOutOfRangeException(nameof(delta));
        ClickCount += delta;
    }

    public void Deactivate() => IsActive = false;

    /// <summary>Chave composta usada tanto no banco quanto no Redis ("dominio:shortcode").</summary>
    public string BuildCacheKey() => $"{Domain}:{ShortCode}";
}
