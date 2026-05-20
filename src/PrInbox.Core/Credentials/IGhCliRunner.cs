namespace PrInbox.Core.Credentials;

/// <summary>
/// Thin abstraction over "run <c>gh</c> with these args" so the
/// auth-discovery service can be unit-tested without a real binary
/// on PATH. The default impl shells out via <see cref="System.Diagnostics.Process"/>;
/// tests substitute a fake that returns canned output.
/// </summary>
public interface IGhCliRunner
{
    /// <summary>
    /// Run <c>gh</c> with the given arguments. Returns the exit code,
    /// stdout, and stderr. Implementations should NOT throw on a
    /// non-zero exit — callers want to handle that themselves.
    /// </summary>
    /// <param name="args">Arguments to pass to <c>gh</c> (do not include the executable name).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<GhCliResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct = default);
}

/// <param name="ExitCode">Process exit code. Zero = success.</param>
/// <param name="StdOut">Captured stdout.</param>
/// <param name="StdErr">Captured stderr.</param>
/// <param name="FailedToStart">
/// True if the process could not be started at all (e.g. <c>gh</c> not
/// on PATH). When true, <see cref="ExitCode"/> is meaningless and both
/// streams are empty.
/// </param>
public sealed record GhCliResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    bool FailedToStart);
