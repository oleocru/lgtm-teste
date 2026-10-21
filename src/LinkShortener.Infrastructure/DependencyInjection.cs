using LinkShortener.Application.Abstractions;
using LinkShortener.Infrastructure;
using LinkShortener.Infrastructure.Caching;
using LinkShortener.Infrastructure.ClickCounting;
using LinkShortener.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace LinkShortener.Infrastructure;

/// <summary>Composition root da camada Infrastructure.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ---------- Banco relacional (SQL Server em produção; InMemory apenas para dev/teste) ----------
        var provider = configuration["Database:Provider"] ?? "SqlServer";

        services.AddDbContext<LinkShortenerDbContext>(options =>
        {
            if (provider.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
                options.UseInMemoryDatabase("linkshortener-dev");
            else
                options.UseSqlServer(
                    configuration.GetConnectionString("SqlServer")
                        ?? throw new InvalidOperationException("ConnectionStrings:SqlServer ausente."),
                    sql => sql.EnableRetryOnFailure(maxRetryCount: 3));
        });

        services.AddScoped<ILinkRepository, EfLinkRepository>();

        // ---------- Redis (StackExchange.Redis) ----------
        var redisConnection = configuration["Redis:ConnectionString"];
        IConnectionMultiplexer? multiplexer = null;

        if (!string.IsNullOrWhiteSpace(redisConnection))
        {
            multiplexer = ConnectionMultiplexer.Connect(new ConfigurationOptions
            {
                EndPoints = { SplitEndpoints(redisConnection) },
                AbortOnConnectFail = false,   // resiliência: inicia mesmo com Redis fora
                ConnectTimeout = 3000,
                SyncTimeout = 1000
            });

            services.AddSingleton(multiplexer);
            services.AddSingleton<IRedirectCache, RedisRedirectCache>();
        }
        else
        {
            // Sem Redis configurado: cache em memória (dev/testes).
            services.AddSingleton<IRedirectCache, MemoryRedirectCache>();
        }

        // ---------- Contador de cliques assíncrono (Channel<T> + batch no banco) ----------
        services.AddSingleton<ChannelClickQueue>();
        services.AddSingleton<IClickEventPublisher>(sp => sp.GetRequiredService<ChannelClickQueue>());
        services.AddSingleton<IClickEventConsumer>(sp => sp.GetRequiredService<ChannelClickQueue>());
        services.AddHostedService<ClickCounterBackgroundService>();

        // ---------- Geração de short codes Base62 ----------
        // Singleton que resolve ILinkRepository (Scoped) sob demanda via IServiceScopeFactory,
        // apenas para a checagem rara de colisão. Em produção o contador Redis INCR garante unicidade.
        services.AddSingleton<IShortCodeGenerator>(sp =>
        {
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SequentialBase62Generator>>();
            var redis = sp.GetService<IConnectionMultiplexer>();

            return new SequentialBase62Generator(
                async (domain, code, ct) =>
                {
                    using var scope = scopeFactory.CreateScope();
                    var repository = scope.ServiceProvider.GetRequiredService<ILinkRepository>();
                    return await repository.ShortCodeExistsAsync(domain, code, ct);
                },
                logger,
                redis);
        });

        // ---------- Domínio / URL base ----------
        services.AddSingleton<IBaseUrlProvider, ConfiguredBaseUrlProvider>();

        return services;
    }

    private static EndPointCollection SplitEndpoints(string connectionString)
    {
        var endpoints = new EndPointCollection();
        foreach (var part in connectionString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var hostPort = part.Split(':', 2);
            var host = hostPort[0];
            var port = hostPort.Length == 2 && int.TryParse(hostPort[1], out var p) ? p : 6379;
            endpoints.Add(new DnsEndPoint(host, port));
        }

        return endpoints;
    }
}
