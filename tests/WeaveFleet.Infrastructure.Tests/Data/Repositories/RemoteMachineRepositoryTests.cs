using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

/// <summary>Other machines in this Fleet's list (<c>machines</c>), on a real database.</summary>
public sealed class RemoteMachineRepositoryTests
{
    private static RemoteMachine Mini() => new()
    {
        Id = "machine-mini",
        Name = "mini",
        BaseUrl = "http://127.0.0.1:5582",
        EncryptedToken = "protected",
        Os = "linux",
        AddedAt = new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero),
        Status = RemoteMachineStatuses.Online,
    };

    [Fact]
    public async Task a_machine_asks_before_agents_send_it_work_until_the_owner_says_always_or_never()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;
        var repo = new RemoteMachineRepository(factory);

        await repo.UpsertAsync(Mini());
        (await repo.GetAsync("machine-mini"))!.AgentWork.ShouldBe(MachineAgentWork.Ask);

        await repo.UpsertAsync(Mini() with { AgentWork = MachineAgentWork.Always });
        (await repo.GetAsync("machine-mini"))!.AgentWork.ShouldBe(MachineAgentWork.Always);
        await repo.UpsertAsync(Mini() with { AgentWork = MachineAgentWork.Never });
        (await repo.ListAsync()).ShouldHaveSingleItem().AgentWork.ShouldBe(MachineAgentWork.Never);
    }

    [Fact]
    public async Task a_machine_agents_were_allowed_on_before_is_always_and_the_rest_ask()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;
        var repo = new RemoteMachineRepository(factory);
        await repo.UpsertAsync(Mini());
        await repo.UpsertAsync(Mini() with { Id = "machine-other", Name = "other" });

        // As migration 072 found them: one allowed with the old box, one not.
        using (var connection = factory.CreateConnection())
        {
            foreach (var sql in (string[])["UPDATE machines SET agents_allowed = 1, agent_work = 'ask' WHERE id = 'machine-mini'",
                         MigrationSql("072_machine_agent_work.sql").Split('\n').Last(line => line.StartsWith("UPDATE", StringComparison.Ordinal))])
            {
                if (connection.State != System.Data.ConnectionState.Open)
                    connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }

        (await repo.GetAsync("machine-mini"))!.AgentWork.ShouldBe(MachineAgentWork.Always);
        (await repo.GetAsync("machine-other"))!.AgentWork.ShouldBe(MachineAgentWork.Ask);
    }

    private static string MigrationSql(string name)
    {
        var assembly = typeof(RemoteMachineRepository).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(resource => resource.EndsWith(name, StringComparison.Ordinal)))!;
        return new StreamReader(stream).ReadToEnd();
    }

    [Fact]
    public async Task the_ssh_destination_is_kept_and_cleared_with_the_machine()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;
        var repo = new RemoteMachineRepository(factory);

        await repo.UpsertAsync(Mini());
        (await repo.GetAsync("machine-mini"))!.SshDestination.ShouldBeNull();

        await repo.UpsertAsync(Mini() with { SshDestination = "alice@mini:2222" });
        (await repo.GetAsync("machine-mini"))!.SshDestination.ShouldBe("alice@mini:2222");

        await repo.UpsertAsync(Mini());
        (await repo.GetAsync("machine-mini"))!.SshDestination.ShouldBeNull();
    }
}
