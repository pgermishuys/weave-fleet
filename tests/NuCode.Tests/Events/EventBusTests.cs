using NuCode.Events;

namespace NuCode;

public sealed class EventBusTests
{
    private static readonly NuCodeEventDefinition<TestPayload> TestEvent = new("test.event");
    private static readonly NuCodeEventDefinition<OtherPayload> OtherEvent = new("other.event");

    private sealed record TestPayload(string Value);

    private sealed record OtherPayload(int Count);

    [Fact]
    public void PublishNotifiesTypedSubscriber()
    {
        var bus = new NuCodeEventBus();
        NuCodeEvent<TestPayload>? received = null;
        bus.Subscribe(TestEvent, e => received = e);

        bus.Publish(TestEvent, new TestPayload("hello"));

        received.ShouldNotBeNull();
        received.Type.ShouldBe("test.event");
        received.Properties.Value.ShouldBe("hello");
    }

    [Fact]
    public void PublishNotifiesWildcardSubscriber()
    {
        var bus = new NuCodeEventBus();
        NuCodeEvent? received = null;
        bus.SubscribeAll(e => received = e);

        bus.Publish(TestEvent, new TestPayload("wild"));

        received.ShouldNotBeNull();
        received.Type.ShouldBe("test.event");
    }

    [Fact]
    public void TypedSubscriberDoesNotReceiveUnrelatedEvents()
    {
        var bus = new NuCodeEventBus();
        var received = new List<NuCodeEvent<TestPayload>>();
        bus.Subscribe(TestEvent, e => received.Add(e));

        bus.Publish(OtherEvent, new OtherPayload(42));

        received.ShouldBeEmpty();
    }

    [Fact]
    public void WildcardSubscriberReceivesAllEventTypes()
    {
        var bus = new NuCodeEventBus();
        var received = new List<NuCodeEvent>();
        bus.SubscribeAll(e => received.Add(e));

        bus.Publish(TestEvent, new TestPayload("a"));
        bus.Publish(OtherEvent, new OtherPayload(1));

        received.Count.ShouldBe(2);
        received[0].Type.ShouldBe("test.event");
        received[1].Type.ShouldBe("other.event");
    }

    [Fact]
    public void MultipleSubscribersAllReceiveEvent()
    {
        var bus = new NuCodeEventBus();
        var count = 0;
        bus.Subscribe(TestEvent, _ => Interlocked.Increment(ref count));
        bus.Subscribe(TestEvent, _ => Interlocked.Increment(ref count));
        bus.Subscribe(TestEvent, _ => Interlocked.Increment(ref count));

        bus.Publish(TestEvent, new TestPayload("multi"));

        count.ShouldBe(3);
    }

    [Fact]
    public void DisposeUnsubscribesTypedSubscriber()
    {
        var bus = new NuCodeEventBus();
        var count = 0;
        var sub = bus.Subscribe(TestEvent, _ => count++);

        bus.Publish(TestEvent, new TestPayload("before"));
        count.ShouldBe(1);

        sub.Dispose();

        bus.Publish(TestEvent, new TestPayload("after"));
        count.ShouldBe(1);
    }

    [Fact]
    public void DisposeUnsubscribesWildcardSubscriber()
    {
        var bus = new NuCodeEventBus();
        var count = 0;
        var sub = bus.SubscribeAll(_ => count++);

        bus.Publish(TestEvent, new TestPayload("before"));
        count.ShouldBe(1);

        sub.Dispose();

        bus.Publish(TestEvent, new TestPayload("after"));
        count.ShouldBe(1);
    }

    [Fact]
    public void DoubleDisposeIsIdempotent()
    {
        var bus = new NuCodeEventBus();
        var count = 0;
        var sub = bus.Subscribe(TestEvent, _ => count++);

        sub.Dispose();
        sub.Dispose(); // Should not throw

        bus.Publish(TestEvent, new TestPayload("after"));
        count.ShouldBe(0);
    }

    [Fact]
    public void PublishWithNoSubscribersDoesNotThrow()
    {
        var bus = new NuCodeEventBus();

        Should.NotThrow(() => bus.Publish(TestEvent, new TestPayload("orphan")));
    }

    [Fact]
    public void EventDefinitionCreatesCorrectEvent()
    {
        var evt = TestEvent.Create(new TestPayload("def"));

        evt.Type.ShouldBe("test.event");
        evt.Properties.Value.ShouldBe("def");
    }

    [Fact]
    public void TypedAndWildcardSubscribersBothFire()
    {
        var bus = new NuCodeEventBus();
        var typedReceived = false;
        var wildcardReceived = false;
        bus.Subscribe(TestEvent, _ => typedReceived = true);
        bus.SubscribeAll(_ => wildcardReceived = true);

        bus.Publish(TestEvent, new TestPayload("both"));

        typedReceived.ShouldBeTrue();
        wildcardReceived.ShouldBeTrue();
    }

    [Fact]
    public async Task ConcurrentPublishAndSubscribeDoNotThrow()
    {
        var bus = new NuCodeEventBus();
        var count = 0;
        var barrier = new ManualResetEventSlim(false);

        // Spin up subscribers and publishers concurrently
        var tasks = new List<Task>();
        for (var i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                barrier.Wait();
                var sub = bus.Subscribe(TestEvent, _ => Interlocked.Increment(ref count));
                bus.Publish(TestEvent, new TestPayload("concurrent"));
                sub.Dispose();
            }));
        }

        barrier.Set();
        await Task.WhenAll(tasks);

        // Just verify no exceptions were thrown; count is non-deterministic
        (count >= 0).ShouldBeTrue();
    }
}

public sealed class GlobalEventBusTests
{
    private static readonly NuCodeEventDefinition<string> TestDef = new("global.test");

    [Fact]
    public void GlobalEventBusDelegatesPublishAndSubscribe()
    {
        var global = new GlobalEventBus();
        string? received = null;
        global.Subscribe(TestDef, e => received = e.Properties);

        global.Publish(TestDef, "hello-global");

        received.ShouldBe("hello-global");
    }

    [Fact]
    public void GlobalEventBusSubscribeAllWorks()
    {
        var global = new GlobalEventBus();
        NuCodeEvent? received = null;
        global.SubscribeAll(e => received = e);

        global.Publish(TestDef, "wildcard");

        received.ShouldNotBeNull();
        received.Type.ShouldBe("global.test");
    }
}
