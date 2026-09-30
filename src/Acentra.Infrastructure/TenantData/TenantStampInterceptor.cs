using Acentra.Domain.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Acentra.Infrastructure.TenantData;

/// <summary>
/// Write-side tenant enforcement, applied on every <c>SaveChanges</c>/<c>SaveChangesAsync</c>:
/// <list type="bullet">
///   <item>an insert with an empty <c>TenantId</c> is stamped from <see cref="ITenantContext"/>;</item>
///   <item>an insert carrying a different tenant's <c>TenantId</c> throws;</item>
///   <item>a modify or delete whose <c>TenantId</c> is not the current tenant's throws.</item>
/// </list>
/// Tenant-owned entities are discovered from the EF model (any entity with a CLR <c>Guid TenantId</c>
/// property), so a new tenant-owned entity gets this protection without touching the interceptor.
/// </summary>
public sealed class TenantStampInterceptor(ITenantContext tenant) : SaveChangesInterceptor
{
    /// <summary>The property every tenant-owned entity carries.</summary>
    public const string TenantIdPropertyName = "TenantId";

    private readonly ITenantContext _tenant = tenant;

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Apply(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var property = entry.Metadata.FindProperty(TenantIdPropertyName);

            // Not tenant-owned (or shadowed, which this model never does) — nothing to enforce.
            if (property is null || property.PropertyInfo is null || property.ClrType != typeof(Guid))
            {
                continue;
            }

            // Fail closed: without a resolved tenant there is no correct value to write. A context
            // can only be built through the factory, which refuses this case, but the interceptor
            // does not rely on that.
            if (!_tenant.IsResolved)
            {
                throw new TenantIsolationException(
                    $"Refusing to {Describe(entry.State)} '{entry.Metadata.DisplayName()}': no tenant is " +
                    "resolved, so the row's TenantId cannot be verified.");
            }

            var current = entry.Property(TenantIdPropertyName).CurrentValue is Guid value ? value : Guid.Empty;

            switch (entry.State)
            {
                case EntityState.Added when current == Guid.Empty:
                    // Stamp from the current tenant — the client never supplies it.
                    entry.Property(TenantIdPropertyName).CurrentValue = _tenant.TenantId;
                    break;

                case EntityState.Added when current != _tenant.TenantId:
                    throw new TenantIsolationException(
                        $"Refusing to insert '{entry.Metadata.DisplayName()}' carrying TenantId {current}: " +
                        $"the current tenant is {_tenant.TenantId}.");

                case EntityState.Modified when current != _tenant.TenantId:
                    throw new TenantIsolationException(
                        $"Refusing to modify '{entry.Metadata.DisplayName()}' carrying TenantId {current}: " +
                        $"the current tenant is {_tenant.TenantId}.");

                case EntityState.Modified when entry.Property(TenantIdPropertyName).OriginalValue is Guid original
                                               && original != _tenant.TenantId:
                    // Re-stamping a row that belongs to another tenant to look like ours.
                    throw new TenantIsolationException(
                        $"Refusing to modify '{entry.Metadata.DisplayName()}' because the stored row carries " +
                        $"TenantId {entry.Property(TenantIdPropertyName).OriginalValue}: the current tenant is " +
                        $"{_tenant.TenantId}.");

                case EntityState.Deleted when current != _tenant.TenantId:
                    throw new TenantIsolationException(
                        $"Refusing to delete '{entry.Metadata.DisplayName()}' carrying TenantId {current}: " +
                        $"the current tenant is {_tenant.TenantId}.");
            }
        }
    }

    private static string Describe(EntityState state) => state switch
    {
        EntityState.Added => "insert",
        EntityState.Modified => "modify",
        EntityState.Deleted => "delete",
        _ => state.ToString()
    };
}
