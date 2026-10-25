using ControlWinSwitch.App;

namespace ControlWinSwitch.Tests;

public class TimerServiceTests
{
    private readonly List<(nuint Id, uint Ms)> _started = [];
    private readonly List<nuint> _killed = [];

    private TimerService NewService() => new((id, ms) => _started.Add((id, ms)), id => _killed.Add(id));

    [Fact]
    public void Each_timer_gets_its_own_id_and_callback()
    {
        var timers = NewService();
        int a = 0, b = 0;
        var first = timers.Create(() => a++);
        var second = timers.Create(() => b++);
        first.Start(100);
        second.Start(200);
        Assert.Equal([((nuint)1, 100u), ((nuint)2, 200u)], _started);

        timers.Dispatch(2);
        Assert.Equal((0, 1), (a, b));
    }

    [Fact]
    public void A_stopped_timer_does_not_run_and_a_late_tick_kills_it_again()
    {
        var timers = NewService();
        int ticks = 0;
        var timer = timers.Create(() => ticks++);
        timer.Start(50);
        timer.Stop();
        Assert.False(timer.Running);

        timers.Dispatch(1);
        Assert.Equal(0, ticks);
        Assert.Equal([(nuint)1, (nuint)1], _killed);
    }

    [Fact]
    public void Unknown_ids_are_ignored()
    {
        var timers = NewService();
        timers.Dispatch(0);
        timers.Dispatch(7);
        Assert.Empty(_killed);
    }
}
