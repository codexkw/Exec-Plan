using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure.Notifications;
using ExecPlan.Infrastructure.Persistence;
using FluentAssertions;

namespace ExecPlan.IntegrationTests;

public class DatabasePlaceholderProviderTests : IClassFixture<SqliteFixture>
{
    private readonly SqliteFixture _fx;
    public DatabasePlaceholderProviderTests(SqliteFixture fx) => _fx = fx;

    [Fact]
    public async Task StageNotification_adds_a_row_that_persists_after_SaveChangesAsync()
    {
        var activationId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();
        var utcNow = new DateTime(2026, 6, 30, 9, 0, 0, DateTimeKind.Utc);

        await using var ctx = _fx.NewContext();
        var uow = new UnitOfWork(ctx);
        var sut = new DatabasePlaceholderProvider(uow);

        var staged = sut.StageNotification(activationId, recipientId, NotificationKind.Notification, "ready check", utcNow);

        // Stage-only: nothing persisted yet until the caller saves (NFR-8 single-transaction rule).
        await using (var preSaveCtx = _fx.NewContext())
        {
            (await preSaveCtx.Set<NotificationLog>().FindAsync(staged.Id)).Should().BeNull();
        }

        await uow.SaveChangesAsync();

        await using var readCtx = _fx.NewContext();
        var saved = await readCtx.Set<NotificationLog>().FindAsync(staged.Id);
        saved.Should().NotBeNull();
        saved!.ActivationId.Should().Be(activationId);
        saved.RecipientUserId.Should().Be(recipientId);
        saved.Kind.Should().Be(NotificationKind.Notification);
        saved.Body.Should().Be("ready check");
    }

    [Fact]
    public async Task StageCallAttempt_adds_a_row_that_persists_after_SaveChangesAsync()
    {
        var activationId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var utcNow = new DateTime(2026, 6, 30, 9, 0, 0, DateTimeKind.Utc);

        await using var ctx = _fx.NewContext();
        var uow = new UnitOfWork(ctx);
        var sut = new DatabasePlaceholderProvider(uow);

        var staged = sut.StageCallAttempt(activationId, participantId, 1, utcNow);
        await uow.SaveChangesAsync();

        await using var readCtx = _fx.NewContext();
        var saved = await readCtx.Set<CallAttempt>().FindAsync(staged.Id);
        saved.Should().NotBeNull();
        saved!.ActivationId.Should().Be(activationId);
        saved.ParticipantId.Should().Be(participantId);
        saved.AttemptNumber.Should().Be(1);
    }
}
