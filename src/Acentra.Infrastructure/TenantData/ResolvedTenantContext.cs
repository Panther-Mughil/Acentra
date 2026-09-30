using Acentra.Domain.Abstractions;

namespace Acentra.Infrastructure.TenantData;

/// <summary>
/// Immutable <see cref="ITenantContext"/> for a context that is bound to an explicitly supplied
/// tenant — provisioning, startup migration and design time — rather than to the ambient
/// circuit-scoped context. Keeps the query filter and the write interceptor bound to the same
/// tenant as the connection the context was built with.
/// </summary>
public sealed record ResolvedTenantContext(Guid TenantId, string Slug) : ITenantContext
{
    public bool IsResolved => true;
}
