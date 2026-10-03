using AwesomeAssertions;
using Xunit;

namespace ChildProcessGuard.Tests;

public class CoalescingSnapshotTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Get_CalledSequentially_TakesANewSnapshotEachTime()
    {
        var taken = 0;
        var snapshot = new CoalescingSnapshot<int>(() => Interlocked.Increment(ref taken));

        snapshot.Get().Should().Be(1);
        snapshot.Get().Should().Be(2);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public async Task Get_CalledWhileASnapshotIsBeingTaken_SharesTheNextSnapshotOnly()
    {
        // Arrange - the first snapshot is held open until the other callers have arrived
        var taken = 0;
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var snapshot = new CoalescingSnapshot<int>(() =>
        {
            var number = Interlocked.Increment(ref taken);
            if (number == 1)
            {
                firstStarted.Set();
                releaseFirst.Wait(TimeSpan.FromSeconds(10));
            }

            return number;
        });

        // Dedicated threads, not the thread pool: blocked pool threads would start the later callers
        // late, after the first snapshot had already finished.
        var first = 0;
        var firstCaller = new Thread(() => first = snapshot.Get());
        firstCaller.Start();
        firstStarted.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).Should().BeTrue();

        // Act - callers that arrive while the first snapshot is in progress
        var results = new int[10];
        using var arrived = new CountdownEvent(results.Length);
        var laterCallers = Enumerable.Range(0, results.Length).Select(i => new Thread(() =>
        {
            arrived.Signal();
            results[i] = snapshot.Get();
        })).ToList();
        laterCallers.ForEach(t => t.Start());
        arrived.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).Should().BeTrue();
        await Task.Delay(200, TestContext.Current.CancellationToken); // from Signal to inside Get
        releaseFirst.Set();
        firstCaller.Join();
        laterCallers.ForEach(t => t.Join());

        // Assert - none of them is handed the snapshot that started before they asked
        first.Should().Be(1);
        results.Should().AllBeEquivalentTo(2);
        taken.Should().Be(2);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Get_WhenTheSnapshotThrows_RethrowsAndTakesANewOneNextTime()
    {
        var taken = 0;
        var snapshot = new CoalescingSnapshot<int>(() =>
            Interlocked.Increment(ref taken) == 1 ? throw new InvalidOperationException("unreadable") : taken);

        snapshot.Invoking(s => s.Get()).Should().Throw<InvalidOperationException>();
        snapshot.Get().Should().Be(2);
    }
}
