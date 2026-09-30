namespace Acentra.Infrastructure.TenantData;

/// <summary>
/// Thrown when a write would cross the tenant boundary: an entity is being inserted, modified or
/// deleted while carrying a <c>TenantId</c> that is not the current tenant's. This is the write-side
/// half of tenant isolation — query filters only cover reads, so a client-supplied
/// <c>TenantId</c> smuggled through a request body is refused here.
/// </summary>
public sealed class TenantIsolationException : InvalidOperationException
{
    public TenantIsolationException(string message)
        : base(message)
    {
    }
}
