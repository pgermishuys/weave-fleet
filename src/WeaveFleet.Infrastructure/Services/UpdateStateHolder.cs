using WeaveFleet.Application.Services;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// Thread-safe singleton that holds the current update state and raises a change event
/// when the state transitions.
/// </summary>
public sealed class UpdateStateHolder
{
    private readonly Lock _lock = new();
    private UpdateState _state = UpdateState.Initial;

    /// <summary>Raised whenever the update state changes.</summary>
    public event Action<UpdateState>? StateChanged;

    /// <summary>Current update state snapshot.</summary>
    public UpdateState State
    {
        get
        {
            lock (_lock)
                return _state;
        }
    }

    /// <summary>Atomically replaces the state and fires <see cref="StateChanged"/>.</summary>
    public void SetState(UpdateState newState)
    {
        lock (_lock)
            _state = newState;

        StateChanged?.Invoke(newState);
    }
}
