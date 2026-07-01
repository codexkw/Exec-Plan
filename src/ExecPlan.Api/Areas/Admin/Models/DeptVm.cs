using Microsoft.AspNetCore.Mvc.Rendering;

namespace ExecPlan.Api.Areas.Admin.Models;

public sealed class DeptVm
{
    public Guid? Id { get; set; }
    public string? Name { get; set; }
    public Guid OrganizationId { get; set; }
    public SelectList? Orgs { get; set; }
}

public sealed class DeptListVm
{
    public sealed record Row(Guid Id, string Name, string? Organization);

    public IReadOnlyList<Row> Departments { get; set; } = Array.Empty<Row>();

    /// <summary>True only for SystemAdmin — Manager gets a read-only list (no Add link).</summary>
    public bool CanWrite { get; set; }
}
