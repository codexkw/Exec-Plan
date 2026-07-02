namespace ExecPlan.Application.Common;

/// <summary>
/// Canonical, stable machine codes attached to user-facing <see cref="AppException"/>s. They are emitted
/// on the <c>/api</c> JSON error body (<c>{ error, kind, code }</c>) and on the auth 401 body
/// (<c>{ message, code }</c>) so clients branch and localize on the CODE, never on the English message
/// (which is for logs/developers). This class is also the authoritative key list a client error-string
/// catalogue (mobile ARB, web resx) mirrors — every code here should have a localized message on each
/// client. Kept as plain string consts because the values also travel over the wire.
/// </summary>
public static class AppErrorCodes
{
    // Activation (one-tap launch guards).
    public const string PlanAlreadyActive = "PlanAlreadyActive";
    public const string NoOneOnDuty = "NoOneOnDuty";
    public const string NotAuthorizedToActivate = "NotAuthorizedToActivate";

    // Live execution edits.
    public const string AlreadyClosed = "AlreadyClosed";
    public const string CrossTeamReassign = "CrossTeamReassign";
    public const string RaiseIssueLeaderOnly = "RaiseIssueLeaderOnly";
    public const string SetSubstituteForbidden = "SetSubstituteForbidden";
    public const string CloseManagerOnly = "CloseManagerOnly";

    // Escalation / broadcast.
    public const string EscalateClosed = "EscalateClosed";
    public const string BroadcastEmpty = "BroadcastEmpty";
    public const string BroadcastManagerOnly = "BroadcastManagerOnly";

    // Auth (the 401 surface — {message, code}).
    public const string InvalidCredentials = "InvalidCredentials";
    public const string RefreshInvalid = "RefreshInvalid";
}
