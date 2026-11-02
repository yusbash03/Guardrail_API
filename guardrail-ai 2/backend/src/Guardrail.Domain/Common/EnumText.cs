using System.Collections.Concurrent;
using System.Text.Json;

namespace Guardrail.Domain.Common;

/// <summary>
/// Canonical text form of every enum (UPPER_SNAKE_CASE, DOMAIN_MODEL.md §3).
/// The same conversion is used for the database CHECK lists and the JSON wire format.
/// </summary>
public static class EnumText
{
    private static readonly ConcurrentDictionary<Enum, string> ToTextCache = new();
    private static readonly ConcurrentDictionary<(Type, string), object> FromTextCache = new();

    public static string ToText<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        ToTextCache.GetOrAdd(value, v => JsonNamingPolicy.SnakeCaseUpper.ConvertName(v.ToString()));

    public static TEnum Parse<TEnum>(string text)
        where TEnum : struct, Enum =>
        TryParse<TEnum>(text, out var value)
            ? value
            : throw new ArgumentException($"'{text}' is not a valid {typeof(TEnum).Name}.", nameof(text));

    public static bool TryParse<TEnum>(string? text, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var found = FromTextCache.GetOrAdd((typeof(TEnum), text), key =>
        {
            foreach (var candidate in Enum.GetValues<TEnum>())
            {
                if (ToText(candidate) == key.Item2)
                {
                    return candidate;
                }
            }

            return "__none__";
        });

        if (found is TEnum typed)
        {
            value = typed;
            return true;
        }

        return false;
    }

    public static IReadOnlyList<string> AllTexts<TEnum>()
        where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().Select(ToText).ToArray();
}
