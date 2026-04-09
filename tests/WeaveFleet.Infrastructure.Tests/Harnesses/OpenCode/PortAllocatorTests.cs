using Shouldly;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

public sealed class PortAllocatorTests
{
    [Fact]
    public void AllocatePort_ReturnsPortInRange()
    {
        var allocator = new PortAllocator(10000, 10009);

        int port = allocator.AllocatePort();

        port.ShouldBeInRange(10000, 10009);
    }

    [Fact]
    public void AllocatePort_NeverReturnsSamePortTwice()
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
    public void AllocatePort_ThrowsWhenExhausted()
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
    public void ReleasePort_AllowsReallocation()
    {
        var allocator = new PortAllocator(30000, 30000); // single port

        int first = allocator.AllocatePort();
        first.ShouldBe(30000);

        allocator.ReleasePort(first);

        int second = allocator.AllocatePort();
        second.ShouldBe(30000);
    }

    [Fact]
    public void ReleasePort_UnknownPort_DoesNotThrow()
    {
        // ReleasePort on a port that was never allocated must not throw
        var allocator = new PortAllocator(40000, 40009);

        Should.NotThrow(() => allocator.ReleasePort(40005));
    }

    [Fact]
    public void AllocatedCount_TracksCorrectly()
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
    public void AvailableCount_TracksCorrectly()
    {
        var allocator = new PortAllocator(60000, 60004); // 5 ports

        allocator.AvailableCount.ShouldBe(5);

        int p = allocator.AllocatePort();
        allocator.AvailableCount.ShouldBe(4);

        allocator.ReleasePort(p);
        allocator.AvailableCount.ShouldBe(5);
    }

    [Fact]
    public async Task ConcurrentAllocation_NoCollisions()
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
}
