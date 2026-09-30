using Acentra.Domain.Abstractions;

namespace Acentra.Web.Auth;

/// <summary>
/// Circuit-scoped, mutable tenant context. This is the single seam the UI tenant switcher
/// mutates and the source of truth for <see cref="ITenantContext"/> after a circuit starts.
/// It never reads <see cref="Microsoft.AspNetCore.Http.HttpContext"/> - the resolved tenant
/// is seeded once at circuit start (see <see cref="TenantCircuitHandler"/>).
/// Registered as Scoped: one instance per HTTP request scope *and* one per Blazor circuit.
/// </summary>
public sealed class TenantState : ITenantContext
{
    public Guid TenantId { get; private set; }

    public string Slug { get; private set; } = string.Empty;

    public bool IsResolved => TenantId != Guid.Empty;

    /// <summary>Raised whenever the tenant changes (set or cleared). Subscribers drop cached data.</summary>
    public event Action? Changed;

    public void Set(Guid id, string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        if (id == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(id));
        }

        TenantId = id;
        Slug = slug;
        Changed?.Invoke();
    }

    public void Clear()
    {
        TenantId = Guid.Empty;
        Slug = string.Empty;
        Changed?.Invoke();
    }
}
