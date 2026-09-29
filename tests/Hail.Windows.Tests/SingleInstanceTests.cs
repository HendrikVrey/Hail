namespace Hail.Windows.Tests;

public sealed class SingleInstanceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // A name of the test's own, so a Hail running on this machine is never touched.
    private static string UniqueName() => "HailTest." + Guid.NewGuid().ToString("N");

    [Fact]
    public void A_second_claim_in_the_same_session_is_refused_until_the_first_lets_go()
    {
        var name = UniqueName();
        using (var first = SingleInstance.TryClaim(name))
        {
            Assert.NotNull(first);
            Assert.Null(SingleInstance.TryClaim(name));
        }

        using var again = SingleInstance.TryClaim(name);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task A_second_start_passes_its_command_to_the_first()
    {
        var name = UniqueName();
        using var claim = SingleInstance.TryClaim(name)!;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var shown = new TaskCompletionSource();
        var received = new List<InstanceCommand>();
        var listening = claim.ListenAsync(
            command =>
            {
                lock (received)
                {
                    received.Add(command);
                }

                shown.TrySetResult();
            },
            stop.Token);

        Assert.True(await SingleInstance.SignalAsync(InstanceCommand.Show, name, TimeSpan.FromSeconds(5)));
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

        // And it keeps listening after answering once.
        shown = new TaskCompletionSource();
        Assert.True(await SingleInstance.SignalAsync(InstanceCommand.Quit, name, TimeSpan.FromSeconds(5)));
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

        Assert.Equal([InstanceCommand.Show, InstanceCommand.Quit], received);

        await stop.CancelAsync();
        await listening.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task With_nobody_listening_the_signal_gives_up_rather_than_hanging()
    {
        Assert.False(await SingleInstance.SignalAsync(InstanceCommand.Show, UniqueName(), TimeSpan.FromMilliseconds(300)));
    }
}
