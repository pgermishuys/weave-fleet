using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Machines;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Machines;

/// <summary>
/// Another machine in this Fleet's list, answering its API from a script: <see cref="Answer"/> per request, or "didn't
/// answer" when <see cref="Away"/>. Every request is kept with its token and body.
/// </summary>
internal sealed class FakeMachine : HttpMessageHandler, IHttpClientFactory
{
    public const string Id = "machine-atlas";
    public const string Name = "atlas";
    public const string Token = "fmt_atlas";

    /// <summary>For services that need the way to other machines, in tests that never use it.</summary>
    public static RemoteAutomationRuns Unused { get; } = new FakeMachine().Runs;

    public FakeMachine(FakeTimeProvider? time = null)
    {
        Time = time ?? new FakeTimeProvider(new DateTimeOffset(2026, 10, 8, 7, 30, 0, TimeSpan.Zero));
        var protection = new EphemeralDataProtectionProvider();
        Service = new RemoteMachineService(
            Machines,
            this,
            protection,
            new MachineIdentityStore(Path.Combine(Path.GetTempPath(), $"fleet-{Guid.NewGuid():N}.db")),
            Time);
        Machines.UpsertAsync(new RemoteMachine
        {
            Id = Id,
            Name = Name,
            BaseUrl = "https://atlas.test",
            EncryptedToken = protection.CreateProtector("MachineTokens").Protect(Token),
            Status = RemoteMachineStatuses.Online,
        }).GetAwaiter().GetResult();
        Runs = new RemoteAutomationRuns(Service, this, Time);
    }

    public FakeTimeProvider Time { get; }
    public InMemoryRemoteMachineRepository Machines { get; } = new();
    public RemoteMachineService Service { get; }
    public RemoteAutomationRuns Runs { get; }

    /// <summary>How the machine answers a request ("POST /api/sessions"); null for a 404.</summary>
    public Func<string, (HttpStatusCode Status, string Body)?> Answer { get; set; } = _ => null;

    /// <summary>Awaited before each answer, with the request ("GET /api/sessions/s1").</summary>
    public Func<string, Task>? BeforeAnswer { get; set; }

    /// <summary>The machine is off: nothing answers.</summary>
    public bool Away { get; set; }

    public List<(string Request, string? Token, string? Body)> Requests { get; } = [];

    HttpClient IHttpClientFactory.CreateClient(string name) => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var line = $"{request.Method} {request.RequestUri!.AbsolutePath}";
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
            Requests.Add((line, request.Headers.Authorization?.Parameter, body));

        if (Away)
            throw new HttpRequestException("Connection refused");
        if (BeforeAnswer is { } before)
            await before(line);

        var (status, json) = Answer(line) ?? (HttpStatusCode.NotFound, """{"error":"Not found."}""");
        return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
