namespace Acentra.Domain.Abstractions;

public sealed record TenantDescriptor(Guid Id, string Slug, string Name, string DatabaseName);
