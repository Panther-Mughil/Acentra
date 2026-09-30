using Acentra.Domain.Abstractions;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http;

namespace Acentra.Web.Auth;

/// <summary>
/// Bridges the HTTP request scope into the Blazor circuit scope (§4.1 of the architecture doc).
///
/// With interactive Server the middleware runs once per circuit (at the <c>/_blazor</c>
/// handshake), while <see cref="TenantState"/> is resolved in the circuit's own DI scope.
/// This handler copies the tenant the middleware resolved on that request — published in
/// <c>HttpContext.Items</c> — into this circuit's <see cref="TenantState"/>.
///
/// Fail closed: if there is nothing to seed (no HttpContext, or no resolved tenant, e.g. the
/// circuit was opened from a tenant-agnostic page), the circuit stays unresolved and tenant
/// pages must render a "select a tenant" state. A default tenant is never chosen.
/// </summary>
public sealed class TenantCircuitHandler(
    TenantState state,
    IHttpContextAccessor httpContextAccessor) : CircuitHandler
{
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var items = httpContextAccessor.HttpContext?.Items;

        if (items is not null &&
            items.TryGetValue(TenantResolutionConstants.HttpContextItemKey, out var value) &&
            value is TenantDescriptor descriptor &&
            descriptor.Id != Guid.Empty)
        {
            state.Set(descriptor.Id, descriptor.Slug);
        }

        return Task.CompletedTask;
    }
}
