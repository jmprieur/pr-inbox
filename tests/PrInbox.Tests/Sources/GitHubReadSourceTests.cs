using PrInbox.Core.Models;
using PrInbox.Sources.GitHub;

namespace PrInbox.Tests.Sources;

public class BotDetectorTests
{
    private readonly BotDetector _detector = new();

    [Fact]
    public void Null_Login_With_Bot_Type_Reports_Bot_Other()
    {
        var (isBot, kind) = _detector.Classify(null, reportedTypeIsBot: true);
        isBot.Should().BeTrue();
        kind.Should().Be(BotKind.Other);
    }

    [Fact]
    public void Null_Login_Without_Bot_Type_Is_Not_A_Bot()
    {
        var (isBot, _) = _detector.Classify(null, reportedTypeIsBot: false);
        isBot.Should().BeFalse();
    }

    [Theory]
    [InlineData("copilot-pull-request-reviewer[bot]", BotKind.CopilotReview)]
    [InlineData("Copilot", BotKind.CopilotReview)]
    [InlineData("copilot[bot]", BotKind.CopilotReview)]
    public void Known_Copilot_Logins_Resolve_To_CopilotReview(string login, BotKind expected)
    {
        var (isBot, kind) = _detector.Classify(login, reportedTypeIsBot: false);
        isBot.Should().BeTrue();
        kind.Should().Be(expected);
    }

    [Fact]
    public void Copilot_Coding_Agent_Login_Resolves_To_CopilotCodingAgent()
    {
        var (isBot, kind) = _detector.Classify("copilot-coding-agent[bot]", reportedTypeIsBot: true);
        isBot.Should().BeTrue();
        kind.Should().Be(BotKind.CopilotCodingAgent);
    }

    [Fact]
    public void Github_Actions_Login_Resolves_To_GitHubActions()
    {
        var (isBot, kind) = _detector.Classify("github-actions[bot]", reportedTypeIsBot: true);
        isBot.Should().BeTrue();
        kind.Should().Be(BotKind.GitHubActions);
    }

    [Fact]
    public void Dependabot_Login_Resolves_To_Dependabot()
    {
        var (isBot, kind) = _detector.Classify("dependabot[bot]", reportedTypeIsBot: true);
        isBot.Should().BeTrue();
        kind.Should().Be(BotKind.Dependabot);
    }

    [Fact]
    public void Bracket_Bot_Login_Defaults_To_Other_Without_Match()
    {
        var (isBot, kind) = _detector.Classify("custom-helper[bot]", reportedTypeIsBot: true);
        isBot.Should().BeTrue();
        kind.Should().Be(BotKind.Other);
    }

    [Fact]
    public void Bracket_Bot_Login_Detected_Even_When_Reported_Bot_Is_False()
    {
        var (isBot, kind) = _detector.Classify("anything[bot]", reportedTypeIsBot: false);
        isBot.Should().BeTrue();
        kind.Should().Be(BotKind.Other);
    }

    [Fact]
    public void Human_Login_Is_Not_A_Bot()
    {
        var (isBot, kind) = _detector.Classify("jmprieur", reportedTypeIsBot: false);
        isBot.Should().BeFalse();
        kind.Should().BeNull();
    }

    [Fact]
    public void Extra_Bot_Logins_Are_Honored()
    {
        var detector = new BotDetector(new[] { "internal-service-account" });
        var (isBot, kind) = detector.Classify("internal-service-account", reportedTypeIsBot: false);
        isBot.Should().BeTrue();
        kind.Should().Be(BotKind.Other);
    }
}

public class GitHubReadSourceParseTests
{
    [Theory]
    [InlineData("https://github.com/agency-microsoft/playground/pull/4248", "agency-microsoft", "playground", 4248)]
    [InlineData("https://ghe.contoso.com/org/repo-name/pull/12", "org", "repo-name", 12)]
    public void ParseUrl_Splits_Correctly(string url, string owner, string repo, int number)
    {
        var (parsedOwner, parsedRepo, parsedNumber) =
            GitHubReadSource.ParseUrl(url);
        parsedOwner.Should().Be(owner);
        parsedRepo.Should().Be(repo);
        parsedNumber.Should().Be(number);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("https://github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo/issues/5")]
    public void ParseUrl_Throws_On_Malformed_Input(string input)
    {
        var act = () => GitHubReadSource.ParseUrl(input);
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void ParseUrl_Throws_For_AzureDevOps_Url()
    {
        var act = () => GitHubReadSource.ParseUrl("https://dev.azure.com/mseng/Context/_git/Private/pullrequest/1234");
        act.Should().Throw<FormatException>();
    }

    /// <summary>
    /// Regression test for the "PR I commented on disappeared from my inbox"
    /// bug. GitHub removes a reviewer from <c>requested_reviewers</c> the
    /// moment they submit any review, so a single <c>review-requested:@me</c>
    /// query was losing PRs the user was actively engaged with. The inbox
    /// must union <c>review-requested:@me</c> with <c>reviewed-by:@me</c>.
    /// </summary>
    [Fact]
    public void InboxQueries_Cover_Both_Pending_And_Engaged_Reviews()
    {
        GitHubReadSource.InboxQueries.Should().BeEquivalentTo(new[]
        {
            "is:pr is:open review-requested:@me",
            "is:pr is:open reviewed-by:@me",
        });
    }
}
