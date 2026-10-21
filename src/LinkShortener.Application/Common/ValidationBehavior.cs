using FluentValidation;
using LinkShortener.Application.Abstractions;
using MediatR;

namespace LinkShortener.Application.Common;

/// <summary>Resultado do pipeline de validação (para não depender de exceções no handler).</summary>
public readonly record struct ValidationResultModel(bool IsValid, IReadOnlyList<string> Errors)
{
    public static ValidationResultModel Success => new(true, Array.Empty<string>());
}

/// <summary>
/// Behavior do MediatR que executa todos os IValidator&lt;TRequest&gt; registrados
/// antes de entregar o request ao handler (validação declarativa com FluentValidation).
/// </summary>
public sealed class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators)
    {
        _validators = validators;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (_validators.Any())
        {
            var context = new ValidationContext<TRequest>(request);

            var validationResults = await Task.WhenAll(
                _validators.Select(v => v.ValidateAsync(context, cancellationToken)));

            var failures = validationResults
                .SelectMany(r => r.Errors)
                .Where(f => f is not null)
                .Select(f => f.ErrorMessage)
                .Distinct()
                .ToList();

            if (failures.Count > 0)
                throw new FluentValidation.ValidationException(
                    failures.Select(f => new FluentValidation.Results.ValidationFailure("Request", f)));
        }

        return await next();
    }
}
