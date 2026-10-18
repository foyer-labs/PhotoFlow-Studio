// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.App.Services;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

/// <summary>
/// The GPU turns of phase 5b: analyses share the GPU; a level D pass waits for the analyses running, holds back the
/// new ones while it runs, and lets them go on after. A pass cancelled while waiting leaves the turns as they were.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class GpuTurnsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_pass_waits_for_the_analyses_and_holds_back_the_new_ones()
    {
        using var turns = new GpuTurns();
        var first = await turns.EnterSharedAsync(Ct);
        var second = await turns.EnterSharedAsync(Ct); // shared: both at once

        var pass = turns.EnterExclusiveAsync(Ct);
        await Task.Delay(50, Ct);
        Assert.False(pass.IsCompleted);
        first.Dispose();
        first.Dispose(); // once only
        await Task.Delay(50, Ct);
        Assert.False(pass.IsCompleted);
        second.Dispose();
        var exclusive = await pass.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        var waiting = turns.EnterSharedAsync(Ct);
        await Task.Delay(50, Ct);
        Assert.False(waiting.IsCompleted); // held back during the pass
        exclusive.Dispose();
        (await waiting.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Dispose();
    }

    [Fact]
    public async Task A_pass_cancelled_while_waiting_leaves_the_turns_free()
    {
        using var turns = new GpuTurns();
        var analysis = await turns.EnterSharedAsync(Ct);
        using var cancel = new CancellationTokenSource();
        var pass = turns.EnterExclusiveAsync(cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pass);

        (await turns.EnterSharedAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct)).Dispose(); // not blocked
        analysis.Dispose();
        (await turns.EnterExclusiveAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct)).Dispose();
    }
}
