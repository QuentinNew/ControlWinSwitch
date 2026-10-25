namespace ControlWinSwitch.Tests;

public class ErrorBudgetTests
{
    [Fact]
    public void First_error_is_logged_and_the_next_ones_are_quiet()
    {
        var budget = new ErrorBudget(giveUpCount: 5, windowMs: 1000);
        Assert.Equal(ErrorVerdict.Log, budget.Record(0, out int suppressed));
        Assert.Equal(0, suppressed);
        Assert.Equal(ErrorVerdict.Quiet, budget.Record(10, out _));
        Assert.Equal(ErrorVerdict.Quiet, budget.Record(20, out _));
    }

    [Fact]
    public void Too_many_errors_within_the_window_give_up()
    {
        var budget = new ErrorBudget(giveUpCount: 3, windowMs: 1000);
        budget.Record(0, out _);
        budget.Record(1, out _);
        Assert.Equal(ErrorVerdict.GiveUp, budget.Record(2, out _));
    }

    [Fact]
    public void A_new_window_logs_again_and_counts_the_quiet_ones()
    {
        var budget = new ErrorBudget(giveUpCount: 10, windowMs: 1000);
        budget.Record(0, out _);
        budget.Record(100, out _);
        budget.Record(200, out _);
        Assert.Equal(ErrorVerdict.Log, budget.Record(1000, out int suppressed));
        Assert.Equal(2, suppressed);
    }

    [Fact]
    public void Spread_out_errors_never_give_up()
    {
        var budget = new ErrorBudget(giveUpCount: 2, windowMs: 1000);
        for (int i = 0; i < 10; i++)
            Assert.Equal(ErrorVerdict.Log, budget.Record(i * 1000L, out _));
    }
}
