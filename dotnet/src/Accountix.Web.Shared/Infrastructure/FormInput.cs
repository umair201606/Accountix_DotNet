using System.Globalization;
using Accountix.Core.Shared;

namespace Accountix.Web.Infrastructure;

/// <summary>
/// Numeric and date form fields that never 500 (shared/forms.py): blank reads
/// as the default (or "X is required."), unreadable input refuses naming the field.
/// </summary>
public static class FormInput
{
    private static string Label(string name, string? label) =>
        label ?? char.ToUpperInvariant(name.Replace('_', ' ')[0]) + name.Replace('_', ' ')[1..];

    private static string? Raw(IFormCollection form, string name) => form[name].FirstOrDefault()?.Trim();

    public static double Float(IFormCollection form, string name, double? @default = 0, string? label = null)
    {
        string? raw = Raw(form, name);
        if (string.IsNullOrEmpty(raw))
            return @default ?? throw new FormInputException($"{Label(name, label)} is required.");
        if (double.TryParse(raw.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            return v;
        throw new FormInputException($"{Label(name, label)} must be a number.");
    }

    public static int Int(IFormCollection form, string name, int? @default = 0, string? label = null)
    {
        string? raw = Raw(form, name);
        if (string.IsNullOrEmpty(raw))
            return @default ?? throw new FormInputException($"{Label(name, label)} is required.");
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            return v;
        throw new FormInputException($"{Label(name, label)} must be a whole number.");
    }

    /// <summary>form_date: required unless a default is given.</summary>
    public static DateOnly Date(IFormCollection form, string name, DateOnly? @default = null, string? label = null)
    {
        string? raw = Raw(form, name);
        if (string.IsNullOrEmpty(raw))
            return @default ?? throw new FormInputException($"{Label(name, label)} is required.");
        if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        throw new FormInputException($"{Label(name, label)} is not a valid date.");
    }

    /// <summary>form_date(default=False): null when blank.</summary>
    public static DateOnly? OptionalDate(IFormCollection form, string name, string? label = null)
    {
        string? raw = Raw(form, name);
        if (string.IsNullOrEmpty(raw)) return null;
        if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        throw new FormInputException($"{Label(name, label)} is not a valid date.");
    }
}
