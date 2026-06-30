using ExecPlan.Domain.Common;
using ExecPlan.Domain.Enums;

namespace ExecPlan.Domain.Entities;

public class User : BaseEntity
{
    public string UserName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Phone { get; set; } = "";
    public UserRole Role { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? DepartmentId { get; set; }
    public bool IsActive { get; set; } = true;
}
