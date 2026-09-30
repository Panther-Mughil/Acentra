using Acentra.Domain.Abstractions;
using Acentra.Infrastructure.TenantData;

namespace Acentra.UnitTests;

/// <summary>
/// REQ-003 §8: a tenant slug that does not yield a valid PostgreSQL identifier must sanitise
/// deterministically, and an empty database name must fail loudly rather than be guessed at.
/// </summary>
public sealed class TenantDatabaseNameTests
{
    [Fact]
    public void FromSlug_MatchesTheControlPlaneNamingConvention()
    {
        // The control plane stores "acentra_<slug>"; deriving the ambient connection from the slug
        // must land on exactly the same database.
        Assert.Equal("acentra_acme", TenantDatabaseName.FromSlug("acme"));
        Assert.Equal("acentra_globex", TenantDatabaseName.FromSlug("globex"));
    }

    [Theory]
    [InlineData("ACME", "acme")]
    [InlineData("acme-eu", "acme_eu")]
    [InlineData("acme corp", "acme_corp")]
    [InlineData("acme.eu", "acme_eu")]
    [InlineData("  acme  ", "acme")]
    [InlineData("acentra_acme", "acentra_acme")]
    public void Normalize_IsDeterministicAndSanitises(string input, string expected)
    {
        var first = TenantDatabaseName.Normalize(input);

        Assert.Equal(expected, first);
        Assert.Equal(first, TenantDatabaseName.Normalize(input));
        Assert.True(TenantDatabaseName.IsValid(first));
    }

    [Fact]
    public void Normalize_PrefixesADigitLedName()
    {
        var name = TenantDatabaseName.Normalize("9lives");

        Assert.Equal("t9lives", name);
        Assert.True(TenantDatabaseName.IsValid(name));
    }

    [Fact]
    public void Normalize_TruncatesToPostgreSqlsIdentifierLimit()
    {
        var name = TenantDatabaseName.Normalize(new string('a', 200));

        Assert.Equal(TenantDatabaseName.MaxLength, name.Length);
        Assert.True(TenantDatabaseName.IsValid(name));
    }

    [Fact]
    public void Normalize_KeepsTheNameWithinTheIdentifierLimitWhenItPrefixesADigit()
    {
        var name = TenantDatabaseName.Normalize(new string('9', 200));

        Assert.Equal(TenantDatabaseName.MaxLength, name.Length);
        Assert.StartsWith("t", name, StringComparison.Ordinal);
        Assert.All(name.Skip(1), c => Assert.Equal('9', c));
        Assert.True(TenantDatabaseName.IsValid(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Normalize_Throws_ForAnEmptyName(string? input) =>
        Assert.Throws<ArgumentException>(() => TenantDatabaseName.Normalize(input));

    [Fact]
    public void FromSlug_Throws_ForAnEmptySlug() =>
        Assert.Throws<ArgumentException>(() => TenantDatabaseName.FromSlug("  "));

    [Theory]
    [InlineData("Acme")]
    [InlineData("9acme")]
    [InlineData("acme-eu")]
    [InlineData("acme;DROP DATABASE postgres")]
    [InlineData("acentra_acme ")]
    public void IsValid_RejectsAnythingThatIsNotASafeIdentifier(string candidate) =>
        Assert.False(TenantDatabaseName.IsValid(candidate));

    [Fact]
    public void ConnectionStrings_SubstituteOnlyTheDatabaseName()
    {
        var connections = new TenantConnectionStrings(
            "Host=localhost;Port=5432;Database={0};Username=acentra;Password=secret");

        // ForDatabase takes a database name (sanitised, not prefixed); For(descriptor) takes the
        // control plane's name verbatim.
        Assert.Equal(
            "Host=localhost;Port=5432;Database=acme;Username=acentra;Password=secret",
            connections.ForDatabase("ACME"));

        Assert.Equal(
            "Host=localhost;Port=5432;Database=acentra_acme;Username=acentra;Password=secret",
            connections.For(new TenantDescriptor(Guid.NewGuid(), "acme", "Acme", "acentra_acme")));

        Assert.Contains("Database=postgres;", connections.ForMaintenance(), StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionStrings_RedactTheTemplateInToString()
    {
        var connections = new TenantConnectionStrings("Host=localhost;Database={0};Password=secret");

        Assert.DoesNotContain("secret", connections.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionStrings_Throw_WhenTheTemplateIsMissingOrHasNoPlaceholder()
    {
        Assert.Throws<ArgumentException>(() => new TenantConnectionStrings(string.Empty));
        Assert.Throws<ArgumentException>(() => new TenantConnectionStrings("Host=localhost;Database=acentra"));
    }
}
