using LinkShortener.Application.Abstractions;
using Microsoft.Extensions.Configuration;

namespace LinkShortener.Infrastructure;

/// <summary>
/// Monta a URL base dos links curtos. Suporta múltiplos domínios customizados:
/// - Se o host da requisição estiver em "CustomDomains" (config), usa https://esse-host.
/// - Caso contrário, usa "BaseUrl" configurado (fallback para localhost em dev).
/// </summary>
public sealed class ConfiguredBaseUrlProvider : IBaseUrlProvider
{
    private readonly string _defaultBaseUrl;
    private readonly HashSet<string> _customDomains;

    public ConfiguredBaseUrlProvider(IConfiguration configuration)
    {
        _defaultBaseUrl = configuration["LinkShortener:BaseUrl"] ?? "http://localhost:5000";

        _customDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var section = configuration.GetSection("LinkShortener:CustomDomains");
        foreach (var child in section.GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(child.Value))
                _customDomains.Add(child.Value.Trim().ToLowerInvariant());
        }
    }

    public string GetBaseUrl(string requestHost)
    {
        var host = requestHost?.Trim().ToLowerInvariant() ?? string.Empty;

        // Domínio do próprio request (ex.: "meudominio.com" ou "meudominio.com:443").
        var bareHost = host.Contains(':') ? host[..host.IndexOf(':')] : host;

        if (!string.IsNullOrEmpty(bareHost) && _customDomains.Contains(bareHost))
            return $"https://{bareHost}";

        return _defaultBaseUrl;
    }
}
