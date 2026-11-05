using System.Net;
using System.Net.Sockets;

namespace WeaveFleet.Application.Machines;

/// <summary>One thing Fleet checks on the way to a machine. <see cref="State"/> is one of <see cref="MachineStepStates"/>.</summary>
public sealed record MachineStep(string Label, string State, string? Detail = null);

public static class MachineStepStates
{
    public const string Ok = "ok";
    public const string Failed = "failed";
    public const string NotChecked = "notChecked";
}

/// <summary>
/// Which step of reaching a machine broke, in words the owner can act on. <see cref="Code"/> is stable (the docs have a
/// heading for each <see cref="Headline"/>); <see cref="Steps"/> are the checks in order, the ones before the break ticked
/// and the ones after it not checked; <see cref="Try"/> says what to do next.
/// </summary>
public sealed record MachineDiagnosis(string Code, string Headline, IReadOnlyList<MachineStep> Steps, string Try);

/// <summary>
/// What went wrong when Fleet asked another machine something. <see cref="Message"/> is the one-line text older clients
/// show; <see cref="Diagnosis"/> is there when the failure is one of the steps. A plain string converts to a problem with
/// no diagnosis.
/// </summary>
public sealed record MachineProblem(string Message, MachineDiagnosis? Diagnosis = null)
{
    public static implicit operator MachineProblem(string message) => new(message);

    public static implicit operator string(MachineProblem problem) => problem.Message;
}

/// <summary>
/// The one place that turns a failed request to a machine into a <see cref="MachineProblem"/>: adding, pairing and the
/// card's check all ask it, so they name the same step. It reads the failure, it sends nothing.
/// </summary>
public static class MachineDiagnoses
{
    public const string NameNotFound = "name-not-found";
    public const string NotAnswering = "not-answering";
    public const string FleetNotRunning = "fleet-not-running";
    public const string CantConnect = "cant-connect";
    public const string NotFleet = "not-fleet";
    public const string Redirected = "redirected";
    public const string TooOld = "too-old";
    public const string VersionMismatch = "version-mismatch";
    public const string UnsupportedSignIn = "unsupported-sign-in";
    public const string KeyRejected = "key-rejected";
    public const string CodeExpired = "code-expired";
    public const string TooManyCodes = "too-many-codes";

    private static readonly string[] StepLabels =
    [
        "Name found",
        "Machine answers",
        "Port answers",
        "It's Fleet",
        "Version fits",
        "Key accepted",
    ];

    /// <summary>The problem for a request that never got an answer.</summary>
    public static MachineProblem FromException(Exception exception, string baseUrl, string? name = null)
    {
        var host = name ?? HostOf(baseUrl);
        var socket = exception.InnerException as SocketException;
        if ((exception as HttpRequestException)?.HttpRequestError == HttpRequestError.NameResolutionError
            || socket?.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData)
            return Problem(0, NameNotFound, $"Can't find {host}", "Nothing on this network knows that name.",
                "Check the address. A tailnet name looks like falcon.tail9c2e.ts.net.", Unreached(baseUrl));
        if (socket?.SocketErrorCode == SocketError.ConnectionRefused)
            return Problem(2, FleetNotRunning, $"Fleet isn't running on {host}", "The connection was refused, so the machine is up and Fleet is not.",
                $"On {host}, run fleet node up. It starts Fleet in the background.", Unreached(baseUrl));
        if ((exception as HttpRequestException)?.HttpRequestError == HttpRequestError.SecureConnectionError)
            return Problem(2, CantConnect, $"Can't open a secure connection to {host}", "The certificate or the secure handshake failed.",
                "Use an http address inside your tailnet, or the machine's tailscale serve address.", Unreached(baseUrl));
        if (exception is TaskCanceledException || socket?.SocketErrorCode is SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable)
            return Problem(1, NotAnswering, $"{host} isn't answering", "Nothing came back in 5 seconds.",
                $"Check that {host} is on, on the same network or tailnet as this machine, and not behind a firewall.", Unreached(baseUrl));
        return Problem(1, CantConnect, $"Can't connect to {host}", exception.Message,
            $"Check the address and that {host} is on this machine's network.", Unreached(baseUrl));
    }

