using Acentra.Domain.Abstractions;

namespace Acentra.Domain.Entities;

public class TenantMembership
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public MembershipRole Role { get; set; }
}
