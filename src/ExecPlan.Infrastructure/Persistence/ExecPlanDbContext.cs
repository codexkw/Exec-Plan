using ExecPlan.Domain.Common;
using ExecPlan.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ExecPlan.Infrastructure.Persistence;

public class ExecPlanDbContext : DbContext
{
    public ExecPlanDbContext(DbContextOptions<ExecPlanDbContext> o) : base(o) { }

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<PlanContact> PlanContacts => Set<PlanContact>();
    public DbSet<PlanActivator> PlanActivators => Set<PlanActivator>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMembership> TeamMemberships => Set<TeamMembership>();
    public DbSet<ShiftAssignment> ShiftAssignments => Set<ShiftAssignment>();
    public DbSet<TaskTemplate> TaskTemplates => Set<TaskTemplate>();
    public DbSet<PlanActivation> PlanActivations => Set<PlanActivation>();
    public DbSet<ActivationParticipant> ActivationParticipants => Set<ActivationParticipant>();
    public DbSet<ExecutionTask> ExecutionTasks => Set<ExecutionTask>();
    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();
    public DbSet<CallAttempt> CallAttempts => Set<CallAttempt>();
    public DbSet<ResponseStatus> ResponseStatuses => Set<ResponseStatus>();
    public DbSet<EscalationLog> EscalationLogs => Set<EscalationLog>();
    public DbSet<BroadcastMessage> BroadcastMessages => Set<BroadcastMessage>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.ApplyConfigurationsFromAssembly(typeof(ExecPlanDbContext).Assembly);

        // Global: Guid keys are app-assigned (ValueGeneratedNever) so SaveChanges never re-generates them.
        foreach (var et in b.Model.GetEntityTypes())
        {
            var pk = et.FindPrimaryKey()?.Properties.FirstOrDefault(p => p.Name == "Id");
            if (pk is not null)
            {
                pk.ValueGenerated = ValueGenerated.Never;
            }
        }
    }

    // DbContext.SaveChanges()/SaveChangesAsync(CancellationToken) both funnel into these two
    // (bool, [CancellationToken]) overloads, so overriding them here stamps timestamps on every
    // sync and async save entry point without double-stamping.
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void StampTimestamps()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAtUtc = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAtUtc = now;
            }
        }
    }
}
