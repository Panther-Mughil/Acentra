using Acentra.Domain.Abstractions;
using Acentra.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Acentra.Infrastructure.TenantData;

public sealed class AppDbContext : DbContext
{
    private readonly ITenantContext _tenant;

    public AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenant)
        : base(options)
    {
        _tenant = tenant;
    }

    public DbSet<Product> Products => Set<Product>();
    public DbSet<StockLevel> StockLevels => Set<StockLevel>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<TenantFile> Files => Set<TenantFile>();
}
