using Acentra.Domain.Abstractions;
using Acentra.Infrastructure.ControlPlane;
using Acentra.Infrastructure.Inventory;
using Acentra.Infrastructure.Storage;
using Acentra.Infrastructure.TenantData;
using Acentra.Web.Auth;
using Acentra.Web.Components;
using Acentra.Web.Middleware;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();
builder.Services.AddControlPlane(builder.Configuration);
builder.Services.AddScoped<TenantState>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantState>());
builder.Services.AddTenantResolution();
builder.Services.AddTenantData(builder.Configuration);
builder.Services.AddFileStorage(builder.Configuration);
builder.Services.AddInventory();

// Behind a TLS-terminating proxy, Request.IsHttps is false unless the forwarded headers are
// honoured, which would otherwise emit the continuity cookie WITHOUT `Secure`. Off by default:
// trusting forwarded headers from an unknown proxy is a spoofing risk, so it is opt-in per host.
var forwardedHeadersEnabled = builder.Configuration.GetValue<bool>("ForwardedHeaders:Enabled");

if (forwardedHeadersEnabled)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        // Exactly one proxy hop in front of the app; the default known proxies/networks are kept,
        // so an unknown (spoofed) X-Forwarded-* header is not trusted.
        options.ForwardLimit = 1;
    });
}

var app = builder.Build();

// First middleware, so Request.IsHttps (and therefore the cookie's `Secure` flag) is correct
// before anything else observes the scheme.
if (forwardedHeadersEnabled)
{
    app.UseForwardedHeaders();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapInventoryFileDownloads();

app.Run();
