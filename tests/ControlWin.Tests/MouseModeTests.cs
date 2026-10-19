using ControlWin.App;
using ControlWin.Input;

namespace ControlWin.Tests;

public class MouseModeTests
{
    private sealed class FakeOutput : IMouseOutput
    {
        public List<string> Events { get; } = [];
        public int MoveX, MoveY, WheelTotal, HWheelTotal;
        public bool TimerRunning;

        public void Move(int dx, int dy) { MoveX += dx; MoveY += dy; Events.Add($"move {dx},{dy}"); }
        public void Button(bool right, bool down) => Events.Add($"{(right ? "right" : "left")} {(down ? "down" : "up")}");
        public void Wheel(int amount) { WheelTotal += amount; Events.Add($"wheel {amount}"); }
        public void HWheel(int amount) { HWheelTotal += amount; Events.Add($"hwheel {amount}"); }
        public void SetTimer(bool running) => TimerRunning = running;
    }

    private static ProReport Report(Buttons b, int lx = 2048, int ly = 2048, int rx = 2048, int ry = 2048) =>
        new(ReportFormat.Full, b, lx, ly, rx, ry);

    private static (ControllerState State, FakeOutput Out, MouseMode Mouse) Create()
    {
        var state = new ControllerState();
        var output = new FakeOutput();
        return (state, output, new MouseMode(state, output, clock: () => 0));
    }

    [Fact]
    public void Speed_is_quadratic_and_scaled_by_multiplier()
    {
        Assert.Equal(1400f, MouseMath.Speed(new Stick(1f, 0f), 1400f, 1f), 1);
        Assert.Equal(350f, MouseMath.Speed(new Stick(0.5f, 0f), 1400f, 1f), 1);
        Assert.Equal(3500f, MouseMath.Speed(new Stick(1f, 0f), 1400f, 2.5f), 1);
        Assert.Equal(0f, MouseMath.Speed(default, 1400f, 1f));
    }

    [Fact]
    public void Idle_sticks_do_not_arm_the_timer()
    {
        var (state, output, mouse) = Create();
        mouse.Activate(Buttons.None);
        state.Update(Report(Buttons.None));
        mouse.OnReport();
        Assert.False(output.TimerRunning);
    }

    [Fact]
    public void Stick_outside_deadzone_arms_timer_and_moves_then_stops_when_released()
    {
        var (state, output, mouse) = Create();
        mouse.Activate(Buttons.None);
        state.Update(Report(Buttons.None, lx: 2048 + 1300)); // full right
        mouse.OnReport();
        Assert.True(output.TimerRunning);

        mouse.Tick(0.05); // 1400 px/s * 0.05 s
        Assert.InRange(output.MoveX, 39, 40);
        Assert.Equal(0, output.MoveY);

        state.Update(Report(Buttons.None));
        mouse.Tick(0.05);
        Assert.False(output.TimerRunning);
    }

    [Fact]
    public void Stick_inside_deadzone_does_not_move()
    {
        var (state, output, mouse) = Create();
        mouse.Activate(Buttons.None);
        state.Update(Report(Buttons.None, lx: 2048 + 100)); // ~0.08, below 0.15
        mouse.OnReport();
        mouse.Tick(0.05);
        Assert.Equal(0, output.MoveX);
        Assert.False(output.TimerRunning);
    }

    [Fact]
    public void Fractions_accumulate_between_ticks()
    {
        var (state, output, mouse) = Create();
        mouse.Activate(Buttons.None);
        state.Update(Report(Buttons.None, lx: 2048 + 1300));
        mouse.OnReport();
        for (int i = 0; i < 10; i++) mouse.Tick(0.001); // 1.4 px each tick
        Assert.InRange(output.MoveX, 7, 8);
    }

    [Fact]
    public void ZR_speeds_up_and_ZL_slows_down()
    {
        var (state, output, mouse) = Create();
        mouse.Activate(Buttons.None);
        state.Update(Report(Buttons.ZR, lx: 2048 + 1300));
        mouse.OnReport();
        mouse.Tick(0.05);
        Assert.InRange(output.MoveX, 99, 100);

        output.MoveX = 0;
        state.Update(Report(Buttons.ZL, lx: 2048 + 1300));
        mouse.OnReport();
        mouse.Tick(0.05);
        Assert.InRange(output.MoveX, 11, 12);
    }

    [Fact]
    public void A_and_B_press_and_release_clicks()
    {
        var (state, output, mouse) = Create();
        mouse.Activate(Buttons.None);

        state.Update(Report(Buttons.A));
        mouse.OnReport();
        state.Update(Report(Buttons.A | Buttons.B));
        mouse.OnReport();
        state.Update(Report(Buttons.None));
        mouse.OnReport();

        Assert.Equal(["left down", "right down", "left up", "right up"], output.Events);
    }

