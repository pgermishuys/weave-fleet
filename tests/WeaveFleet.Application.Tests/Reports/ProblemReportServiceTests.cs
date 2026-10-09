using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Reports;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Reports;

public sealed class ProblemReportServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fleet-report-").FullName;
    private readonly InMemorySessionRepository _sessions = new();
    private readonly InMemoryWorkspaceRepository _workspaces = new();
    private readonly InMemoryWorkspaceRootRepository _roots = new();
    private readonly FakeCredentialStore _credentials = new();
    private readonly SessionActivityTracker _activity = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 27, 14, 3, 10, TimeSpan.Zero));
    private readonly FleetOptions _options = new();
    private readonly InboxHandler _inbox = new();

    public ProblemReportServiceTests()
    {
        _workspaces.Seed(new Workspace
        {
            Id = "ws-000001",
            Directory = "/work/acme-payments",
            IsolationStrategy = "worktree",
            Branch = "fix/eu-rounding",
            UserId = TestUserContext.DefaultUserId,
        });
        _sessions.Seed(new Session
        {
            Id = "session-000001",
            WorkspaceId = "ws-000001",
            OpencodeSessionId = "ses_harness01",
            Title = "Fix invoice rounding for EU",
            Directory = "/work/acme-payments",
            HarnessType = "opencode2",
            ActivityStatus = "busy",
            UserId = TestUserContext.DefaultUserId,
        });
        _sessions.Seed(new Session
        {
            Id = "session-000002",
            Title = "Bump Postgres to 17",
            Directory = "/work/platform-infra",
            UserId = TestUserContext.DefaultUserId,
        });
        _roots.Seed(new WorkspaceRoot { Id = "root-1", Path = "/work" });
        _credentials.Seed(new UserCredential
        {
            Id = "cred-1",
            UserId = TestUserContext.DefaultUserId,
            Namespace = "provider",
            Kind = "api-key",
            Label = "Anthropic",
            EncryptedValue = "plain-secret-value-123",
        });
        _activity.Update("session-000001", "idle", TestUserContext.DefaultUserId);

        File.WriteAllText(Path.Combine(_dir, "fleet-2026-09-27.log"), """
            2026-09-27 14:01:58.204 [DBG] [OpenCode2HarnessSession] session-000001 status working
            2026-09-27 14:02:00.000 [DBG] [OpenCode2HarnessSession] session-000002 status working
            2026-09-27 14:02:03.911 [WRN] [SessionHub] Connection aborted: WebSocket closed (1006)
            2026-09-27 14:02:31.118 [INF] [OpenCode2HarnessSession] ses_harness01 idle (cwd /work/acme-payments)
            2026-09-27 14:02:44.502 [ERR] [GitHubWatcher] 401 with plain-secret-value-123
            """);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ProblemReportService Service(FleetLogLocation? log = null) => new(
        _sessions,
        _workspaces,
        _roots,
        _credentials,
        new TestUserContext(),
        _activity,
        new HarnessAvailabilityCache(new FakeHarnessRegistry(), _time, NullLogger<HarnessAvailabilityCache>.Instance),
        new MachineIdentityStore(Path.Combine(_dir, "fleet.db")),
        [],
        _options,
        log ?? new FleetLogLocation(true, _dir, "fleet"),
        new SingleClientFactory(new HttpClient(_inbox)),
        _time,
        NullLogger<ProblemReportService>.Instance);

    private static PrepareReportRequest Request(string description = "Stopped updating in /work/acme-payments on fix/eu-rounding.") => new(
        "bug",
        description,
        "New messages show up.",
        "session-000001",
        new ReportIncludes(),
        new ReportClientContext(
            App: "Desktop app",
            Screen: "Sessions",
            ShownStatus: "Working",
            Where: [new ReportFact("Right panel", "Files")],
            Connection: [new ReportFact("Now", "connected · WebSockets")],
            PrivateValues: [new ReportPrivateValue("machine", "studio-mac"), new ReportPrivateValue("url", "https://studio-mac.tail1234.ts.net:6262")]));

    [Fact]
    public async Task Prepare_replaces_the_sessions_private_details_everywhere_in_the_report()
    {
        var report = await Service().PrepareAsync(
            Request("Fix invoice rounding for EU stopped updating in /work/acme-payments on fix/eu-rounding, also studio-mac."),
            CancellationToken.None);

        report.Body.ShouldNotContain("acme-payments");
        report.Body.ShouldNotContain("fix/eu-rounding");
        report.Body.ShouldNotContain("Fix invoice rounding");
        report.Body.ShouldNotContain("studio-mac");
        report.Body.ShouldContain("## What happened\n‹session-1› stopped updating in ‹folder-1› on ‹branch-1›, also ‹machine-");
        report.Title.ShouldBe("‹session-1› stopped updating in ‹folder-1› on ‹branch-1›, also ‹machine-2›");
        report.Replacements.ShouldContain(r => r.Label == "‹folder-1›" && r.Shown == "/work/acme-payments");
    }

    [Fact]
    public async Task Prepare_describes_where_the_person_was_and_what_the_server_knows()
    {
        var report = await Service().PrepareAsync(Request(), CancellationToken.None);

        report.Body.ShouldContain("- **App:** Desktop app");
        report.Body.ShouldContain("- **Screen:** Sessions");
        report.Body.ShouldContain("- **Right panel:** Files");
        report.Body.ShouldContain("- **Status shown:** Working");
        report.Body.ShouldContain("- **Session:** session-000001 · opencode2 · manual");
        report.Body.ShouldContain("- **Status on the server:** idle");
        report.Body.ShouldContain("- **Works in:** a worktree · branch ‹branch-1›");
        report.Body.ShouldContain("## Connection\n- **Now:** connected · WebSockets");
        report.Labels.ShouldContain("opencode2");
        report.Problems.ShouldBeEmpty();
        report.CanSend.ShouldBeTrue();
    }

    [Fact]
    public async Task Prepare_takes_the_sessions_log_lines_and_every_warning_with_secrets_replaced()
    {
        var report = await Service().PrepareAsync(Request(), CancellationToken.None);

        report.LogEntries.ShouldBe(4);
        report.Log.ShouldNotBeNull();
        report.Log.ShouldContain("session-000001 status working");
        report.Log.ShouldContain("ses_harness01 idle (cwd ‹folder-1›)");
        report.Log.ShouldContain("WebSocket closed (1006)");
        report.Log.ShouldContain("401 with ‹secret-");
        report.Log.ShouldNotContain("session-000002");
        report.Log.ShouldNotContain("plain-secret-value-123");
    }

    [Fact]
    public async Task Prepare_leaves_out_what_the_person_unticked()
    {
        var report = await Service().PrepareAsync(
            Request() with { Include = new ReportIncludes(Environment: false, Where: false, Connection: false, Log: false) },
            CancellationToken.None);

        report.Body.ShouldNotContain("## Fleet and system");
        report.Body.ShouldNotContain("## Where");
        report.Body.ShouldNotContain("## Connection");
        report.Log.ShouldBeNull();
    }

    [Fact]
    public async Task Prepare_names_the_log_as_missing_when_log_files_are_off()
    {
        var report = await Service(FleetLogLocation.Disabled).PrepareAsync(Request(), CancellationToken.None);

        report.Log.ShouldBeNull();
        report.Problems.ShouldContain(p => p.Item == "Fleet log");
    }

    [Fact]
    public async Task Send_posts_the_reviewed_report_to_the_inbox_and_returns_its_id()
    {
        _inbox.Respond(HttpStatusCode.Created, """{"id":"r_abc123"}""");

        var id = await Service().SendAsync(
            new SendReportRequest("bug", "Title", "Body", "log line", "iVBORw0KGgo=", " @sam ", ["opencode2"]),
            CancellationToken.None);

        id.ShouldBe("r_abc123");
        _inbox.Uri.ShouldBe(new Uri("https://issues.tryweave.io/v1/reports"));
        using var sent = JsonDocument.Parse(_inbox.Body!);
        sent.RootElement.GetProperty("kind").GetString().ShouldBe("bug");
        sent.RootElement.GetProperty("contact").GetString().ShouldBe("@sam");
        sent.RootElement.GetProperty("screenshot").GetString().ShouldBe("iVBORw0KGgo=");
        sent.RootElement.GetProperty("labels")[0].GetString().ShouldBe("opencode2");
        sent.RootElement.GetProperty("fleetVersion").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Send_passes_on_the_inboxs_reason_when_it_refuses()
    {
        _inbox.Respond(HttpStatusCode.TooManyRequests, """{"error":"Too many reports from this address."}""");

        var error = await Should.ThrowAsync<ReportSendException>(() => Service().SendAsync(
            new SendReportRequest("bug", "T", "B", null, null, null, null), CancellationToken.None));

        error.Message.ShouldBe("Too many reports from this address.");
        error.StatusCode.ShouldBe(429);
    }

    [Fact]
    public async Task Send_says_so_when_the_inbox_cant_be_reached()
    {
        _inbox.Fail = true;

        var error = await Should.ThrowAsync<ReportSendException>(() => Service().SendAsync(
            new SendReportRequest("bug", "T", "B", null, null, null, null), CancellationToken.None));

        error.Message.ShouldContain("Couldn't reach the report inbox");
        error.StatusCode.ShouldBe(502);
    }

    [Fact]
    public async Task Send_is_off_without_an_inbox_address()
    {
        _options.Reports.InboxUrl = "";

        (await Service().PrepareAsync(Request(), CancellationToken.None)).CanSend.ShouldBeFalse();
        await Should.ThrowAsync<ReportSendException>(() => Service().SendAsync(
            new SendReportRequest("bug", "T", "B", null, null, null, null), CancellationToken.None));
    }

    [Fact]
    public void The_archive_holds_the_report_the_log_and_the_screenshot()
    {
        var png = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        var bytes = ProblemReportService.BuildArchive(
            new SendReportRequest("bug", "It broke", "## What happened\nIt broke.", "a log line", $"data:image/png;base64,{png}", "@sam", []));

        using var zip = new ZipArchive(new MemoryStream(bytes));
        zip.Entries.Select(e => e.FullName).ShouldBe(["report.md", "fleet.log", "screenshot.png"]);
        using var reader = new StreamReader(zip.GetEntry("report.md")!.Open());
        var markdown = reader.ReadToEnd();
        markdown.ShouldStartWith("# It broke\n");
        markdown.ShouldContain("reply to @sam");
        markdown.ShouldContain("## What happened\nIt broke.");
    }

    [Theory]
    [InlineData("It stopped. Then more.", "It stopped")]
    [InlineData("First line\nsecond line", "First line")]
    [InlineData("   ", "Problem report")]
    public void The_title_is_the_descriptions_first_sentence(string description, string title)
    {
        ProblemReportComposer.TitleFrom(description).ShouldBe(title);
    }

    [Fact]
    public void A_long_title_is_cut_at_a_word()
    {
        var title = ProblemReportComposer.TitleFrom(string.Join(' ', Enumerable.Repeat("word", 40)));

        title.Length.ShouldBeLessThanOrEqualTo(ProblemReportComposer.MaxTitleLength + 1);
        title.ShouldEndWith("word…");
    }

    private sealed class InboxHandler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.Created;
        private string _response = """{"id":"r_default"}""";

        public bool Fail { get; set; }
        public Uri? Uri { get; private set; }
        public string? Body { get; private set; }

        public void Respond(HttpStatusCode status, string body) => (_status, _response) = (status, body);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Fail) throw new HttpRequestException("connection refused");
            Uri = request.RequestUri;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(_status) { Content = new StringContent(_response, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
