using Porta.Pty;
using WeaveFleet.Application.Terminals;

namespace WeaveFleet.Infrastructure.Terminals;

internal sealed class PortaPtyProcess : IPtyProcess
{
    private readonly IPtyConnection _connection;
    private readonly TaskCompletionSource<PtyExit> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile bool _killed;
    private int _disposed;

    public PortaPtyProcess(IPtyConnection connection)
    {
        _connection = connection;
        _connection.ProcessExited += OnProcessExited;
    }

    public int Pid => _connection.Pid;

    public Task<PtyExit> Exited => _exited.Task;

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        try
        {
            return await _connection.ReaderStream.ReadAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Linux reports EIO once the shell's side of the terminal has closed.
            return 0;
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_exited.Task.IsCompleted || Volatile.Read(ref _disposed) != 0)
            return;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _connection.WriterStream.WriteAsync(data, ct).ConfigureAwait(false);
            await _connection.WriterStream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The process ended between the check and the write.
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Resize(int cols, int rows)
    {
        if (_exited.Task.IsCompleted || Volatile.Read(ref _disposed) != 0)
            return;

        try
        {
            _connection.Resize(Math.Max(cols, 1), Math.Max(rows, 1));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Resizing a terminal that just closed isn't worth reporting.
        }
    }

    public void Kill()
    {
        if (_exited.Task.IsCompleted)
            return;

        _killed = true;
        try
        {
            _connection.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Kill();
        // Give the exit event a moment so Exited reports how the process ended.
        await Task.WhenAny(_exited.Task, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        _connection.ProcessExited -= OnProcessExited;
        _connection.Dispose();
        _exited.TrySetResult(new PtyExit(null, Killed: true));
        _writeLock.Dispose();
    }

    private void OnProcessExited(object? sender, PtyExitedEventArgs e)
    {
        // After Kill, Porta.Pty reports exit code 0 (Task 0 findings), so a kill never reports a code.
        _exited.TrySetResult(_killed ? new PtyExit(null, Killed: true) : new PtyExit(e.ExitCode, Killed: false));
    }
}
