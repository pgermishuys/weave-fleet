using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Jobs;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.IntegrationTests.Jobs;

/// <summary>
/// The queue end to end on one Fleet: jobs posted over the API are handed to machines by the dispatcher, wait with a
/// reason when no seat is free, and start when one frees. The machines and sessions are scripted; the passes are run by hand.
/// </summary>
[Trait("Category", "Integration")]
public sealed class JobsQueueTests
{
    private static readonly JobRepository _ledgerApi = new("/work/ledger-api", "ledger-api", "https://example.com/acme/ledger-api");

    [Fact]
    public async Task Jobs_start_on_free_seats_wait_with_a_reason_and_start_when_a_seat_frees()
    {
        await using var host = new JobsHost(on: true);
        var sessions = host.Services.GetRequiredService<ScriptedJobSessions>();
        host.Services.GetRequiredService<ScriptedJobMachines>().Machines =
        [
            new JobMachine(host.MachineId, "atlas", true, true, true, 2, [], [_ledgerApi]),
            new JobMachine("mini-id", "mini", false, true, true, 1, ["mac"], [_ledgerApi]),
        ];
        var dispatcher = host.Services.GetRequiredService<JobDispatcher>();

        var first = await host.PostAsync("Add rate limits");
        var second = await host.PostAsync("Fix the rounding");
        var mac = await host.PostAsync("Build the iOS app", "mac");
        var fourth = await host.PostAsync("Update the docs");
        (await dispatcher.RunOnceAsync(CancellationToken.None)).ShouldBeTrue();

        var queue = await host.ListAsync();
        queue.Job(first).MachineName.ShouldBe("atlas");
        queue.Job(second).MachineName.ShouldBe("atlas");
        queue.Job(mac).MachineName.ShouldBe("mini");
        queue.Job(fourth).State.ShouldBe(JobStates.Waiting);
        queue.Job(fourth).WhyWaiting.ShouldBe("No machine has a free seat.");
        queue.Machine(host.MachineId).Used.ShouldBe(2);

        // A new session reads idle before its first turn starts: that isn't done.
        await dispatcher.RunOnceAsync(CancellationToken.None);
        (await host.ListAsync()).Job(first).State.ShouldBe(JobStates.Running);

        sessions.Set(queue.Job(first).SessionId!, JobSessionState.InTurn);
        sessions.Set(queue.Job(second).SessionId!, JobSessionState.WaitingInput);
        await dispatcher.RunOnceAsync(CancellationToken.None);
        queue = await host.ListAsync();
        queue.Job(first).State.ShouldBe(JobStates.Running);
        queue.Job(second).State.ShouldBe(JobStates.NeedsYou);
        queue.Job(fourth).State.ShouldBe(JobStates.Waiting);

        sessions.Set(queue.Job(first).SessionId!, JobSessionState.Quiet);
        await dispatcher.RunOnceAsync(CancellationToken.None);
        queue = await host.ListAsync();
        queue.Job(first).State.ShouldBe(JobStates.Done);
        queue.Job(fourth).MachineName.ShouldBe("atlas");
        queue.Job(fourth).State.ShouldBe(JobStates.Running);
        sessions.Started.Select(start => start.Ask).ShouldBe(["Add rate limits", "Fix the rounding", "Build the iOS app", "Update the docs"]);
    }

    [Fact]
    public async Task A_start_that_fails_puts_the_job_back_to_waiting_with_the_reason()
    {
        await using var host = new JobsHost(on: true);
        var sessions = host.Services.GetRequiredService<ScriptedJobSessions>();
        sessions.FailWith = "Couldn't start: no disk space.";
        host.Services.GetRequiredService<ScriptedJobMachines>().Machines = [new JobMachine(host.MachineId, "atlas", true, true, true, 2, [], [_ledgerApi])];

        var id = await host.PostAsync("Add rate limits");
        await host.Services.GetRequiredService<JobDispatcher>().RunOnceAsync(CancellationToken.None);

        var job = (await host.ListAsync()).Job(id);
        job.State.ShouldBe(JobStates.Waiting);
        job.Error.ShouldBe("Couldn't start: no disk space.");
        (await host.ListAsync()).Machine(host.MachineId).Used.ShouldBe(0);
    }

