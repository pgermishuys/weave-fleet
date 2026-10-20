using WeaveFleet.Infrastructure.Updates;

namespace WeaveFleet.Infrastructure.Tests.Updates;

/// <summary>The two files the launcher and the app talk through (see scripts/launcher.sh).</summary>
public sealed class UpdateFilesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fleet-update-files-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string name, string json) => File.WriteAllText(Path.Combine(_root, name), json);

    [Fact]
    public void Outside_an_install_there_is_nothing_to_read_or_confirm()
    {
        var files = new UpdateFiles();

        files.IsInstalled.ShouldBeFalse();
        files.ReadTrial().ShouldBeNull();
        files.ReadFailure().ShouldBeNull();
        files.ConfirmTrial("0.59.1").ShouldBeFalse();
    }

    [Fact]
    public void The_new_version_ends_its_own_trial_and_a_failure_before_it_is_forgotten()
    {
        Write("update-trial.json", """{"from":"0.59.0","to":"0.59.1","startedAt":"2026-10-20T09:00:00Z"}""");
        Write("update-failed.json", """{"from":"0.59.0","to":"0.59.0-x","reason":"it exited with code 1 before it was ready","at":"2026-10-19T09:00:00Z"}""");
        var files = new UpdateFiles(_root);

        files.ReadTrial()!.To.ShouldBe("0.59.1");
        Directory.CreateDirectory(Path.Combine(_root, "update.prev"));
        File.WriteAllText(Path.Combine(_root, "update.prev", "fleet.db"), "old");
        Directory.CreateDirectory(Path.Combine(_root, "app.prev"));
        files.ConfirmTrial("0.59.1").ShouldBeTrue();

        Directory.Exists(Path.Combine(_root, "update.prev")).ShouldBeFalse("the database copy goes once the new version is confirmed");
        Directory.Exists(Path.Combine(_root, "app.prev")).ShouldBeTrue();

        files.ReadTrial().ShouldBeNull();
        files.ReadFailure().ShouldBeNull();
    }

    [Fact]
    public void A_different_version_leaves_the_trial_for_the_launcher_to_judge()
    {
        Write("update-trial.json", """{"from":"0.59.0","to":"0.59.2"}""");
        var files = new UpdateFiles(_root);

        files.ConfirmTrial("0.59.0").ShouldBeFalse();

        files.ReadTrial().ShouldNotBeNull();
    }

    [Fact]
    public void A_failure_is_read_as_the_launcher_wrote_it_and_a_broken_file_reads_as_none()
    {
        Write("update-failed.json", """{"from":"0.59.0","to":"0.59.2","reason":"it wasn't ready after 120 seconds","at":"2026-10-20T09:05:00Z"}""");
        var files = new UpdateFiles(_root);

        files.ReadFailure().ShouldBe(new UpdateFailure("0.59.0", "0.59.2", "it wasn't ready after 120 seconds", "2026-10-20T09:05:00Z"));

        Write("update-failed.json", "{ not json");
        files.ReadFailure().ShouldBeNull();
    }
}
