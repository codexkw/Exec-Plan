namespace ExecPlan.Api.Areas.Admin.Models;

public sealed class LoginVm
{
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string? ReturnUrl { get; set; }
    public string? Error { get; set; }
}