    [Fact]
    public async Task Only_a_waiting_job_can_be_cancelled()
    {
        await using var host = new JobsHost(on: true);
        host.Services.GetRequiredService<ScriptedJobMachines>().Machines = [new JobMachine(host.MachineId, "atlas", true, true, true, 1, [], [_ledgerApi])];
        var running = await host.PostAsync("Add rate limits");
        await host.Services.GetRequiredService<JobDispatcher>().RunOnceAsync(CancellationToken.None);
        var waiting = await host.PostAsync("Fix the rounding");

        (await host.Client.DeleteAsync($"/api/jobs/{running}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await host.Client.DeleteAsync($"/api/jobs/{waiting}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await host.Services.GetRequiredService<JobDispatcher>().RunOnceAsync(CancellationToken.None)).ShouldBeTrue();
        (await host.ListAsync()).Jobs.Select(job => job.Id).ShouldBe([running]);
    }

    [Fact]
    public async Task Seats_and_labels_are_kept_per_machine_and_this_one_has_two_until_told_otherwise()
    {
        await using var host = new JobsHost(on: true);
        (await host.ListAsync()).Machine(host.MachineId).Seats.ShouldBe(2);

        var saved = await host.Client.PutAsJsonAsync($"/api/jobs/machines/{host.MachineId}", new SaveJobMachineRequest(3, [" mac ", "mac", "gpu"]));

        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        var machine = (await host.ListAsync()).Machine(host.MachineId);
        machine.Seats.ShouldBe(3);
        machine.Labels.ShouldBe(["mac", "gpu"]);
    }

    [Fact]
    public async Task With_jobs_off_every_route_answers_404_and_nothing_starts()
    {
        await using var host = new JobsHost(on: false);

        (await host.Client.GetAsync("/api/jobs")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await host.Client.PostAsJsonAsync("/api/jobs", new PostJobRequest("t", "a", "ledger-api", null, null))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await host.Client.DeleteAsync("/api/jobs/x")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_job_posted_while_jobs_are_off_never_starts()
    {
        await using var host = new JobsHost(on: false);
        host.Services.GetRequiredService<ScriptedJobMachines>().Machines = [new JobMachine(host.MachineId, "atlas", true, true, true, 2, [], [_ledgerApi])];
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<Domain.Repositories.IJobRepository>().InsertAsync(new Job
            {
                Id = "old", UserId = "local-user", Title = "t", Ask = "a", Repository = "ledger-api", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });

        (await host.Services.GetRequiredService<JobDispatcher>().RunOnceAsync(CancellationToken.None)).ShouldBeFalse();

        host.Services.GetRequiredService<ScriptedJobSessions>().Started.ShouldBeEmpty();
    }

    private sealed class ScriptedJobMachines : IJobMachines
    {
        public IReadOnlyList<JobMachine> Machines { get; set; } = [];

        public Task<IReadOnlyList<JobMachine>> ListAsync(CancellationToken ct) => Task.FromResult(Machines);
    }

    private sealed class ScriptedJobSessions : IJobSessions
    {
        private readonly Dictionary<string, JobSessionState> _states = [];

        public List<Job> Started { get; } = [];

        public string? FailWith { get; set; }

        public void Set(string sessionId, JobSessionState state) => _states[sessionId] = state;

        public Task<(string? SessionId, string? Error)> StartAsync(Job job, JobMachine machine, JobRepository repository, CancellationToken ct)
        {
            if (FailWith is not null)
                return Task.FromResult<(string?, string?)>((null, FailWith));

            Started.Add(job);
            var id = $"session-{Started.Count}";
            _states[id] = JobSessionState.Quiet;
            return Task.FromResult<(string?, string?)>((id, null));
        }

        public Task<JobSessionState?> ReadAsync(Job job, CancellationToken ct) => Task.FromResult<JobSessionState?>(_states[job.SessionId!]);
    }

    private sealed class JobsHost(bool on) : WebApplicationFactory<Program>
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("fleet-jobs-").FullName;
        private HttpClient? _client;

        public string MachineId => Services.GetRequiredService<MachineIdentityStore>().Get().Id;

        public HttpClient Client => _client ??= CreateAuthorizedClient();

        public async Task<string> PostAsync(string title, params string[] labels)
        {
            var response = await Client.PostAsJsonAsync("/api/jobs", new PostJobRequest(title, title, "https://example.com/acme/ledger-api", labels, null));
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        }

        public async Task<Queue> ListAsync() => new(await Client.GetFromJsonAsync<JsonElement>("/api/jobs"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<WeaveFleet.Application.Mods.Host.IModHostBun, WeaveFleet.Testing.Fakes.NoModHostBun>();
                services.AddSingleton<ScriptedJobMachines>();
                services.AddSingleton<IJobMachines>(sp => sp.GetRequiredService<ScriptedJobMachines>());
                services.AddSingleton<ScriptedJobSessions>();
                services.AddScoped<IJobSessions>(sp => sp.GetRequiredService<ScriptedJobSessions>());
            });
            var webRoot = Path.Combine(_dir, "wwwroot");
            Directory.CreateDirectory(webRoot);
            File.WriteAllText(Path.Combine(webRoot, "index.html"), "<html></html>");
            builder.UseEnvironment("Testing");
            builder.UseSetting(WebHostDefaults.WebRootKey, webRoot);
            builder.UseSetting("Fleet:DatabasePath", Path.Combine(_dir, "fleet.db"));
            builder.UseSetting("Fleet:AnalyticsDatabasePath", Path.Combine(_dir, "analytics.db"));
            builder.UseSetting("Fleet:AnalyticsEnabled", "false");
            builder.UseSetting("Fleet:Port", "0");
            builder.UseSetting("Fleet:Host", "127.0.0.1");
            builder.UseSetting("Fleet:Auth:Enabled", "false");
            builder.UseSetting("Fleet:Auth:TokenAuthEnabled", "true");
            builder.UseSetting("Fleet:Auth:RequireToken", "true");
            builder.UseSetting("Fleet:Harness:Jobs", on ? "true" : "false");
        }

        public override async ValueTask DisposeAsync()
        {
            _client?.Dispose();
            await base.DisposeAsync();
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private HttpClient CreateAuthorizedClient()
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Services.GetRequiredService<ILocalTokenAuthService>().Token);
            return client;
        }
    }

