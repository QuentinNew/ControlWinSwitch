using ControlWinSwitch.App;
using ControlWinSwitch.Input;

namespace ControlWinSwitch.Tests;

public class RadialModelTests
{
    private static MenuEntry Entry(string label, CommandKind kind = CommandKind.Quit, Func<MenuPage>? submenu = null,
        string? confirm = null, bool enabled = true) =>
        new() { Label = label, Command = new(kind), Submenu = submenu, ConfirmPrompt = confirm, Enabled = enabled };

    private static MenuPage Page(int count) =>
        new("Root", Enumerable.Range(0, count).Select(i => Entry($"e{i}")).ToList());

    private static RadialModel Open(MenuPage page)
    {
        var m = new RadialModel();
        m.Reset(page);
        return m;
    }

    [Fact]
    public void Root_ring_spreads_entries_evenly_starting_at_the_top()
    {
        var m = Open(Page(4));
        var nodes = m.Ring.Nodes;

        Assert.Equal(4, nodes.Count);
        Assert.Equal(0f, nodes[0].Angle, 3);
        Assert.Equal(MathF.PI / 2, nodes[1].Angle, 3);
        Assert.Equal(MathF.PI, nodes[2].Angle, 3);
    }

    [Fact]
    public void Submenu_ring_has_its_back_node_opposite_the_node_it_came_from()
    {
        var sub = new MenuPage("Sub", [Entry("x"), Entry("y")]);
        var m = Open(new MenuPage("Root", [Entry("a"), Entry("b", submenu: () => sub), Entry("c"), Entry("d")]));
        float parent = m.Ring.Nodes[1].Angle; // right

        m.Select(1);
        Assert.Null(m.Activate());

        Assert.Equal(2, m.Depth);
        var back = m.Ring.Nodes[0];
        Assert.Equal(RadialNodeKind.Back, back.Kind);
        Assert.Equal(0, RadialGeometry.Distance(back.Angle, parent + MathF.PI), 3);
        Assert.Equal(3, m.Ring.Nodes.Count);
    }

    [Fact]
    public void Back_node_returns_to_the_parent_ring()
    {
        var sub = new MenuPage("Sub", [Entry("x")]);
        var m = Open(new MenuPage("Root", [Entry("a", submenu: () => sub)]));
        m.Select(0);
        m.Activate();

        m.Select(0); // the back node
        Assert.Null(m.Activate());

        Assert.Equal(1, m.Depth);
    }

    [Fact]
    public void Pages_over_eight_nodes_get_a_more_node_and_the_rest_on_the_next_ring()
    {
        var m = Open(Page(10));

        Assert.Equal(8, m.Ring.Nodes.Count);
        var more = m.Ring.Nodes[^1];
        Assert.Equal(RadialNodeKind.More, more.Kind);
        Assert.Equal(7, more.NextStart);

        m.Select(7);
        m.Activate();

        // 3 entries left + back
        Assert.Equal(2, m.Depth);
        Assert.Equal(4, m.Ring.Nodes.Count);
        Assert.Equal("e7", m.Ring.Nodes[1].Label);
    }

    [Fact]
    public void Confirmation_needs_a_second_selection_of_the_same_node()
    {
        var m = Open(new MenuPage("Root", [Entry("a"), Entry("Quit", CommandKind.Quit, confirm: "Quit?")]));

        m.Select(1);
        Assert.Null(m.Activate());
        Assert.True(m.IsConfirming);

        m.Select(-1); // stick back to center: still armed
        m.Select(1);
        var run = m.Activate();

        Assert.NotNull(run);
        Assert.Equal(CommandKind.Quit, run!.Value.Command.Kind);
    }

    [Fact]
    public void Selecting_another_node_drops_the_confirmation()
    {
        var m = Open(new MenuPage("Root", [Entry("a"), Entry("Quit", CommandKind.Quit, confirm: "Quit?")]));

        m.Select(1);
        m.Activate();
        m.Select(0);
        m.Select(1);

        Assert.Null(m.Activate()); // armed again, not executed
        Assert.True(m.IsConfirming);
    }

    [Fact]
    public void Back_cancels_a_confirmation_before_leaving_the_ring()
    {
        var m = Open(new MenuPage("Root", [Entry("Quit", confirm: "Quit?")]));
        m.Select(0);
        m.Activate();

        Assert.True(m.Back());
        Assert.False(m.IsConfirming);
        Assert.False(m.Back()); // root: the menu closes
    }

