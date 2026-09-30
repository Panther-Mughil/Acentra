using System.Text.RegularExpressions;
using Acentra.Infrastructure.ControlPlane.Configurations;

namespace Acentra.Infrastructure.ControlPlane;

/// <summary>
/// Normalisation/validation of client-supplied tenant slugs. A slug is a *hint*:
/// normalising here makes lookups case-insensitive without ever trusting the input.
/// </summary>
public static class TenantSlug
{
    private static readonly Regex SlugRegex = new(
        TenantConfiguration.SlugPattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Trim + lower-case; returns null when the result cannot be a valid slug.</summary>
    public static string? TryNormalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var normalized = raw.Trim().ToLowerInvariant();
        return SlugRegex.IsMatch(normalized) ? normalized : null;
    }
}
