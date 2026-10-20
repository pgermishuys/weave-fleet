using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.AspNetCore.DataProtection;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Machines;
using WeaveFleet.Application.Updates;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Updates;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Updates;

/// <summary>The home Fleet gives each machine the rule it should follow, only while the Updates switch is on, and again when a machine reports another.</summary>
public sealed class UpdateRulePushServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fleet-rule-push-").FullName;
    private readonly FakeTimeProvider _time = new();
    private readonly Dictionary<string, string> _preferences = [];
    private readonly Dictionary<string, (string? Rule, bool WebApp)> _nodes = [];
    private readonly List<string> _pushes = [];
    private readonly UpdateRuleStore _ownRules;
    private readonly UpdateRulePushService _service;

    public UpdateRulePushServiceTests()
    {
        _ownRules = new UpdateRuleStore(Path.Combine(_root, "own.update.json"));
        var repository = new InMemoryRemoteMachineRepository();
        var protection = new EphemeralDataProtectionProvider();
        var identity = new MachineIdentityStore(Path.Combine(_root, "fleet.db"));
        var machines = new RemoteMachineService(repository, new NoHttp(), protection, identity, _time);
        foreach (var name in new[] { "falcon", "heron" })
        {
            _nodes[name] = (null, false);
            repository.UpsertAsync(new RemoteMachine { Id = $"id-{name}", Name = name, BaseUrl = $"https://{name}.test", EncryptedToken = protection.CreateProtector("MachineTokens").Protect("t") }).GetAwaiter().GetResult();
        }

        var services = new ServiceCollection().AddSingleton<IUserPreferenceRepository>(new Preferences(_preferences)).BuildServiceProvider();
        _service = new UpdateRulePushService(machines, new Nodes(this), _ownRules, identity, new FleetOptions { ServeUi = true }, services.GetRequiredService<IServiceScopeFactory>(), _time, NullLogger<UpdateRulePushService>.Instance);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(null, null, true, "ask")]
    [InlineData(null, null, false, "idle")]
    [InlineData(null, "overnight", false, "overnight")]
    [InlineData("ask", "idle", false, "ask")]
    [InlineData("nonsense", null, false, "idle")]
    public void A_machine_follows_its_override_then_the_default_then_what_suits_its_kind(string? overridden, string? chosen, bool webApp, string expected)
        => UpdateRulePushService.Effective(overridden, chosen, webApp).ShouldBe(expected);

    [Fact]
    public async Task Nothing_is_given_out_while_the_switch_is_off()
    {
        (await _service.SyncAsync(default)).ShouldBe(0);
        _pushes.ShouldBeEmpty();
        _ownRules.Rule.ShouldBeNull();
    }

    [Fact]
    public async Task With_the_switch_on_nodes_get_idle_unless_overridden_and_a_machine_reporting_another_rule_is_given_it_again()
    {
        _preferences[UpdateRulePushService.SwitchKey] = "true";
        _preferences[UpdateRulePushService.OverridesKey] = """{"id-heron":"ask"}""";

        (await _service.SyncAsync(default)).ShouldBe(3, "this Fleet, falcon and heron");
        _pushes.OrderBy(p => p).ShouldBe(["id-falcon:idle", "id-heron:ask"]);
        _ownRules.Rule.ShouldBe("ask", "a full Fleet asks");

        _pushes.Clear();
        (await _service.SyncAsync(default)).ShouldBe(0, "they report what they were given");
        _pushes.ShouldBeEmpty();

        _nodes["falcon"] = ("ask", false);
        (await _service.SyncAsync(default)).ShouldBe(1);
        _pushes.ShouldBe(["id-falcon:idle"]);

        _preferences[UpdateRulePushService.DefaultKey] = "overnight";
        _pushes.Clear();
        await _service.SyncAsync(default);
        _pushes.ShouldBe(["id-falcon:overnight"]);
        _ownRules.Rule.ShouldBe("overnight");
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("The push talks through IUpdateNodes.");
    }

    private sealed class Nodes(UpdateRulePushServiceTests test) : IUpdateNodes
    {
        public Task<NodeUpdateInfo?> ReadAsync(RemoteMachine machine, CancellationToken ct)
        {
            var (rule, webApp) = test._nodes[machine.Name];
            return Task.FromResult<NodeUpdateInfo?>(new NodeUpdateInfo("0.59.0", null, 0, true, null, null, rule, webApp));
        }

        public Task<NodeApplyResult> ApplyAsync(RemoteMachine machine, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> PushRuleAsync(RemoteMachine machine, string rule, CancellationToken ct)
        {
            test._pushes.Add($"{machine.Id}:{rule}");
            test._nodes[machine.Name] = (rule, test._nodes[machine.Name].WebApp);
            return Task.FromResult(true);
        }
    }

    private sealed class Preferences(Dictionary<string, string> values) : IUserPreferenceRepository
    {
        public Task<string?> GetAsync(string key) => Task.FromResult(values.GetValueOrDefault(key));
        public Task<IReadOnlyDictionary<string, string>> GetAllAsync() => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(values));
        public Task SetAsync(string key, string value) { values[key] = value; return Task.CompletedTask; }
    }
}
