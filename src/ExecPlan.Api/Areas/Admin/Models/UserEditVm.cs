using ExecPlan.Domain.Enums;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace ExecPlan.Api.Areas.Admin.Models;

public sealed class UserEditVm
{
    public Guid? Id { get; set; }
    public string? UserName { get; set; }
    public string? FullName { get; set; }
    public string? Phone { get; set; }
    public UserRole Role { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? DepartmentId { get; set; }

    /// <summary>Create: required. Edit: optional — blank leaves the existing hash untouched.</summary>
    public string? Password { get; set; }

    public bool IsActive { get; set; } = true;

    public SelectList? Orgs { get; set; }
    public SelectList? Depts { get; set; }
}
