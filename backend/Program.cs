using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Models;
using Acentra.MultiTenant.Core.Interfaces;
using Acentra.MultiTenant.Infrastructure.Data;
using Acentra.MultiTenant.Infrastructure.Middleware;
using Acentra.MultiTenant.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

// Add Services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// Swagger with custom X-Tenant-ID Header support
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Acentra Multi-Tenant Inventory API",
        Version = "v1",
        Description = "Enterprise Multi-Tenant Inventory Platform with EF Core Global Query Filter isolation & S3 partition security."
    });

    // Add X-Tenant-ID header parameter to Swagger UI
    c.AddSecurityDefinition("TenantHeader", new OpenApiSecurityScheme
    {
        Name = "X-Tenant-ID",
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Description = "Enter Tenant ID (GUID) or Tenant Code (e.g., '11111111-1111-1111-1111-111111111111' or 'apex-health')"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "TenantHeader"
                }
            },
            Array.Empty<string>()
        }
    });
});

// Configure CORS for React frontend
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// Register Scoped Tenant Context
builder.Services.AddScoped<ITenantService, TenantService>();
builder.Services.AddScoped<IS3StorageService, S3StorageService>();

// Register EF Core DbContext (using In-Memory / SQLite for seamless execution)
builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
{
    var tenantService = serviceProvider.GetService<ITenantService>();
    options.UseInMemoryDatabase("AcentraMultiTenantDb");
});

var app = builder.Build();

// Seed initial Multi-Tenant Data
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await DbSeeder.SeedAsync(context);
}

// Middleware Pipeline
if (app.Environment.IsDevelopment() || true)
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Acentra Multi-Tenant API v1");
    });
}

app.UseCors("AllowFrontend");

// Tenant Resolution Middleware (Extracts header/subdomain & attaches context)
app.UseMiddleware<TenantResolutionMiddleware>();

app.UseAuthorization();
app.MapControllers();

app.Run();
