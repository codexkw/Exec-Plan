namespace ExecPlan.Cli;

/// <summary>
/// Parsed arguments for the <c>run-escalation</c> command (FR-ESC-1): either one specific activation
/// (<c>--activation &lt;guid&gt;</c>) or every currently-Active activation (<c>--all-active</c>).
/// Mutually exclusive — exactly one must be supplied.
/// </summary>
public sealed record EscalationArgs(Guid? ActivationId, bool AllActive)
{
    /// <summary>
    /// Parses the arguments that follow the <c>run-escalation</c> command token (callers strip that
    /// token before calling this). Returns <c>null</c> on any malformed input so the caller can print
    /// usage and exit non-zero without throwing.
    /// </summary>
    public static EscalationArgs? Parse(IReadOnlyList<string> args)
    {
        Guid? activationId = null;
        var allActive = false;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--activation":
                    if (i + 1 >= args.Count || !Guid.TryParse(args[i + 1], out var id))
                    {
                        return null;
                    }

                    activationId = id;
                    i++;
                    break;

                case "--all-active":
                    allActive = true;
                    break;

                default:
                    return null;
            }
        }

        // Exactly one of --activation / --all-active must be supplied.
        var hasActivation = activationId is not null;
        if (hasActivation == allActive)
        {
            return null;
        }

        return new EscalationArgs(activationId, allActive);
    }
}
