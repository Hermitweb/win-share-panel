using WinShare.App.Events;

namespace WinShare.App.Tests;

/// <summary>事件总线：发布/订阅/取消/异常隔离。</summary>
public class AppEventBusTests
{
    record TestEvent(int Value);
    record OtherEvent(string S);

    [Fact]
    public void Publish_DeliversToAllSubscribers()
    {
        var bus = new AppEventBus();
        var got = new List<int>();
        bus.Subscribe<TestEvent>(e => got.Add(e.Value));
        bus.Subscribe<TestEvent>(e => got.Add(e.Value * 10));

        bus.Publish(new TestEvent(5));

        Assert.Equal(new[] { 5, 50 }, got);
    }

    [Fact]
    public void Unsubscribe_StopsDelivery()
    {
        var bus = new AppEventBus();
        var got = 0;
        var sub = bus.Subscribe<TestEvent>(_ => got++);
        bus.Publish(new TestEvent(1));
        sub.Unsubscribe();
        bus.Publish(new TestEvent(2));

        Assert.Equal(1, got);
        Assert.Equal(0, bus.Subscribers<TestEvent>());
    }

    [Fact]
    public void Unsubscribe_IsIdempotent()
    {
        var bus = new AppEventBus();
        var sub = bus.Subscribe<TestEvent>(_ => { });
        sub.Unsubscribe();
        sub.Unsubscribe(); // 二次取消不抛
        Assert.Equal(0, bus.Subscribers<TestEvent>());
    }

    [Fact]
    public void HandlerException_DoesNotBreakOtherSubscribers()
    {
        var bus = new AppEventBus();
        var got = 0;
        bus.Subscribe<TestEvent>(_ => throw new InvalidOperationException("boom"));
        bus.Subscribe<TestEvent>(_ => got++);

        bus.Publish(new TestEvent(1)); // 不应抛出

        Assert.Equal(1, got);
    }

    [Fact]
    public void DifferentEventTypes_DoNotInterfere()
    {
        var bus = new AppEventBus();
        var got = 0;
        bus.Subscribe<TestEvent>(_ => got++);
        bus.Publish(new OtherEvent("x"));
        Assert.Equal(0, got);
        bus.Publish(new TestEvent(1));
        Assert.Equal(1, got);
    }

    [Fact]
    public void AlertRaisedEvent_CarriesFields()
    {
        var bus = new AppEventBus();
        AlertRaised? received = null;
        bus.Subscribe<AlertRaised>(e => received = e);
        bus.Publish(new AlertRaised { kind = "smb1", level = "danger", text = "SMB1 启用" });

        Assert.NotNull(received);
        Assert.Equal("smb1", received!.kind);
        Assert.Equal("danger", received.level);
    }
}
