using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Machines;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Machines;

/// <summary>Each way reaching a machine can fail names its own step, and the steps before it are ticked.</summary>
public sealed class MachineDiagnosisTests
{
    private const string Url = "http://falcon.test:2113";

    private static HttpRequestException Network(HttpRequestError error, SocketError? socket = null) =>
        new(error, "failed", socket is null ? null : new SocketException((int)socket));

    [Fact]
    public void A_tailnet_name_that_does_not_resolve_points_at_MagicDNS()
    {
        var problem = MachineDiagnoses.FromException(Network(HttpRequestError.NameResolutionError, null), "http://falcon.tail9c2e.ts.net:2113");

        problem.Diagnosis!.Code.ShouldBe(MachineDiagnoses.NameNotFound);
        problem.Diagnosis.Try.ShouldBe("Turn on MagicDNS in Tailscale on this machine, or paste the 100.x address that fleet node pair lists under Also reachable at, with the code.");
    }

    [Fact]
    public async Task A_refused_connection_says_fleet_is_not_running_with_the_name_and_port_steps()
    {
        var machine = new FakeMachine { Fails = Network(HttpRequestError.ConnectionError, SocketError.ConnectionRefused) };

        var (found, problem) = await machine.Service.CheckAsync(FakeMachine.Id, CancellationToken.None);

        found.ShouldBeTrue();
        problem!.Diagnosis!.Code.ShouldBe(MachineDiagnoses.FleetNotRunning);
        problem.Diagnosis.Headline.ShouldBe("Fleet isn't running on atlas");
        problem.Diagnosis.Try.ShouldBe("On atlas, run fleet node up. It starts Fleet in the background.");
        problem.Diagnosis.Steps.Select(step => step.State).ShouldBe(
            [MachineStepStates.Ok, MachineStepStates.Ok, MachineStepStates.Failed, MachineStepStates.NotChecked, MachineStepStates.NotChecked, MachineStepStates.NotChecked]);
        problem.Message.ShouldContain("Couldn't reach");
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, null, MachineDiagnoses.NameNotFound, 0)]
    [InlineData(HttpRequestError.ConnectionError, SocketError.HostNotFound, MachineDiagnoses.NameNotFound, 0)]
    [InlineData(HttpRequestError.ConnectionError, SocketError.TimedOut, MachineDiagnoses.NotAnswering, 1)]
    [InlineData(HttpRequestError.ConnectionError, SocketError.HostUnreachable, MachineDiagnoses.NotAnswering, 1)]
    [InlineData(HttpRequestError.SecureConnectionError, null, MachineDiagnoses.CantConnect, 2)]
    [InlineData(HttpRequestError.Unknown, null, MachineDiagnoses.CantConnect, 1)]
    public void A_network_failure_is_told_apart_by_its_cause(HttpRequestError error, SocketError? socket, string code, int failedStep)
    {
        var problem = MachineDiagnoses.FromException(Network(error, socket), Url);

        problem.Diagnosis!.Code.ShouldBe(code);
        problem.Diagnosis.Steps.ToList().FindIndex(step => step.State == MachineStepStates.Failed).ShouldBe(failedStep);
    }

    [Fact]
    public void A_timeout_means_nothing_answered_in_time()
    {
        var problem = MachineDiagnoses.FromException(new TaskCanceledException(), Url);

        problem.Diagnosis!.Code.ShouldBe(MachineDiagnoses.NotAnswering);
        problem.Diagnosis.Headline.ShouldBe("falcon.test isn't answering");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, MachineDiagnoses.KeyRejected, 5, "That token wasn't accepted.")]
    [InlineData(HttpStatusCode.NotFound, MachineDiagnoses.TooOld, 4, "That Fleet is too old to add as a machine. Update it first.")]
    [InlineData(HttpStatusCode.Found, MachineDiagnoses.Redirected, 3, "http://falcon.test:2113 redirects elsewhere. Use the address it redirects to.")]
    [InlineData(HttpStatusCode.BadGateway, MachineDiagnoses.NotFleet, 3, "The machine answered 502.")]
    public async Task An_answer_that_is_not_ok_names_the_step_and_keeps_the_old_text(HttpStatusCode status, string code, int failedStep, string message)
    {
        var machine = new FakeMachine { Answer = _ => (status, "{}") };

        var (_, problem) = await machine.Service.IdentifyAsync(Url, "token", CancellationToken.None);

        problem!.Message.ShouldBe(message);
        problem.Diagnosis!.Code.ShouldBe(code);
        problem.Diagnosis.Steps.ToList().FindIndex(step => step.State == MachineStepStates.Failed).ShouldBe(failedStep);
    }

    [Theory]
    [InlineData("not json", MachineDiagnoses.NotFleet)]
    [InlineData("""{"apiVersion":1,"authMode":"token"}""", MachineDiagnoses.NotFleet)]
    [InlineData("""{"apiVersion":7,"authMode":"token","id":"x"}""", MachineDiagnoses.VersionMismatch)]
    [InlineData("""{"apiVersion":1,"authMode":"oidc","id":"x"}""", MachineDiagnoses.UnsupportedSignIn)]
    public async Task An_answer_that_is_not_what_fleet_says_is_told_apart(string body, string code)
    {
        var machine = new FakeMachine { Answer = _ => (HttpStatusCode.OK, body) };

        var (info, problem) = await machine.Service.IdentifyAsync(Url, "token", CancellationToken.None);

        info.ShouldBeNull();
        problem!.Diagnosis!.Code.ShouldBe(code);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, MachineDiagnoses.CodeExpired)]
    [InlineData(HttpStatusCode.TooManyRequests, MachineDiagnoses.TooManyCodes)]
    public async Task A_code_that_is_turned_away_says_why(HttpStatusCode status, string code)
    {
        var machine = new FakeMachine { Answer = _ => (status, "{}") };

        var (found, problem) = await machine.Service.PreviewByCodeAsync(Url, "ABCD-EFGH", CancellationToken.None);

        found.ShouldBeNull();
        problem!.Diagnosis!.Code.ShouldBe(code);
        problem.Diagnosis.Steps[^1].State.ShouldBe(MachineStepStates.Failed);
    }

    [Fact]
    public async Task A_machine_that_answers_has_no_problem_and_an_unknown_one_is_not_found()
    {
        var machine = new FakeMachine { Answer = _ => (HttpStatusCode.OK, $$"""{"apiVersion":1,"authMode":"token","id":"{{FakeMachine.Id}}","name":"atlas"}""") };

        (await machine.Service.CheckAsync(FakeMachine.Id, CancellationToken.None)).ShouldBe((true, null));
        (await machine.Service.CheckAsync("nope", CancellationToken.None)).Found.ShouldBeFalse();
    }

    [Fact]
    public async Task A_closed_port_and_an_unknown_name_are_told_apart_on_the_real_network()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var closedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var service = new RemoteMachineService(
            new InMemoryRemoteMachineRepository(),
            new RealClients(),
            new EphemeralDataProtectionProvider(),
            new MachineIdentityStore(Path.Combine(Path.GetTempPath(), $"fleet-{Guid.NewGuid():N}.db")),
            new FakeTimeProvider());

        var refused = await service.IdentifyAsync($"http://127.0.0.1:{closedPort}", "token", CancellationToken.None);
        var unknown = await service.IdentifyAsync("http://nowhere.invalid:2113", "token", CancellationToken.None);

        refused.Error!.Diagnosis!.Code.ShouldBe(MachineDiagnoses.FleetNotRunning);
        unknown.Error!.Diagnosis!.Code.ShouldBe(MachineDiagnoses.NameNotFound);
    }

    private sealed class RealClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(5) };
    }
}
