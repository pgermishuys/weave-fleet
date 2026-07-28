using System.Diagnostics.Metrics;
using Shouldly;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

public sealed class PortAllocatorTests
{
    [Fact]
    public void allocate_port_returns_port_in_range()
    {
        var allocator = new PortAllocator(10000, 10009);

        int port = allocator.AllocatePort();

        port.ShouldBeInRange(10000, 10009);
    }

    [Fact]
    public void allocate_port_never_returns_same_port_twice()
    {
        var allocator = new PortAllocator(10000, 10004); // 5 ports
        var allocated = new HashSet<int>();

        for (int i = 0; i < 5; i++)
        {
            bool added = allocated.Add(allocator.AllocatePort());
            added.ShouldBeTrue("Duplicate port was allocated.");
        }
    }

    [Fact]
    public void allocate_port_throws_when_exhausted()
    {
        var allocator = new PortAllocator(20000, 20002); // 3 ports

        allocator.AllocatePort();
        allocator.AllocatePort();
        allocator.AllocatePort();

        var ex = Should.Throw<InvalidOperationException>(() => allocator.AllocatePort());
        ex.Message.ShouldContain("Port range exhausted");
        ex.Message.ShouldContain("20000");
        ex.Message.ShouldContain("20002");
    }

    [Fact]
    public void release_port_allows_reallocation()
    {
        var allocator = new PortAllocator(30000, 30000); // single port

        int first = allocator.AllocatePort();
        first.ShouldBe(30000);

        allocator.ReleasePort(first);

        int second = allocator.AllocatePort();
        second.ShouldBe(30000);
    }

    [Fact]
    public void release_port_unknown_port_does_not_throw()
    {
        // ReleasePort on a port that was never allocated must not throw
        var allocator = new PortAllocator(40000, 40009);

        Should.NotThrow(() => allocator.ReleasePort(40005));
    }

    [Fact]
    public void allocated_count_tracks_correctly()
    {
        var allocator = new PortAllocator(50000, 50009);

        allocator.AllocatedCount.ShouldBe(0);

        int p1 = allocator.AllocatePort();
        allocator.AllocatedCount.ShouldBe(1);

        int p2 = allocator.AllocatePort();
        allocator.AllocatedCount.ShouldBe(2);

        allocator.ReleasePort(p1);
        allocator.AllocatedCount.ShouldBe(1);

        allocator.ReleasePort(p2);
        allocator.AllocatedCount.ShouldBe(0);
    }

    [Fact]
    public void available_count_tracks_correctly()
    {
        var allocator = new PortAllocator(60000, 60004); // 5 ports

        allocator.AvailableCount.ShouldBe(5);

        int p = allocator.AllocatePort();
        allocator.AvailableCount.ShouldBe(4);

        allocator.ReleasePort(p);
        allocator.AvailableCount.ShouldBe(5);
    }

    [Fact]
    public async Task concurrent_allocation_no_collisions()
    {
        const int portCount = 50;
        var allocator = new PortAllocator(70000, 70000 + portCount - 1);
        var results = new System.Collections.Concurrent.ConcurrentBag<int>();

        await Task.WhenAll(Enumerable.Range(0, portCount).Select(_ => Task.Run(() =>
        {
            results.Add(allocator.AllocatePort());
        })));

        results.Count.ShouldBe(portCount);
        results.Distinct().Count().ShouldBe(portCount); // no duplicates
    }

    [Fact]
    public void utilization_ratio_tracks_allocated_port_fraction()
    {
        var allocator = new PortAllocator(71000, 71003);

        allocator.Capacity.ShouldBe(4);
        allocator.UtilizationRatio.ShouldBe(0);

        int first = allocator.AllocatePort();
        allocator.AllocatePort();

        allocator.UtilizationRatio.ShouldBe(0.5);

        allocator.ReleasePort(first);
        allocator.UtilizationRatio.ShouldBe(0.25);
    }

    [Fact]
    public void utilization_metric_reports_allocated_port_fraction()
    {
        using var listener = new TestMeterListener();
        listener.Start();
        var allocator = new PortAllocator(72000, 72003);

        allocator.AllocatePort();
        allocator.AllocatePort();

        listener.RecordObservableInstruments();

        listener.GetObservableValues().ShouldContain(0.5);
    }

    private sealed class TestMeterListener : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<double> _observableValues = [];

        public TestMeterListener()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == FleetInstrumentation.ServiceName
                    && instrument.Name == "opencode_port_pool_utilization")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<double>((_, measurement, _, _) =>
            {
                _observableValues.Add(measurement);
            });
        }

        public void Start()
        {
            _listener.Start();
        }

        public void RecordObservableInstruments()
        {
            _observableValues.Clear();
            _listener.RecordObservableInstruments();
        }

        public List<double> GetObservableValues()
        {
            return _observableValues;
        }

        public void Dispose()
        {
            _listener.Dispose();
        }
    }
}
