using System.Globalization;
using System.Text.RegularExpressions;

namespace Guardrail.Domain.Common;

/// <summary>Money on the wire is a decimal string with exactly 2 dp (API_CONTRACT.md).</summary>
public static partial class MoneyFormat
{
    public const string DefaultCurrency = "USD";

    public static string Format(decimal amount) =>
        Round(amount).ToString("0.00", CultureInfo.InvariantCulture);

    public static string? Format(decimal? amount) => amount is null ? null : Format(amount.Value);

    /// <summary>USD rounding, banker's rounding at defined points only (CODING_STANDARDS.md).</summary>
    public static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.ToEven);

    /// <summary>Parses a strict "123.45" string (exactly 2 dp, non-negative).</summary>
    public static bool TryParseStrict(string? text, out decimal amount)
    {
        amount = 0;
        return text is not null
            && StrictPattern().IsMatch(text)
            && decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount);
    }

    /// <summary>Parses "12", "12.5" or "12.50" (used for loose inputs such as maxUnitPrice).</summary>
    public static bool TryParseLoose(string? text, out decimal amount)
    {
        amount = 0;
        return text is not null
            && LoosePattern().IsMatch(text)
            && decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount);
    }

    [GeneratedRegex(@"^\d{1,12}\.\d{2}$")]
    private static partial Regex StrictPattern();

    [GeneratedRegex(@"^\d{1,6}(\.\d{1,2})?$")]
    private static partial Regex LoosePattern();
}
