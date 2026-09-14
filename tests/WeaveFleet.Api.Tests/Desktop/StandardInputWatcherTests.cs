using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Api.Desktop;

namespace WeaveFleet.Api.Tests.Desktop;

public sealed class StandardInputWatcherTests
{
    [Fact]
    public async Task Fleet_stops_when_the_app_closes_its_input()
    {
        using var input = new AppPipe();
        var lifetime = new FakeLifetime();
        using var watcher = new StandardInputWatcher(() => input, lifetime, NullLogger<StandardInputWatcher>.Instance);
        await watcher.StartAsync(CancellationToken.None);

        lifetime.StopRequested.ShouldBeFalse();
        input.CloseFromApp();
        await watcher.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));

        lifetime.StopRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task A_broken_pipe_stops_fleet_too()
    {
        using var input = new AppPipe();
        var lifetime = new FakeLifetime();
        using var watcher = new StandardInputWatcher(() => input, lifetime, NullLogger<StandardInputWatcher>.Instance);
        await watcher.StartAsync(CancellationToken.None);

        input.Break();
        await watcher.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));

        lifetime.StopRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task Bytes_on_the_input_do_not_stop_fleet()
    {
        using var input = new AppPipe();
        var lifetime = new FakeLifetime();
        using var watcher = new StandardInputWatcher(() => input, lifetime, NullLogger<StandardInputWatcher>.Instance);
        await watcher.StartAsync(CancellationToken.None);

        input.Send("hello\n"u8.ToArray());
        await input.WaitUntilConsumedAsync().WaitAsync(TimeSpan.FromSeconds(10));

        lifetime.StopRequested.ShouldBeFalse();
        await watcher.StopAsync(CancellationToken.None);
        input.CloseFromApp();
    }

    [Fact]
    public async Task Shutting_down_does_not_wait_for_the_input_to_close()
    {
        using var input = new AppPipe();
        var lifetime = new FakeLifetime();
        using var watcher = new StandardInputWatcher(() => input, lifetime, NullLogger<StandardInputWatcher>.Instance);
        await watcher.StartAsync(CancellationToken.None);

        await watcher.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        lifetime.StopRequested.ShouldBeFalse();
        input.CloseFromApp();
    }

    /// <summary>Fleet's end of the app's pipe: reads block until the app sends bytes, closes, or breaks it.</summary>
    private sealed class AppPipe : Stream
    {
        private readonly Queue<byte[]?> _chunks = new();
        private readonly SemaphoreSlim _available = new(0);
        private readonly TaskCompletionSource _consumed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Send(byte[] bytes) => Add(bytes);
        public void CloseFromApp() => Add([]);
        public void Break() => Add(null);
        public Task WaitUntilConsumedAsync() => _consumed.Task;

        public override int Read(byte[] buffer, int offset, int count)
        {
            _available.Wait();
            byte[]? next;
            lock (_chunks)
                next = _chunks.Dequeue();
            var chunk = next ?? throw new IOException("Broken pipe");
            chunk.CopyTo(buffer, offset);
            if (chunk.Length > 0)
                _consumed.TrySetResult();
            return chunk.Length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _available.Dispose();
            base.Dispose(disposing);
        }

        private void Add(byte[]? chunk)
        {
            lock (_chunks)
                _chunks.Enqueue(chunk);
            _available.Release();
        }
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private volatile bool _stopRequested;

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public bool StopRequested => _stopRequested;
        public void StopApplication() => _stopRequested = true;
    }
}
