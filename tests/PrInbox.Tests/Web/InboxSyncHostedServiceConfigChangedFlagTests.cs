using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PrInbox.Web.Services;

namespace PrInbox.Tests.Web;

/// <summary>
/// Locks the single-shot atomic semantics of
/// <see cref="InboxSyncHostedService.NoteConfigChanged"/> /
/// <see cref="InboxSyncHostedService.ConsumeConfigChanged"/>. The flag
/// is the contract that the Inbox page consumes on arrival to decide
/// whether to kick an out-of-band sync after a Settings edit. The
/// guarantees these tests pin:
/// 1. A fresh host returns false (no false positives).
/// 2. After Note, the first Consume returns true; subsequent ones
///    return false until the next Note (single-shot — Inbox can't
///    accidentally double-sync from one Settings edit).
/// 3. Multiple Notes coalesce into one Consume (rapid-fire edits
///    don't queue up redundant syncs).
/// 4. Concurrent consumers see exactly one true (so SignalR
///    reconnects firing OnAfterRender twice on first paint don't both
///    trigger a sync).
/// </summary>
public class InboxSyncHostedServiceConfigChangedFlagTests
{
    private static InboxSyncHostedService NewHost() =>
        new(new InboxState(),
            new ConfigurationBuilder().Build(),
            NullLogger<InboxSyncHostedService>.Instance);

    [Fact]
    public void Fresh_Host_Consume_Returns_False()
    {
        var host = NewHost();
        host.ConsumeConfigChanged().Should().BeFalse();
    }

    [Fact]
    public void Note_Then_Consume_Returns_True_Once()
    {
        var host = NewHost();
        host.NoteConfigChanged();

        host.ConsumeConfigChanged().Should().BeTrue();
        host.ConsumeConfigChanged().Should().BeFalse();
    }

    [Fact]
    public void Multiple_Notes_Before_Consume_Coalesce()
    {
        var host = NewHost();
        host.NoteConfigChanged();
        host.NoteConfigChanged();
        host.NoteConfigChanged();

        host.ConsumeConfigChanged().Should().BeTrue();
        host.ConsumeConfigChanged().Should().BeFalse();
    }

    [Fact]
    public async Task Concurrent_Consumers_See_Exactly_One_True()
    {
        var host = NewHost();
        host.NoteConfigChanged();

        const int consumerCount = 32;
        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, consumerCount).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return host.ConsumeConfigChanged();
        })).ToArray();

        start.Set();
        var results = await Task.WhenAll(tasks);

        results.Count(r => r).Should().Be(1, "single-shot semantics must hold across threads");
    }
}