    [Fact]
    public void Button_held_on_entry_is_ignored_until_released()
    {
        var (state, output, mouse) = Create();
        state.Update(Report(Buttons.A));
        mouse.Activate(Buttons.A); // the A press that selected "Mouse mode"
        state.Update(Report(Buttons.A));
        mouse.OnReport();
        state.Update(Report(Buttons.None));
        mouse.OnReport();
        Assert.Empty(output.Events);

        state.Update(Report(Buttons.A));
        mouse.OnReport();
        Assert.Equal(["left down"], output.Events);
    }

    [Fact]
    public void Deactivate_releases_held_buttons_and_stops_timer()
    {
        var (state, output, mouse) = Create();
        mouse.Activate(Buttons.None);
        state.Update(Report(Buttons.A | Buttons.B, lx: 2048 + 1300));
        mouse.OnReport();
        output.Events.Clear();

        mouse.Deactivate();
        Assert.Equal(["left up", "right up"], output.Events);
        Assert.False(output.TimerRunning);

        mouse.Deactivate(); // idempotent
        Assert.Equal(2, output.Events.Count);
    }

    [Fact]
    public void Right_stick_scrolls_in_chunks_up_is_positive()
    {
        var (state, output, mouse) = Create();
        mouse.Activate(Buttons.None);
        state.Update(Report(Buttons.None, ry: 2048 + 1300)); // full up (full reports grow upward)
        mouse.OnReport();
        mouse.Tick(0.05);
        mouse.Tick(0.05); // 12 notches/s * 120 * 0.1 s = 144 -> 3 chunks of 40
        Assert.Equal(120, output.WheelTotal);
        Assert.Equal(0, output.HWheelTotal);
    }

    [Fact]
    public void Right_stick_right_scrolls_horizontally_positive()
    {
        var (state, output, mouse) = Create();
        mouse.Activate(Buttons.None);
        state.Update(Report(Buttons.None, rx: 2048 + 1300));
        mouse.OnReport();
        mouse.Tick(0.05);
        mouse.Tick(0.05);
        Assert.Equal(120, output.HWheelTotal);
    }

    [Fact]
    public void Lone_joycon_stick_scrolls_while_L_or_R_is_held()
    {
        var (state, output, mouse) = Create();
        mouse.Activate(Buttons.None);
        state.Update(new ProReport(ReportFormat.Full, Buttons.R, 2048, 2048 + 1300, 2048, 2048, SingleStick: true));
        mouse.OnReport();
        mouse.Tick(0.05);
        mouse.Tick(0.05);
        Assert.Equal(120, output.WheelTotal);
        Assert.Equal(0, output.MoveY); // the stick scrolls instead of moving the cursor

        state.Update(new ProReport(ReportFormat.Full, Buttons.None, 2048, 2048 + 1300, 2048, 2048, SingleStick: true));
        mouse.Tick(0.05);
        Assert.NotEqual(0, output.MoveY); // released: it moves again
    }

    [Fact]
    public void Dpad_tap_moves_exactly_one_pixel()
    {
        var (state, output, mouse) = Create();
        mouse.Activate(Buttons.None);
        state.Update(Report(Buttons.Right));
        mouse.OnReport();
        state.Update(Report(Buttons.None));
        mouse.OnReport();
        Assert.Equal(["move 1,0"], output.Events);
    }

    [Fact]
    public void Dpad_held_glides_slowly_after_delay()
    {
        var (state, output, mouse) = Create();
        mouse.Activate(Buttons.None);
        state.Update(Report(Buttons.Down));
        mouse.OnReport();
        Assert.True(output.TimerRunning);
        output.MoveY = 0;
        mouse.Tick(0.05); mouse.Tick(0.05); mouse.Tick(0.05); // under 0.3 s: nothing
        Assert.Equal(0, output.MoveY);
        for (int i = 0; i < 10; i++) mouse.Tick(0.05); // 0.5 s more
        Assert.InRange(output.MoveY, 38, 46);
    }

    [Fact]
    public void ZR_and_ZL_scale_scrolling()
    {
        var (state, output, mouse) = Create();
        mouse.Activate(Buttons.None);

        state.Update(Report(Buttons.ZR, ry: 2048 + 1300)); // full up, fast
        mouse.OnReport();
        mouse.Tick(0.05); // 12 * 120 * 2.5 * 0.05 = 180 -> 160 in chunks of 40
        Assert.Equal(160, output.WheelTotal);

        output.WheelTotal = 0;
        state.Update(Report(Buttons.ZL, ry: 2048 + 1300)); // slow
        mouse.OnReport();
        for (int i = 0; i < 10; i++) mouse.Tick(0.05); // 12 * 120 * 0.3 * 0.5 s = 216 -> 200
        Assert.InRange(output.WheelTotal, 160, 240);
    }
}
