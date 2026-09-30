using Acentra.Web.Components;
using Acentra.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Register ORGSHIELD 360 Enterprise Services
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<TenantStateService>();
builder.Services.AddScoped<InventoryService>();
builder.Services.AddScoped<SupplierService>();
builder.Services.AddScoped<PurchaseOrderService>();
builder.Services.AddScoped<DocumentVaultService>();
builder.Services.AddScoped<AuditLogService>();
builder.Services.AddScoped<SecurityCenterService>();
builder.Services.AddScoped<AlertService>();
builder.Services.AddScoped<AIInsightService>();
builder.Services.AddScoped<CommandPaletteService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
