using NUnit.Framework;
using Shouldly;

namespace Nexus.Service.Tests;

/// <summary>Nexus starting before MySQL answers, as after a restart or a reboot.</summary>
[TestFixture]
public sealed class DatabaseStartTests
{
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(1);

    private static DatabaseException NotAnswering() =>
        new("Unable to connect to any of the specified MySQL hosts.") { NotAnswering = true };

    [Test]
    public async Task Waits_while_the_server_is_not_answering_then_goes_on()
    {
        var attempts = 0;
        var waits = 0;

        var waited = await DatabaseStart.WaitAsync(
            _ => ++attempts < 3 ? throw NotAnswering() : Task.CompletedTask,
            TimeSpan.FromMinutes(1),
            Pause,
            _ => waits++,
            CancellationToken.None);

        attempts.ShouldBe(3);
        waits.ShouldBe(1);
        waited.ShouldNotBeNull();
    }

    [Test]
    public async Task A_refusal_that_waiting_cannot_fix_fails_at_once()
    {
        var attempts = 0;

        await Should.ThrowAsync<DatabaseException>(() => DatabaseStart.WaitAsync(
            _ =>
            {
                attempts++;
                throw new DatabaseException("Access denied for user 'nexus'");
            },
            TimeSpan.FromMinutes(1),
            Pause,
            _ => { },
            CancellationToken.None));

        attempts.ShouldBe(1);
    }

    [Test]
    public async Task Gives_up_after_the_limit_with_the_servers_own_words()
    {
        var attempts = 0;

        var refusal = await Should.ThrowAsync<DatabaseException>(() => DatabaseStart.WaitAsync(
            _ =>
            {
                attempts++;
                throw NotAnswering();
            },
            TimeSpan.FromMilliseconds(30),
            Pause,
            _ => { },
            CancellationToken.None));

        refusal.Message.ShouldContain("Unable to connect");
        attempts.ShouldBeGreaterThan(1);
    }

    [Test]
    public async Task A_server_that_answers_at_once_is_not_waited_for()
    {
        var waits = 0;

        var waited = await DatabaseStart.WaitAsync(_ => Task.CompletedTask, TimeSpan.FromMinutes(1), Pause, _ => waits++, CancellationToken.None);

        waited.ShouldBeNull();
        waits.ShouldBe(0);
    }
}
