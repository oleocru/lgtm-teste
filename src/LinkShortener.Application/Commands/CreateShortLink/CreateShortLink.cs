using LinkShortener.Application.Abstractions;
using MediatR;

namespace LinkShortener.Application.Commands.CreateShortLink;

/// <summary>
/// CQRS Command: criar um link curto.
/// Fluxo de escrita (Write): valida, gera short code Base62, salva no banco e faz warm-up do Redis.
/// </summary>
public sealed record CreateShortLinkCommand(
    string OriginalUrl,
    string? Domain = null,
    int? ExpiresInDays = null) : IRequest<CreateShortLinkResult>;

public sealed record CreateShortLinkResult(
    string ShortCode,
    string ShortUrl,
    string OriginalUrl,
    DateTime CreatedAtUtc,
    DateTime? ExpiresAtUtc);

public sealed class CreateShortLinkHandler
{
    private readonly IShortCodeGenerator _generator;
    private readonly ILinkRepository _repository;
    private readonly IRedirectCache _cache;
    private readonly IBaseUrlProvider _baseUrlProvider;
    private readonly TimeProvider _timeProvider;

    public CreateShortLinkHandler(
        IShortCodeGenerator generator,
        ILinkRepository repository,
        IRedirectCache cache,
        IBaseUrlProvider baseUrlProvider,
        TimeProvider timeProvider)
    {
        _generator = generator;
        _repository = repository;
        _cache = cache;
        _baseUrlProvider = baseUrlProvider;
        _timeProvider = timeProvider;
    }

    public async Task<CreateShortLinkResult> Handle(
        CreateShortLinkCommand request,
        CancellationToken cancellationToken)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var domain = string.IsNullOrWhiteSpace(request.Domain)
            ? NormalizeDomain(_baseUrlProvider.GetBaseUrl(string.Empty))
            : request.Domain.Trim().ToLowerInvariant();

        // 1. Gera um short code único (Base62 via contador sequencial — sem loops de busca no banco).
        var shortCode = await _generator.GenerateAsync(domain, cancellationToken);

        // 2. Persiste os metadados no banco relacional.
        var expiresAtUtc = request.ExpiresInDays.HasValue
            ? nowUtc.AddDays(request.ExpiresInDays.Value)
            : (DateTime?)null;

        var link = Domain.Entities.ShortenedLink.Create(
            shortCode, domain, request.OriginalUrl, nowUtc, expiresAtUtc);

        await _repository.AddAsync(link, cancellationToken);

        // 3. Warm-up do cache: a relação Domain:ShortCode -> OriginalUrl já entra no Redis.
        TimeSpan? ttl = expiresAtUtc.HasValue
            ? expiresAtUtc.Value - nowUtc
            : null;

        await _cache.WarmUpAsync(domain, shortCode, link.OriginalUrl, ttl, cancellationToken);

        // 4. Retorna o link encurtado montado com o domínio da requisição.
        var baseUrl = _baseUrlProvider.GetBaseUrl(domain);
        var shortUrl = $"{baseUrl.TrimEnd('/')}/{shortCode}";

        return new CreateShortLinkResult(shortCode, shortUrl, link.OriginalUrl, nowUtc, expiresAtUtc);
    }

    private static string NormalizeDomain(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host.ToLowerInvariant()
            : "localhost";
}
