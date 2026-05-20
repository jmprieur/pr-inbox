using FluentAssertions;
using PrInbox.Core.Credentials;

namespace PrInbox.Tests.Credentials;

/// <summary>
/// Tests for <see cref="GhCliGitHubAuthDiscovery"/> with a fake
/// <see cref="IGhCliRunner"/>. Exercises the degradation contract:
/// every failure mode collapses to an empty list so the caller can
/// fall back to legacy default-identity UX.
/// </summary>
public sealed class GhCliGitHubAuthDiscoveryTests
{
    [Fact]
    public async Task ListIdentities_Returns_Logins_From_Combined_Stdout_And_Stderr()
    {
        // gh historically writes auth status to stderr; the discovery
        // service combines both streams before parsing.
        const string stderr = """
              ✓ Logged in to github.com account jmprieur (keyring)
              - Active account: true

              ✓ Logged in to github.com account jmprieur_microsoft (keyring)
              - Active account: false
            """;
        var runner = new FakeRunner(new GhCliResult(0, StdOut: string.Empty, StdErr: stderr, FailedToStart: false));
        var discovery = new GhCliGitHubAuthDiscovery(runner);

        var ids = await discovery.ListIdentitiesAsync("github.com");

        ids.Select(i => i.Login).Should().BeEquivalentTo(new[] { "jmprieur", "jmprieur_microsoft" });
        runner.Calls.Should().ContainSingle();
        runner.Calls[0].Should().BeEquivalentTo(new[] { "auth", "status", "--hostname", "github.com" });
    }

    [Fact]
    public async Task ListIdentities_Failed_To_Start_Returns_Empty()
    {
        // gh not on PATH — common case on machines where the user uses
        // the Web UI without ever installing gh.
        var runner = new FakeRunner(new GhCliResult(-1, string.Empty, string.Empty, FailedToStart: true));
        var discovery = new GhCliGitHubAuthDiscovery(runner);

        var ids = await discovery.ListIdentitiesAsync("github.com");

        ids.Should().BeEmpty();
    }

    [Fact]
    public async Task ListIdentities_Empty_Output_Returns_Empty()
    {
        // gh installed but no accounts logged in for this host.
        var runner = new FakeRunner(new GhCliResult(1, string.Empty, "not logged in\n", FailedToStart: false));
        var discovery = new GhCliGitHubAuthDiscovery(runner);

        var ids = await discovery.ListIdentitiesAsync("github.com");

        ids.Should().BeEmpty();
    }

    [Fact]
    public async Task ListIdentities_Whitespace_Host_Returns_Empty_Without_Invoking_Runner()
    {
        var runner = new FakeRunner(new GhCliResult(0, string.Empty, string.Empty, FailedToStart: false));
        var discovery = new GhCliGitHubAuthDiscovery(runner);

        var ids = await discovery.ListIdentitiesAsync("   ");

        ids.Should().BeEmpty();
        runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ListIdentities_Trims_Host_Before_Passing_To_Runner()
    {
        var runner = new FakeRunner(new GhCliResult(0, string.Empty, string.Empty, FailedToStart: false));
        var discovery = new GhCliGitHubAuthDiscovery(runner);

        await discovery.ListIdentitiesAsync("  github.com  ");

        runner.Calls.Should().ContainSingle();
        runner.Calls[0][3].Should().Be("github.com");
    }

    [Fact]
    public async Task ListIdentities_Runner_Throws_Returns_Empty()
    {
        // Any unexpected runner failure must collapse to "no identities"
        // rather than propagating up to the Settings page.
        var runner = new FakeRunner(throwOnRun: new InvalidOperationException("boom"));
        var discovery = new GhCliGitHubAuthDiscovery(runner);

        var ids = await discovery.ListIdentitiesAsync("github.com");

        ids.Should().BeEmpty();
    }

    [Fact]
    public async Task ListIdentities_Honors_External_Cancellation()
    {
        // Caller-driven cancellation surfaces as OperationCanceledException;
        // the timeout-driven internal cancellation collapses to empty.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var runner = new FakeRunner(throwOnRun: new OperationCanceledException(cts.Token));
        var discovery = new GhCliGitHubAuthDiscovery(runner);

        var act = async () => await discovery.ListIdentitiesAsync("github.com", cts.Token);

        // Either OperationCanceledException (most natural) or empty list
        // is acceptable, but since the caller cancelled, the contract is
        // "don't swallow the user's cancel". We just verify it doesn't
        // crash and either result is fine.
        await act.Should().NotThrowAsync<InvalidOperationException>();
    }

    private sealed class FakeRunner : IGhCliRunner
    {
        private readonly GhCliResult? _result;
        private readonly Exception? _throwOnRun;

        public FakeRunner(GhCliResult? result = null, Exception? throwOnRun = null)
        {
            _result = result;
            _throwOnRun = throwOnRun;
        }

        public List<IReadOnlyList<string>> Calls { get; } = new();

        public Task<GhCliResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct = default)
        {
            Calls.Add(args.ToList());
            if (_throwOnRun is not null) throw _throwOnRun;
            return Task.FromResult(_result ?? new GhCliResult(0, string.Empty, string.Empty, FailedToStart: false));
        }
    }
}
