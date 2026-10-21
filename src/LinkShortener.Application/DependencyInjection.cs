using System.Reflection;
using FluentValidation;
using LinkShortener.Application.Common;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace LinkShortener.Application;

/// <summary>Composition root da camada Application (Clean Architecture).</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(Assembly.GetExecutingAssembly());
            cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        });

        // Validators por convenção (FindValidatorsOfType via assembly scanning).
        services.AddValidatorsFromAssembly(Assembly.GetExecutingAssembly());

        // TimeProvider injetável — facilita testes determinísticos.
        services.AddSingleton(TimeProvider.System);

        return services;
    }
}
