using Acentra.Infrastructure.ControlPlane;
using Acentra.Infrastructure.Inventory;
using Acentra.Infrastructure.Storage;
using Acentra.Infrastructure.TenantData;
using Acentra.Web.Components;
using Acentra.Web.Middleware;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();
builder.Services.AddControlPlane(builder.Configuration);
builder.Services.AddTenantData(builder.Configuration);
builder.Services.AddFileStorage(builder.Configuration);
builder.Services.AddInventory();

var app = builder.Build();

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

app.Run();
