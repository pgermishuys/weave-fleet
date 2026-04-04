using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

public sealed class PortAllocatorTests
{
    [Fact]
    public void AllocatePort_ReturnsPortInRange()
    {
        var allocator = new PortAllocator(10000, 10009);

        int port = allocator.AllocatePort();

        Assert.InRange(port, 10000, 10009);
    }

    [Fact]
    public void AllocatePort_NeverReturnsSamePortTwice()
    {
        var allocator = new PortAllocator(10000, 10004); // 5 ports
        var allocated = new HashSet<int>();

        for (int i = 0; i < 5; i++)
        {
            bool added = allocated.Add(allocator.AllocatePort());
            Assert.True(added, "Duplicate port was allocated.");
        }
    }

    [Fact]
    public void AllocatePort_ThrowsWhenExhausted()
    {
        var allocator = new PortAllocator(20000, 20002); // 3 ports

        allocator.AllocatePort();
        allocator.AllocatePort();
        allocator.AllocatePort();

        var ex = Assert.Throws<InvalidOperationException>(() => allocator.AllocatePort());
        Assert.Contains("Port range exhausted", ex.Message);
        Assert.Contains("20000", ex.Message);
        Assert.Contains("20002", ex.Message);
    }

    [Fact]
    public void ReleasePort_AllowsReallocation()
    {
        var allocator = new PortAllocator(30000, 30000); // single port

        int first = allocator.AllocatePort();
        Assert.Equal(30000, first);

        allocator.ReleasePort(first);

        int second = allocator.AllocatePort();
        Assert.Equal(30000, second);
    }

    [Fact]
    public void ReleasePort_UnknownPort_DoesNotThrow()
    {
        // ReleasePort on a port that was never allocated must not throw
        var allocator = new PortAllocator(40000, 40009);

        var ex = Record.Exception(() => allocator.ReleasePort(40005));
        Assert.Null(ex);
    }

    [Fact]
    public void AllocatedCount_TracksCorrectly()
    {
        var allocator = new PortAllocator(50000, 50009);

        Assert.Equal(0, allocator.AllocatedCount);

        int p1 = allocator.AllocatePort();
        Assert.Equal(1, allocator.AllocatedCount);

        int p2 = allocator.AllocatePort();
        Assert.Equal(2, allocator.AllocatedCount);

        allocator.ReleasePort(p1);
        Assert.Equal(1, allocator.AllocatedCount);

        allocator.ReleasePort(p2);
        Assert.Equal(0, allocator.AllocatedCount);
    }

    [Fact]
    public void AvailableCount_TracksCorrectly()
    {
        var allocator = new PortAllocator(60000, 60004); // 5 ports

        Assert.Equal(5, allocator.AvailableCount);

        int p = allocator.AllocatePort();
        Assert.Equal(4, allocator.AvailableCount);

        allocator.ReleasePort(p);
        Assert.Equal(5, allocator.AvailableCount);
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

        Assert.Equal(portCount, results.Count);
        Assert.Equal(portCount, results.Distinct().Count()); // no duplicates
    }
}
