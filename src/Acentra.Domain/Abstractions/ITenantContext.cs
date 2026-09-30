namespace Acentra.Domain.Abstractions;

public interface ITenantContext
{
    Guid TenantId { get; }
    string Slug { get; }
    bool IsResolved { get; }
}