    private sealed class Queue(JsonElement body)
    {
        public IReadOnlyList<Row> Jobs { get; } = [.. body.GetProperty("jobs").EnumerateArray().Select(job => new Row(job))];

        public Row Job(string id) => Jobs.Single(job => job.Id == id);

        public MachineRow Machine(string idOrName) => body.GetProperty("machines").EnumerateArray()
            .Select(machine => new MachineRow(machine))
            .Single(machine => machine.Id == idOrName || machine.Name == idOrName);
    }

    private sealed class Row(JsonElement job)
    {
        public string Id => job.GetProperty("id").GetString()!;
        public string State => job.GetProperty("state").GetString()!;
        public string? MachineName => Text("machineName");
        public string? SessionId => Text("sessionId");
        public string? WhyWaiting => Text("whyWaiting");
        public string? Error => Text("error");

        private string? Text(string name) => job.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private sealed class MachineRow(JsonElement machine)
    {
        public string Id => machine.GetProperty("id").GetString()!;
        public string Name => machine.GetProperty("name").GetString()!;
        public int Seats => machine.GetProperty("seats").GetInt32();
        public int Used => machine.GetProperty("used").GetInt32();
        public IReadOnlyList<string> Labels => [.. machine.GetProperty("labels").EnumerateArray().Select(label => label.GetString()!)];
    }
}
