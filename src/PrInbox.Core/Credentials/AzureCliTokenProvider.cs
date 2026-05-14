using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PrInbox.Core.Credentials;

/// <summary>
/// Acquires Azure DevOps access tokens via <c>Azure.Identity.AzureCliCredential</c>.
/// The credential shells out to <c>az</c> under the hood; no PATs in flight,
/// no token storage in pr-inbox.
/// </summary>
public sealed class AzureCliTokenProvider : ITokenProvider
{
    /// <summary>
    /// The Azure AD app ID for Azure DevOps. This identifier is documented and
    /// stable across tenants.
    /// </summary>
    public const string AzureDevOpsResource = "499b84ac-1321-427f-aa17-267ca6975798";

    /// <summary>The scope string used when requesting tokens for ADO.</summary>
    public const string AzureDevOpsScope = AzureDevOpsResource + "/.default";

    private readonly TokenCredential _credential;
    private readonly ILogger<AzureCliTokenProvider> _logger;

    public AzureCliTokenProvider(string sourceId, ILogger<AzureCliTokenProvider>? logger = null, TokenCredential? credential = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        SourceId = sourceId;
        _credential = credential ?? new AzureCliCredential();
        _logger = logger ?? NullLogger<AzureCliTokenProvider>.Instance;
    }

    public string SourceId { get; }

    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        try
        {
            var requestContext = new TokenRequestContext(new[] { AzureDevOpsScope });
            var token = await _credential.GetTokenAsync(requestContext, ct);
            return token.Token;
        }
        catch (CredentialUnavailableException ex)
        {
            throw new TokenAcquisitionException(
                "Azure CLI credentials unavailable. Run: az login\n" +
                $"  underlying: {ex.Message}", ex);
        }
        catch (AuthenticationFailedException ex)
        {
            throw new TokenAcquisitionException(
                "Azure CLI failed to mint a token for Azure DevOps. " +
                "Run: az account show ; az login\n" +
                $"  underlying: {ex.Message}", ex);
        }
    }

    public Task<string?> GetAuthenticatedIdentityAsync(CancellationToken ct = default)
    {
        // We could parse `az account show --query user.name -o tsv` but that's
        // an extra process invocation. The Profile API gives the same data
        // server-side once the first token has been acquired. For v0.1 we just
        // delegate to az and accept null if anything goes wrong.
        return GetAzAccountUserNameAsync(ct);
    }

    private static async Task<string?> GetAzAccountUserNameAsync(CancellationToken ct)
    {
        // On Windows az is shipped as az.cmd; Process.Start with UseShellExecute=false
        // cannot launch .cmd directly, so wrap with cmd.exe /c on Windows.
        var (fileName, leadingArgs) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", "az" })
            : ("az", Array.Empty<string>());

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in leadingArgs)
        {
            psi.ArgumentList.Add(a);
        }
        psi.ArgumentList.Add("account");
        psi.ArgumentList.Add("show");
        psi.ArgumentList.Add("--query");
        psi.ArgumentList.Add("user.name");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("tsv");

        try
        {
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return null;
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0) return null;
            var line = (await stdoutTask).Trim();
            return string.IsNullOrEmpty(line) ? null : line;
        }
        catch
        {
            return null;
        }
    }
}
