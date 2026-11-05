namespace WeaveFleet.Application.Machines;

/// <summary>One step of setting a machine up. <see cref="State"/> is one of <see cref="SetupStepStates"/>.</summary>
public sealed record SetupStep(string Key, string Label, string State, string? Detail = null);

public static class SetupStepStates
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Done = "done";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}

/// <summary>The steps of setting a machine up, in order.</summary>
public static class SetupSteps
{
    public const string Login = "login";
    public const string Install = "install";
    public const string Start = "start";
    public const string Pair = "pair";
}

/// <summary>
/// Where a set-up has got to: the four steps, each pending, running, done, skipped or failed. The set-up writes it as it
/// goes, and <see cref="Steps"/> is a snapshot anyone can read meanwhile.
/// </summary>
public sealed class SetupProgress
{
    private readonly object _gate = new();
    private SetupStep[] _steps =
    [
        new(SetupSteps.Login, "Log in over SSH", SetupStepStates.Pending),
        new(SetupSteps.Install, "Install Fleet", SetupStepStates.Pending),
        new(SetupSteps.Start, "Start it in the background", SetupStepStates.Pending),
        new(SetupSteps.Pair, "Pair and add the machine", SetupStepStates.Pending),
    ];

    public IReadOnlyList<SetupStep> Steps
    {
        get
        {
            lock (_gate)
                return _steps;
        }
    }

    public void Begin(string key) => Set(key, SetupStepStates.Running, null);

    public void Finish(string key, string detail) => Set(key, SetupStepStates.Done, detail);

    public void Skip(string key, string detail) => Set(key, SetupStepStates.Skipped, detail);

    public void Fail(string key, string detail) => Set(key, SetupStepStates.Failed, detail);

    private void Set(string key, string state, string? detail)
    {
        lock (_gate)
            _steps = _steps.Select(step => step.Key == key ? step with { State = state, Detail = detail } : step).ToArray();
    }
}