    /// <summary>The problem for a machine that answered with <paramref name="status"/> to a request for who it is.</summary>
    public static MachineProblem FromStatus(HttpStatusCode status, string baseUrl, string? name = null)
    {
        var host = name ?? HostOf(baseUrl);
        var code = (int)status;
        return status switch
        {
            HttpStatusCode.Unauthorized => Problem(5, KeyRejected, $"{host} didn't accept the key", "It turned the access token away.",
                $"On {host}, run fleet node pair for a new code, or paste its current token.", "That token wasn't accepted."),
            HttpStatusCode.NotFound => Problem(4, TooOld, $"Fleet on {host} is too old", "It doesn't know the machine contract.",
                $"Update Fleet on {host}, then try again.", "That Fleet is too old to add as a machine. Update it first."),
            _ when code is >= 300 and < 400 => Problem(3, Redirected, $"{host} sends you elsewhere", $"It answered {code} with another address.",
                "Use the address it redirects to.", $"{baseUrl} redirects elsewhere. Use the address it redirects to."),
            _ => Problem(3, NotFleet, $"{host} answered {code}", "That isn't how Fleet answers.",
                "Check the port. Fleet's default is 2113.", $"The machine answered {code}."),
        };
    }

    /// <summary>An answer that isn't Fleet's: not JSON, or without an id.</summary>
    public static MachineProblem NotAFleet(string baseUrl, string? name = null)
    {
        var host = name ?? HostOf(baseUrl);
        return Problem(3, NotFleet, $"{host} isn't Fleet", "Something answered, but not with what Fleet says about itself.",
            "Check the port. Fleet's default is 2113.", "That isn't a Fleet.");
    }

    public static MachineProblem VersionMismatchOf(int theirs, int ours, string baseUrl, string? name = null)
    {
        var host = name ?? HostOf(baseUrl);
        return Problem(4, VersionMismatch, $"Fleet on {host} speaks another version", $"It speaks machine contract {theirs}; this one speaks {ours}.",
            "Update the older Fleet, then try again.", $"That Fleet speaks machine contract {theirs}; this one speaks {ours}. Update the older one.");
    }

    public static MachineProblem SignInUnsupported(string baseUrl, string? name = null)
    {
        var host = name ?? HostOf(baseUrl);
        return Problem(4, UnsupportedSignIn, $"{host} signs in another way", "It uses an identity provider, which machines don't support yet.",
            "Run fleet node on a machine that uses an access token.", "That Fleet signs people in with an identity provider, which machines don't support yet.");
    }

    /// <summary>The saved token can't be read any more (this Fleet's keys changed), so it can't be sent.</summary>
    public static MachineProblem KeyUnreadable(string baseUrl, string? name = null) =>
        Problem(5, KeyRejected, "This Fleet can't read the saved key", "It was saved with keys this Fleet no longer has.",
            $"Paste {name ?? HostOf(baseUrl)}'s token again, or run fleet node pair on it for a new code.", "The saved token can't be read. Give the machine's token again.");

    /// <summary>The pairing code was turned away: unknown, expired or already used.</summary>
    public static MachineProblem CodeExpiredOn(string baseUrl, string? name = null)
    {
        var host = name ?? HostOf(baseUrl);
        return Problem(5, CodeExpired, "That code has expired", "Codes work once and only for a few minutes.",
            $"On {host}, run fleet node pair for a new one.", "That code has expired or was already used. Run fleet node pair on the other machine for a new one.");
    }

    public static MachineProblem TooManyCodesOn() =>
        Problem(5, TooManyCodes, "Too many codes tried", "That machine counts wrong codes and pauses for a minute.",
            "Wait a minute, then try the code again.", "Too many codes tried on that machine. Wait a minute and try again.");

    private static MachineProblem Problem(int failedStep, string code, string headline, string detail, string tryText, string message)
    {
        var steps = StepLabels.Select((label, index) => index < failedStep
            ? new MachineStep(label, MachineStepStates.Ok)
            : index == failedStep
                ? new MachineStep(label, MachineStepStates.Failed, detail)
                : new MachineStep(label, MachineStepStates.NotChecked)).ToList();
        return new MachineProblem(message, new MachineDiagnosis(code, headline, steps, tryText));
    }

    private static string Unreached(string baseUrl) =>
        $"Couldn't reach {baseUrl}. Check the address, that Fleet is running there, and that this machine can reach it.";

    private static string HostOf(string baseUrl) => Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : baseUrl;
}
