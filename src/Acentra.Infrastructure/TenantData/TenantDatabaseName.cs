using System.Text.RegularExpressions;

namespace Acentra.Infrastructure.TenantData;

/// <summary>
/// Deterministic derivation and sanitisation of the PostgreSQL database name that backs a tenant.
/// A database name is an <em>identifier</em>, not a value: it cannot be parameterised in
/// <c>CREATE DATABASE</c>, so the only safe policy is to constrain it to a strict character set
/// before it ever reaches SQL. Sanitising is deterministic — the same input always yields the same
/// name — so a retry never targets a different database.
/// </summary>
public static class TenantDatabaseName
{
    /// <summary>Prefix used when a database name has to be derived from a tenant slug.</summary>
    public const string SlugPrefix = "acentra_";

    /// <summary>PostgreSQL's identifier limit (<c>NAMEDATALEN - 1</c>).</summary>
    public const int MaxLength = 63;

    private static readonly Regex IdentifierPattern = new(
        "^[a-z_][a-z0-9_]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Derives a database name from a tenant slug. Uses the same <c>acentra_&lt;slug&gt;</c>
    /// convention the control plane stores in <c>Tenant.DatabaseName</c>, so the ambient
    /// (slug-only) and explicit (descriptor) routing paths land on the same database.
    /// </summary>
    public static string FromSlug(string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        return Normalize(SlugPrefix + slug);
    }

    /// <summary>
    /// Sanitises a candidate database name: trims, lower-cases, maps every character outside
    /// <c>[a-z0-9_]</c> to <c>_</c>, guarantees the identifier does not start with a digit and
    /// truncates to <see cref="MaxLength"/>. Throws when nothing usable is left, or when the result
    /// is somehow still not a valid identifier — a bad name must fail loudly, never be guessed at.
    /// </summary>
    public static string Normalize(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new ArgumentException(
                "A tenant database name must not be empty. The control plane's Tenant.DatabaseName " +
                "is the source of truth for it.",
                nameof(candidate));
        }

        var trimmed = candidate.Trim();
        var length = Math.Min(trimmed.Length, MaxLength);
        var buffer = new char[length];
        var index = 0;

        foreach (var character in trimmed)
        {
            if (index == MaxLength)
            {
                break;
            }

            var lower = char.ToLowerInvariant(character);
            var safe = (lower >= 'a' && lower <= 'z') || (lower >= '0' && lower <= '9') || lower == '_';

            buffer[index++] = safe ? lower : '_';
        }

        // A PostgreSQL identifier must not begin with a digit. The prefix is deterministic so the
        // same tenant always maps to the same database.
        var name = index > 0 && char.IsAsciiDigit(buffer[0])
            ? "t" + new string(buffer, 0, index)
            : new string(buffer, 0, index);

        if (name.Length > MaxLength)
        {
            name = name[..MaxLength];
        }

        if (!IsValid(name))
        {
            throw new ArgumentException(
                $"'{candidate}' does not sanitise to a usable PostgreSQL database name.",
                nameof(candidate));
        }

        return name;
    }

    /// <summary>True when <paramref name="candidate"/> is already a safe, unquoted identifier.</summary>
    public static bool IsValid(string? candidate) =>
        candidate is { Length: > 0 and <= MaxLength } && IdentifierPattern.IsMatch(candidate);
}
