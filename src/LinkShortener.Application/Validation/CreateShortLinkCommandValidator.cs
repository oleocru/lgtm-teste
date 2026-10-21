using FluentValidation;
using LinkShortener.Application.Commands.CreateShortLink;

namespace LinkShortener.Application.Validation;

/// <summary>
/// Validações de negócio do comando de criação:
/// URL absoluta http/https, tamanho máximo e domínio opcional válido.
/// </summary>
public class CreateShortLinkCommandValidator : AbstractValidator<CreateShortLinkCommand>
{
    public const int MaxUrlLength = 2048;

    public CreateShortLinkCommandValidator()
    {
        RuleFor(x => x.OriginalUrl)
            .NotEmpty().WithMessage("A URL original é obrigatória.")
            .MaximumLength(MaxUrlLength).WithMessage($"A URL original deve ter no máximo {MaxUrlLength} caracteres.")
            .Must(BeAValidHttpUrl).WithMessage("A URL original deve ser uma URL HTTP/HTTPS válida.");

        RuleFor(x => x.Domain)
            .MaximumLength(253).When(x => x.Domain is not null)
            .Must(BeAValidDomainOrNull).WithMessage("Domínio inválido.")
            .When(x => x.Domain is not null);

        RuleFor(x => x.ExpiresInDays)
            .GreaterThan(0).WithMessage("ExpiresInDays deve ser maior que zero.")
            .When(x => x.ExpiresInDays.HasValue);
    }

    private static bool BeAValidHttpUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        // Bloqueia URLs sem host (ex.: "http:///foo") e endereços locais por segurança básica.
        if (string.IsNullOrEmpty(uri.Host)) return false;
        return true;
    }

    private static bool BeAValidDomainOrNull(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return true;
        return System.Text.RegularExpressions.Regex.IsMatch(
            domain.Trim(),
            @"^(?!-)[a-zA-Z0-9-]{1,63}(?<!-)(\.[a-zA-Z0-9-]{1,63})+$");
    }
}
