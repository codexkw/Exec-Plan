namespace ExecPlan.Api.Areas.Admin.Models;

public sealed class OrgVm
{
    public Guid? Id { get; set; }
    public string? Name { get; set; }
}

public sealed class OrgListVm
{
    public sealed record Row(Guid Id, string Name);

    public IReadOnlyList<Row> Organizations { get; set; } = Array.Empty<Row>();

    /// <summary>True only for SystemAdmin — Manager gets a read-only list (no Add link).</summary>
    public bool CanWrite { get; set; }
}
