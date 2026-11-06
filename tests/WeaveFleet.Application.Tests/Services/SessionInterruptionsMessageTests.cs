using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>What Continue sends after a restart, and that the Interrupted card is on without a setting.</summary>
public sealed class SessionInterruptionsMessageTests
{
    private static SessionInterruption Cut(string? call, int? files, bool waiting = false) => new()
    {
        SessionId = "s1",
        UserId = "local-user",
        At = new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero),
        Waiting = waiting,
        LastCall = call,
        ChangedFiles = files,
    };

    [Fact]
    public void The_message_names_the_call_and_the_files()
    {
        SessionInterruptions.ContinueMessage(Cut("bash", 3)).ShouldBe(
            "Fleet restarted and cut off your last turn. The call that didn't finish: bash. 3 files changed so far, still on disk. Check where you got to and continue.");
    }

    [Fact]
    public void One_file_is_singular_and_a_missing_call_is_left_out()
    {
        SessionInterruptions.ContinueMessage(Cut(null, 1)).ShouldBe(
            "Fleet restarted and cut off your last turn. 1 file changed so far, still on disk. Check where you got to and continue.");
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(null, 0, false)]
    [InlineData("bash", 4, true)]
    public void With_nothing_stored_or_a_lost_question_the_plain_sentence_is_sent(string? call, int? files, bool waiting)
    {
        SessionInterruptions.ContinueMessage(Cut(call, files, waiting)).ShouldBe("Continue where you left off.");
    }

    [Fact]
    public void Restart_continue_is_on_by_default()
    {
        new FleetOptions().Harness.RestartContinue.ShouldBeTrue();
    }
}
