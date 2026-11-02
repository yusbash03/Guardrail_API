using FluentValidation;
using Guardrail.Domain.Common;

namespace Guardrail.Application.Common;

public static class Validation
{
    /// <summary>Runs a FluentValidation validator and converts failures to VALIDATION_FAILED (400).</summary>
    public static async Task<DomainError?> CheckAsync<T>(this IValidator<T> validator, T instance, CancellationToken cancellationToken)
    {
        var result = await validator.ValidateAsync(instance, cancellationToken);
        if (result.IsValid)
        {
            return null;
        }

        var errors = result.Errors
            .GroupBy(e => ToCamel(e.PropertyName))
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());
        return DomainError.Validation("Request failed validation.", errors);
    }

    public static IRuleBuilderOptions<T, string?> MoneyString<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(v => v is null || MoneyFormat.TryParseStrict(v, out _)).WithMessage("Must be a decimal string with 2 decimal places, e.g. \"30.00\".");

    public static IRuleBuilderOptions<T, string?> OneOf<T, TEnum>(this IRuleBuilder<T, string?> rule)
        where TEnum : struct, Enum =>
        rule.Must(v => v is not null && EnumText.TryParse<TEnum>(v, out _))
            .WithMessage($"Must be one of: {string.Join(", ", EnumText.AllTexts<TEnum>())}.");

    private static string ToCamel(string propertyPath) =>
        string.Join('.', propertyPath.Split('.').Select(p => p.Length == 0 ? p : char.ToLowerInvariant(p[0]) + p[1..]));
}
