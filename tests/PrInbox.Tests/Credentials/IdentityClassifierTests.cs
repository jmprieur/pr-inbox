using PrInbox.Core.Credentials;

namespace PrInbox.Tests.Credentials;

public class IdentityClassifierTests
{
    // The Microsoft profile taxonomy (ordered: EMU before Public on github.com).
    private static readonly IReadOnlyList<IdentityClass> Microsoft = new[]
    {
        new IdentityClass { Name = "EMU",     Host = "github.com",        AliasSuffix = "_microsoft" },
        new IdentityClass { Name = "Proxima", Host = "microsoft.ghe.com", AliasSuffix = "_microsoft" },
        new IdentityClass { Name = "Public",  Host = "github.com",        AliasSuffix = "" },
    };

    [Theory]
    [InlineData("jmprieur_microsoft", "github.com",         "EMU")]
    [InlineData("jmprieur",           "github.com",         "Public")]
    [InlineData("alias_microsoft",    "microsoft.ghe.com",  "Proxima")]
    [InlineData("jean-marc-prieur",   "microsoft.ghe.com",  null)]     // no suffix on ghe → unmatched
    [InlineData("jmprieur_microsoft", "github.contoso.com", null)]     // unknown host → unmatched
    public void Classify_MatchesHostAndSuffix(string login, string host, string? expected)
        => IdentityClassifier.Classify(login, host, Microsoft).Should().Be(expected);

    [Fact]
    public void IsShortCode_ChecksNamedClass()
    {
        IdentityClassifier.IsShortCode("EMU", "jmprieur_microsoft", "github.com", Microsoft).Should().BeTrue();
        IdentityClassifier.IsShortCode("EMU", "jmprieur", "github.com", Microsoft).Should().BeFalse();
        IdentityClassifier.IsShortCode("Proxima", "alias_microsoft", "microsoft.ghe.com", Microsoft).Should().BeTrue();
    }

    [Fact]
    public void IsManaged_TrueOnlyForSuffixClasses()
    {
        IdentityClassifier.IsManaged("jmprieur_microsoft", "github.com", Microsoft).Should().BeTrue();   // EMU
        IdentityClassifier.IsManaged("jmprieur", "github.com", Microsoft).Should().BeFalse();            // Public (no suffix)
    }

    [Theory]
    [InlineData("jmprieur_microsoft", "jmprieur")]
    [InlineData("jmprieur", "jmprieur")]
    [InlineData("_microsoft", "_microsoft")]   // login is only the suffix → unchanged
    public void StripAliasSuffix_RemovesMatchingSuffix(string login, string expected)
        => IdentityClassifier.StripAliasSuffix(login, host: null, Microsoft).Should().Be(expected);

    [Fact]
    public void Classify_EmptyClasses_ReturnsNull()
        => IdentityClassifier.Classify("jmprieur_microsoft", "github.com", System.Array.Empty<IdentityClass>())
            .Should().BeNull();
}
