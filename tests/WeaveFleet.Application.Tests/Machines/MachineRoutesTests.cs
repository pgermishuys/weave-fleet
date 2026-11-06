using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Machines;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Machines;

/// <summary>
/// Picking which of a machine's addresses to talk to it on: in order, quickly, and never sending its token to an address
/// where it isn't the one answering.
/// </summary>
public sealed class MachineRoutesTests : IDisposable
{
    private const string Id = "machine-studio";
    private const string Token = "fmt_studio";
    private const string Name = "http://studio.tail4f2a.ts.net:2113";
    private const string TailIp = "http://100.88.14.3:2113";
    private const string Lan = "http://192.168.1.42:2113";
    private const string Host = "http://studio:2113";

    private readonly Network _network = new();
    private readonly InMemoryRemoteMachineRepository _machines = new();
    private readonly MachineRoutes _routes;
    private readonly RemoteMachineService _service;

    public MachineRoutesTests()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero));
        var protection = new EphemeralDataProtectionProvider();
        _service = new RemoteMachineService(
            _machines, _network, protection, new MachineIdentityStore(Path.Combine(Path.GetTempPath(), $"fleet-{Guid.NewGuid():N}.db")), time);
        _routes = new MachineRoutes(_machines, _service, _network, time) { Timeout = TimeSpan.FromMilliseconds(400), Gap = TimeSpan.FromMilliseconds(50) };
        _machines.UpsertAsync(new RemoteMachine
        {
            Id = Id,
            Name = "studio",
            BaseUrl = Name,
            EncryptedToken = protection.CreateProtector("MachineTokens").Protect(Token),
            Addresses = [.. new[] { Name, TailIp, Lan, Host }.Select(url => new RemoteMachineAddress(url, MachineAddresses.KindOf(url), url != Name))],
        }).GetAwaiter().GetResult();
    }

    public void Dispose() => _routes.Dispose();

    [Fact]
    public async Task The_address_in_use_is_kept_when_it_answers_and_nothing_else_is_tried()
    {
        _network.Answers[Name] = Id;

        (await _routes.PickAsync(Id, CancellationToken.None)).ShouldBe(Name);

        _network.Asked.ShouldBe([$"{Name}/api/machine/id"]);
    }

    [Fact]
    public async Task When_tailscale_is_down_the_local_network_is_used_and_remembered()
    {
        _network.Answers[Lan] = Id;
        _network.Answers[Host] = Id;

        (await _routes.PickAsync(Id, CancellationToken.None)).ShouldBe(Lan);

        var saved = (await _machines.GetAsync(Id))!;
        saved.BaseUrl.ShouldBe(Lan);
        saved.Addresses.Single(a => a.Url == Name).LastResult.ShouldBe(RemoteMachineAddressResults.NoAnswer);
        saved.Addresses.Single(a => a.Url == Lan).LastResult.ShouldBe(RemoteMachineAddressResults.Ok);
    }

    [Fact]
    public async Task A_different_fleet_at_an_old_address_is_skipped_and_never_sees_a_token()
    {
        _network.Answers[TailIp] = "machine-someone-else";
        _network.Answers[Lan] = Id;

        (await _routes.PickAsync(Id, CancellationToken.None)).ShouldBe(Lan);

        _network.Tokens.ShouldBeEmpty();
        (await _machines.GetAsync(Id))!.Addresses.Single(a => a.Url == TailIp).LastResult.ShouldBe(RemoteMachineAddressResults.OtherMachine);
    }

    [Fact]
    public async Task Nothing_answering_leaves_the_address_in_use()
    {
        (await _routes.PickAsync(Id, CancellationToken.None)).ShouldBe(Name);
        (await _machines.GetAsync(Id))!.BaseUrl.ShouldBe(Name);
    }

    [Fact]
    public async Task A_better_address_is_found_but_not_moved_to()
    {
        await _routes.UseAsync(Id, Lan);
        _network.Answers[TailIp] = Id;
        _network.Answers[Lan] = Id;

        (await _routes.BetterAsync(Id, Lan, CancellationToken.None)).ShouldBe(TailIp);
        (await _machines.GetAsync(Id))!.BaseUrl.ShouldBe(Lan);
        (await _routes.IsBestAsync(Id, Lan)).ShouldBeFalse();
        (await _routes.IsBestAsync(Id, Name)).ShouldBeTrue();
    }

    [Fact]
    public async Task Only_addresses_above_the_one_in_use_are_looked_at_for_a_better_one()
    {
        await _routes.UseAsync(Id, TailIp);
        _network.Answers[Lan] = Id;

        (await _routes.BetterAsync(Id, TailIp, CancellationToken.None)).ShouldBeNull();
        _network.Asked.ShouldBe([$"{Name}/api/machine/id"]);
    }

    [Fact]
    public async Task Learning_keeps_new_addresses_the_machine_reports_with_its_token_at_the_address_in_use()
    {
        _network.Identity[Name] = """{"id":"machine-studio","name":"studio","apiVersion":1,"authMode":"token","addresses":[{"url":"http://studio.tail4f2a.ts.net:2113","kind":"tailnet"},{"url":"http://192.168.1.77:2113","kind":"lan"}]}""";

        await _routes.LearnAsync(Id, Name, Token, CancellationToken.None);

        var urls = (await _machines.GetAsync(Id))!.Addresses.Select(a => a.Url).ToList();
        // The old local address and the others it no longer reports drop away; the new one takes the old one's place.
        urls.ShouldBe([Name, "http://192.168.1.77:2113"]);
        _network.Tokens.ShouldBe([(Name, Token)]);
    }

    [Fact]
    public async Task A_machine_with_one_address_is_never_probed()
    {
        await _machines.UpsertAsync((await _machines.GetAsync(Id))! with { Addresses = MachineAddresses.Seed(Name) });

        (await _routes.PickAsync(Id, CancellationToken.None)).ShouldBe(Name);
        _network.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reorder_saves_the_owners_order()
    {
        (await _routes.ReorderAsync(Id, [Lan, Name, TailIp, Host])).ShouldNotBeNull();
        (await _machines.GetAsync(Id))!.Addresses.Select(a => a.Url).ShouldBe([Lan, Name, TailIp, Host]);
        (await _routes.ReorderAsync(Id, [Lan, Name])).ShouldBeNull();
    }

    [Fact]
    public async Task The_race_takes_the_first_in_order_even_when_a_later_one_answers_sooner()
    {
        var (winner, _) = await MachineRoutes.RaceAsync(
            ["a", "b", "c"],
            async (url, ct) =>
            {
                await Task.Delay(url == "a" ? 200 : 10, ct);
                return url == "c" ? ProbeOutcome.NoAnswer : ProbeOutcome.Ok;
            },
            TimeSpan.FromMilliseconds(20),
            CancellationToken.None);

        winner.ShouldBe("a");
    }

    [Fact]
    public async Task The_race_moves_on_as_soon_as_the_ones_above_have_failed()
    {
        var started = new List<string>();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var (winner, outcomes) = await MachineRoutes.RaceAsync(
            ["a", "b", "c"],
            (url, _) =>
            {
                lock (started)
                    started.Add(url);
                return Task.FromResult(url == "c" ? ProbeOutcome.Ok : ProbeOutcome.NoAnswer);
            },
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        winner.ShouldBe("c");
        started.ShouldBe(["a", "b", "c"]);
        outcomes.Select(o => o.Outcome).ShouldBe([ProbeOutcome.NoAnswer, ProbeOutcome.NoAnswer, ProbeOutcome.Ok]);
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5), "it doesn't wait out the gap once the ones above failed");
    }

    [Fact]
    public async Task The_race_starts_the_next_before_a_slow_one_gives_up()
    {
        var gate = new TaskCompletionSource<ProbeOutcome>();

        var race = MachineRoutes.RaceAsync(
            ["slow", "fast"],
            (url, ct) => url == "slow" ? gate.Task.WaitAsync(ct) : Task.FromResult(ProbeOutcome.Ok),
            TimeSpan.FromMilliseconds(20),
            CancellationToken.None);

        await Task.Delay(200);
        race.IsCompleted.ShouldBeFalse("the slow one is above and may still answer");
        gate.SetResult(ProbeOutcome.NoAnswer);
        (await race).Winner.ShouldBe("fast");
    }

    /// <summary>
    /// Addresses as a network sees them: the machine id that answers at each (missing: nothing answers), and what
    /// <c>GET /api/machine</c> answers there with a token.
    /// </summary>
    private sealed class Network : HttpMessageHandler, IHttpClientFactory
    {
        public Dictionary<string, string> Answers { get; } = [];
        public Dictionary<string, string> Identity { get; } = [];
        public List<string> Asked { get; } = [];
        public List<(string Url, string Token)> Tokens { get; } = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var origin = uri.GetLeftPart(UriPartial.Authority);
            lock (Asked)
            {
                Asked.Add(uri.ToString());
                if (request.Headers.Authorization?.Parameter is { } token)
                    Tokens.Add((origin, token));
            }

            if (uri.AbsolutePath == "/api/machine" && Identity.TryGetValue(origin, out var identity))
                return Task.FromResult(Json(identity));
            if (uri.AbsolutePath == "/api/machine/id" && Answers.TryGetValue(origin, out var id))
                return Task.FromResult(Json($$"""{"id":"{{id}}","apiVersion":1}"""));
            throw new HttpRequestException("Connection refused");
        }

        private static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