    [Fact]
    public void Disabled_nodes_cannot_be_highlighted_and_are_skipped_when_stepping()
    {
        var m = Open(new MenuPage("Root", [Entry("a"), Entry("b", enabled: false), Entry("c")]));

        Assert.False(m.Select(1));
        m.Step(1);
        Assert.Equal(0, m.Selected);
        m.Step(1);
        Assert.Equal(2, m.Selected);
        m.Step(1);
        Assert.Equal(0, m.Selected);
    }
}

public class RadialStickTests
{
    private static RadialRing Ring(int count) =>
        RadialModel.BuildRing(new MenuPage("Root",
            Enumerable.Range(0, count).Select(i => new MenuEntry { Label = $"e{i}" }).ToList()), 0, null);

    [Fact]
    public void Tilt_highlights_the_node_in_that_direction()
    {
        var stick = new RadialStick(100);
        var ring = Ring(4); // up, right, down, left
        stick.Update(default, 0, ring);

        stick.Update(new Stick(0.9f, 0f), 16, ring);

        Assert.Equal(1, stick.Selected);
    }

    [Fact]
    public void A_fast_return_to_center_is_a_release()
    {
        var stick = new RadialStick(100);
        var ring = Ring(4);
        stick.Update(default, 0, ring);
        stick.Update(new Stick(-0.95f, 0f), 16, ring);
        stick.Update(new Stick(-0.95f, 0f), 32, ring);

        Assert.Equal(-1, stick.Update(new Stick(-0.4f, 0f), 48, ring)); // still held
        int released = stick.Update(new Stick(-0.05f, 0f), 64, ring);

        Assert.Equal(3, released);
        Assert.Equal(-1, stick.Selected);
    }

    [Fact]
    public void A_slow_return_to_center_only_clears_the_highlight()
    {
        var stick = new RadialStick(100);
        var ring = Ring(4);
        stick.Update(default, 0, ring);
        stick.Update(new Stick(-0.95f, 0f), 16, ring);

        long t = 16;
        int released = -1;
        for (float m = 0.9f; m > 0f; m -= 0.1f)
        {
            t += 40;
            released = Math.Max(released, stick.Update(new Stick(-m, 0f), t, ring));
        }
        t += 40;
        released = Math.Max(released, stick.Update(default, t, ring));

        Assert.Equal(-1, released);
        Assert.Equal(-1, stick.Selected);
    }

    [Fact]
    public void A_stick_already_tilted_must_return_to_center_before_it_selects()
    {
        var stick = new RadialStick(100);
        var ring = Ring(4);
        stick.Reset();

        stick.Update(new Stick(0.9f, 0f), 0, ring);
        Assert.Equal(-1, stick.Selected);

        stick.Update(default, 16, ring);
        stick.Update(new Stick(0.9f, 0f), 32, ring);
        Assert.Equal(1, stick.Selected);
    }

    [Fact]
    public void A_release_reported_only_on_change_still_counts_after_a_long_rest()
    {
        var stick = new RadialStick(100);
        var ring = Ring(4);
        stick.Update(default, 0, ring);
        stick.Update(new Stick(0.95f, 0f), 16, ring);

        // Nothing is reported for two seconds, then the spring-back arrives in one step.
        int released = stick.Update(default, 2016, ring);

        Assert.Equal(1, released);
    }
}

public class RadialCardinalBiasTests
{
    private static RadialRing Ring8() =>
        RadialModel.BuildRing(new MenuPage("Root",
            Enumerable.Range(0, 8).Select(i => new MenuEntry { Label = $"e{i}" }).ToList()), 0, null);

    private static Stick Tilt(float degrees, float magnitude = 0.9f)
    {
        float a = degrees * MathF.PI / 180f;
        return new Stick(MathF.Sin(a) * magnitude, -MathF.Cos(a) * magnitude); // 0 = up, clockwise
    }

    [Fact]
    public void Without_bias_the_sectors_are_equal()
    {
        var stick = new RadialStick(100);
        var ring = Ring8();
        stick.Update(default, 0, ring);

        stick.Update(Tilt(27), 16, ring);

        Assert.Equal(1, stick.Selected); // the diagonal (45 degrees) is closer
    }

    [Fact]
    public void With_bias_the_straight_directions_get_a_wider_sector()
    {
        var stick = new RadialStick(100) { CardinalBias = 12 * MathF.PI / 180f };
        var ring = Ring8();
        stick.Update(default, 0, ring);

        stick.Update(Tilt(27), 16, ring);

        Assert.Equal(0, stick.Selected); // still "up"
    }

    [Fact]
    public void The_diagonal_is_still_reachable_with_bias()
    {
        var stick = new RadialStick(100) { CardinalBias = 12 * MathF.PI / 180f };
        var ring = Ring8();
        stick.Update(default, 0, ring);

        stick.Update(Tilt(40), 16, ring);

        Assert.Equal(1, stick.Selected);
    }
}
