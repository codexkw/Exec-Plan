using ExecPlan.Domain.Enums;

namespace ExecPlan.Api.Areas.Admin.Models;

public sealed class UserListVm
{
    public sealed record Row(Guid Id, string UserName, string FullName, UserRole Role, string? Department, bool IsActive);

    public IReadOnlyList<Row> Users { get; set; } = Array.Empty<Row>();

    /// <summary>True only for SystemAdmin — Manager gets a read-only list (no Add link, no Edit links).</summary>
    public bool CanWrite { get; set; }
}
