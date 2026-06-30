namespace ExecPlan.Application.Activation;

/// <summary>
/// One-tap activation: freezes an immutable snapshot of who is on duty for the resolved Kuwait
/// shift, generates their execution tasks from templates, and fires the first call attempt — all in
/// a single atomic SaveChanges. Returns the new activation id.
/// </summary>
public interface IActivationService
{
    Task<Guid> ActivateAsync(Guid planId, Guid actingUserId, CancellationToken ct = default);
}
