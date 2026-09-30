using System;

namespace Acentra.MultiTenant.Core.Entities;

public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty; // e.g. "apex-health"
    public string Name { get; set; } = string.Empty;
    public string SubscriptionTier { get; set; } = "Enterprise";
    public string S3BucketPrefix { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
}
