using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Credentials;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Reports;

/// <summary>
/// Help → Report a problem. Gathers what a report carries only when someone opens it (nothing is recorded ahead of
/// time), replaces private details, and sends the reviewed report to the Fleet maintainers' inbox or packs it into a
/// file. Each part is gathered on its own with a time limit; one that fails is left out and named, and the rest of the
/// report still works.
/// </summary>
public sealed partial class ProblemReportService(
    ISessionRepository sessions,
    IWorkspaceRepository workspaces,
    IWorkspaceRootRepository workspaceRoots,
    ICredentialStore credentials,
    IUserContext user,
    SessionActivityTracker activity,
    HarnessAvailabilityCache harnesses,
    MachineIdentityStore machine,
    IEnumerable<ILocalTokenAuthService> localTokens,
    FleetOptions options,
    FleetLogLocation logLocation,
    IHttpClientFactory httpClientFactory,
    TimeProvider time,
    ILogger<ProblemReportService> logger)
{
    public const string HttpClientName = "FleetReports";

    /// <summary>How long each part of a report may take to gather.</summary>
    public static readonly TimeSpan PartTimeLimit = TimeSpan.FromSeconds(2);

    /// <summary>How far back the log excerpt reaches.</summary>
    public static readonly TimeSpan LogWindow = TimeSpan.FromMinutes(15);

    public const int MaxDescriptionLength = 10_000;

    /// <summary>Whether reports can be sent from this Fleet (an inbox address is set).</summary>
    public bool CanSend => InboxUri is not null;

    private Uri? InboxUri =>
        Uri.TryCreate(options.Reports.InboxUrl?.Trim(), UriKind.Absolute, out var uri) ? uri : null;

    public async Task<PreparedReport> PrepareAsync(PrepareReportRequest request, CancellationToken ct)
    {
        var include = request.Include ?? new ReportIncludes();
        var problems = new List<ReportItemProblem>();

        Session? session = null;
        Workspace? workspace = null;
        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            session = await sessions.GetByIdAsync(request.SessionId).ConfigureAwait(false);
            if (session is not null && !string.IsNullOrEmpty(session.WorkspaceId))
                workspace = await workspaces.GetByIdAsync(session.WorkspaceId).ConfigureAwait(false);
        }

        // Replacing private details isn't optional: if the redactor can't be built, nothing is prepared.
        var redactor = await BuildRedactorAsync(session, workspace, request.Client?.PrivateValues, ct).ConfigureAwait(false);

        var harnessTask = include.Environment
            ? Timed("Fleet and system", GatherHarnessesAsync, problems, ct)
            : Task.FromResult<IReadOnlyList<ReportFact>?>([]);
        var logTask = include.Log
            ? Timed("Fleet log", token => GatherLogAsync(session, token), problems, ct)
            : Task.FromResult<FleetLogExcerpt.Excerpt?>(null);
        await Task.WhenAll(harnessTask, logTask).ConfigureAwait(false);

        var facts = new ReportFacts
        {
            FleetVersion = FleetVersion(),
            AppKind = options.Desktop.Enabled ? "desktop app" : options.Cloud.Enabled ? "cloud" : "server",
            System = $"{RuntimeInformation.OSDescription.Trim()} · {RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}",
            Runtime = RuntimeInformation.FrameworkDescription,
            OsLabel = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : null,
            Harnesses = harnessTask.Result ?? [],
            Session = include.Where ? SessionFacts(session, workspace) : [],
            HarnessType = session?.HarnessType,
            Log = logTask.Result,
            Problems = problems,
        };

        if (include.Log && !logLocation.Enabled)
            problems.Add(new ReportItemProblem("Fleet log", "Fleet's log files are turned off (Fleet:DiagnosticLogging:Enabled)."));
        else if (include.Log && options.Cloud.Enabled)
            problems.Add(new ReportItemProblem("Fleet log", "Not included on a shared Fleet: its log has other people's sessions."));

        return ProblemReportComposer.Compose(
            request with { Description = Truncate(request.Description, MaxDescriptionLength) },
            facts,
            redactor,
            CanSend);
    }

    /// <summary>Sends a reviewed report to the inbox and returns its id.</summary>
    public async Task<string> SendAsync(SendReportRequest request, CancellationToken ct)
    {
        var inbox = InboxUri ?? throw new ReportSendException(
            "Sending reports is off on this Fleet (Fleet:Reports:InboxUrl is empty). Use Save as file.", 503);

        var payload = new InboxReport(
            Kind: request.Kind,
            Title: request.Title,
            Body: request.Body,
            Log: string.IsNullOrEmpty(request.Log) ? null : request.Log,
            Screenshot: string.IsNullOrEmpty(request.Screenshot) ? null : request.Screenshot,
            Contact: string.IsNullOrWhiteSpace(request.Contact) ? null : request.Contact.Trim(),
            Labels: request.Labels ?? [],
            FleetVersion: FleetVersion());

        var client = httpClientFactory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(
                new Uri(inbox, "v1/reports"), payload, ApplicationJsonContext.Default.InboxReport, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            LogInboxUnreachable(logger, ex, inbox);
            throw new ReportSendException(
                "Couldn't reach the report inbox. Check your connection and try again, or use Save as file.", 502);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Created || response.IsSuccessStatusCode)
            {
                var accepted = await ReadAsync(response, ApplicationJsonContext.Default.InboxAccepted, ct).ConfigureAwait(false);
                if (accepted?.Id is { Length: > 0 } id)
                {
                    LogSent(logger, id);
                    return id;
                }
            }

            var error = await ReadAsync(response, ApplicationJsonContext.Default.InboxError, ct).ConfigureAwait(false);
            LogInboxRefused(logger, (int)response.StatusCode, error?.Error);
            throw new ReportSendException(
                error?.Error is { Length: > 0 } message
                    ? message
                    : $"The report inbox answered {(int)response.StatusCode}. Try again later, or use Save as file.",
                (int)response.StatusCode is 400 or 413 or 429 ? (int)response.StatusCode : 502);
        }
    }

    /// <summary>The report as a zip: report.md, fleet.log and the screenshot, for sharing some other way.</summary>
    public static byte[] BuildArchive(SendReportRequest request)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var markdown = new StringBuilder()
                .Append("# ").Append(request.Title).Append("\n\n")
                .Append("> Fleet ").Append(FleetVersion()).Append(" · ").Append(request.Kind);
            if (!string.IsNullOrWhiteSpace(request.Contact)) markdown.Append(" · reply to ").Append(request.Contact.Trim());
            markdown.Append("\n\n").Append(request.Body);
            AddEntry(zip, "report.md", Encoding.UTF8.GetBytes(markdown.ToString()));

            if (!string.IsNullOrEmpty(request.Log))
                AddEntry(zip, "fleet.log", Encoding.UTF8.GetBytes(request.Log));

            if (DecodeScreenshot(request.Screenshot) is { } image)
                AddEntry(zip, image.Name, image.Bytes);
        }

        return buffer.ToArray();
    }

    private async Task<ReportRedactor> BuildRedactorAsync(
        Session? session, Workspace? workspace, IReadOnlyList<ReportPrivateValue>? clientValues, CancellationToken ct)
    {
        var redactor = new ReportRedactor();
        redactor.AddUser(Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        // The reported session's values first, so they get the lowest numbers.
        if (session is not null)
        {
            redactor.AddFolder(session.Directory);
            redactor.AddFolder(session.GitRepoRoot);
            redactor.AddSessionTitle(session.Title);
        }

        if (workspace is not null)
        {
            redactor.AddFolder(workspace.Directory);
            redactor.AddFolder(workspace.SourceDirectory);
            redactor.AddBranch(workspace.Branch);
        }

        foreach (var other in await sessions.ListAsync(5000, 0).ConfigureAwait(false))
        {
            redactor.AddFolder(other.Directory);
            redactor.AddFolder(other.GitRepoRoot);
            redactor.AddSessionTitle(other.Title);
        }

        foreach (var other in await workspaces.ListAsync().ConfigureAwait(false))
        {
            redactor.AddFolder(other.Directory);
            redactor.AddFolder(other.SourceDirectory);
            redactor.AddBranch(other.Branch);
            redactor.AddSessionTitle(other.DisplayName);
        }

        foreach (var root in await workspaceRoots.ListAsync().ConfigureAwait(false))
            redactor.AddFolder(root.Path);

        redactor.AddFolder(options.Cloud.WorkspaceRoot);
        redactor.AddMachine(Environment.MachineName);

        var identity = machine.Get();
        redactor.AddMachine(identity.Name);
        redactor.AddSecret(identity.AccessToken);
        foreach (var tokens in localTokens)
            redactor.AddSecret(tokens.Token);

        try
        {
            foreach (var credential in await credentials.GetDecryptedCredentialsAsync(user.UserId).ConfigureAwait(false))
                redactor.AddSecret(credential.EncryptedValue);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Credentials that can't be read can't leak through a report either; patterns still catch known shapes.
            LogCredentialsUnreadable(logger, ex);
        }

        if (user.Email is { Length: > 0 } email) redactor.AddSecret(email);

        foreach (var value in clientValues ?? [])
        {
            if (string.Equals(value.Kind, "url", StringComparison.OrdinalIgnoreCase)) redactor.AddUrl(value.Value);
            else redactor.AddMachine(value.Value);
        }

        ct.ThrowIfCancellationRequested();
        return redactor;
    }

    private async Task<IReadOnlyList<ReportFact>?> GatherHarnessesAsync(CancellationToken ct)
    {
        var snapshot = await harnesses.GetAsync(fresh: false, ct).ConfigureAwait(false);
        return snapshot.Harnesses
            .Select(harness => new ReportFact(
                harness.DisplayName,
                harness.Available
                    ? harness.Version is { Length: > 0 } version ? version : "installed"
                    : harness.State.Replace('-', ' ')))
            .ToList();
    }

    private async Task<FleetLogExcerpt.Excerpt?> GatherLogAsync(Session? session, CancellationToken ct)
    {
        if (!logLocation.Enabled || options.Cloud.Enabled) return null;
        var now = time.GetUtcNow().UtcDateTime;
        var entries = await FleetLogExcerpt.ReadAsync(logLocation, now - LogWindow, now, ct).ConfigureAwait(false);
        var ids = session is null
            ? []
            : new[] { session.Id, session.OpencodeSessionId, session.WorkspaceId, session.InstanceId }
                .Where(id => !string.IsNullOrEmpty(id) && id.Length >= 6)
                .ToArray();
        return FleetLogExcerpt.Select(entries, ids);
    }

    private List<ReportFact> SessionFacts(Session? session, Workspace? workspace)
    {
        if (session is null) return [];
        var facts = new List<ReportFact>
        {
            new("Session", $"{session.Id} · {session.HarnessType} · {session.RuntimeMode}"),
        };

        var tracked = activity.GetEffectiveActivityStatus(session.Id);
        var status = tracked ?? session.ActivityStatus ?? "unknown";
        var lifecycle = session.LifecycleStatus is { Length: > 0 } l ? $" · {l}" : string.Empty;
        facts.Add(new ReportFact("Status on the server", $"{status}{lifecycle}"));

        var model = string.Join(" · ", new[]
        {
            session.SelectedProviderId is { Length: > 0 } provider && session.SelectedModelId is { Length: > 0 } modelId
                ? $"{provider}/{modelId}"
                : null,
            session.SelectedAgent is { Length: > 0 } agent ? $"agent {agent}" : null,
        }.Where(part => part is not null));
        if (model.Length > 0) facts.Add(new ReportFact("Model", model));

        if (workspace is not null)
        {
            var kind = workspace.IsolationStrategy switch
            {
                "worktree" => "a worktree",
                "clone" => "a clone",
                "existing" => "the folder itself",
                var other => other,
            };
            var branch = workspace.Branch is { Length: > 0 } b ? $" · branch {b}" : string.Empty;
            facts.Add(new ReportFact("Works in", $"{kind}{branch}"));
        }

        if (session.ParentSessionId is { Length: > 0 }) facts.Add(new ReportFact("Started by", "another session"));
        if (session.RetentionStatus is "archived") facts.Add(new ReportFact("Archived", "yes"));
        return facts;
    }

    private async Task<T?> Timed<T>(
        string item, Func<CancellationToken, Task<T?>> gather, List<ReportItemProblem> problems, CancellationToken ct)
        where T : class
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(PartTimeLimit);
        try
        {
            return await gather(limit.Token).WaitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            lock (problems) problems.Add(new ReportItemProblem(item, "Took longer than 2 seconds, so it was left out."));
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPartFailed(logger, ex, item);
            lock (problems) problems.Add(new ReportItemProblem(item, $"Couldn't collect it: {ex.Message}"));
            return null;
        }
    }

    private static string FleetVersion()
    {
        var version = FleetInstrumentation.ServiceVersion.Split('+')[0];
        var commit = FleetInstrumentation.ServiceCommit;
        return string.IsNullOrEmpty(commit) || commit == "unknown" ? version : $"{version} ({commit[..Math.Min(8, commit.Length)]})";
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static async Task<T?> ReadAsync<T>(
        HttpResponseMessage response, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken ct)
        where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(type, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    internal static (string Name, byte[] Bytes)? DecodeScreenshot(string? screenshot)
    {
        if (string.IsNullOrEmpty(screenshot)) return null;
        var comma = screenshot.StartsWith("data:", StringComparison.Ordinal) ? screenshot.IndexOf(',') : -1;
        try
        {
            var bytes = Convert.FromBase64String(comma >= 0 ? screenshot[(comma + 1)..] : screenshot);
            var jpeg = bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8;
            return (jpeg ? "screenshot.jpg" : "screenshot.png", bytes);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static void AddEntry(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't reach the report inbox at {Inbox}")]
    private static partial void LogInboxUnreachable(ILogger logger, Exception exception, Uri inbox);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent problem report {ReportId}")]
    private static partial void LogSent(ILogger logger, string reportId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The report inbox answered {Status}: {Error}")]
    private static partial void LogInboxRefused(ILogger logger, int status, string? error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't read saved credentials to keep them out of a problem report")]
    private static partial void LogCredentialsUnreadable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't gather {Item} for a problem report")]
    private static partial void LogPartFailed(ILogger logger, Exception exception, string item);
}
