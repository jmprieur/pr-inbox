using FluentAssertions;
using PrInbox.Core.Findings;
using Xunit;

namespace PrInbox.Publishers.Tests;

public sealed class PublishHelpersTests
{
    [Fact]
    public void FingerprintOf_is_stable_for_same_file_line_title()
    {
        var a = MakeFinding(file: "src/Foo.cs", line: 42, title: "SQL injection in token lookup");
        var b = MakeFinding(file: "src/Foo.cs", line: 42, title: "SQL injection in token lookup");
        PublishHelpers.FingerprintOf(a).Should().Be(PublishHelpers.FingerprintOf(b));
    }

    [Fact]
    public void FingerprintOf_is_insensitive_to_casing_and_whitespace()
    {
        var a = MakeFinding(title: "SQL injection in token lookup");
        var b = MakeFinding(title: "  sql   injection IN token   lookup  ");
        PublishHelpers.FingerprintOf(a).Should().Be(PublishHelpers.FingerprintOf(b));
    }

    [Fact]
    public void FingerprintOf_differs_when_line_changes()
    {
        var a = MakeFinding(line: 42);
        var b = MakeFinding(line: 43);
        PublishHelpers.FingerprintOf(a).Should().NotBe(PublishHelpers.FingerprintOf(b));
    }

    [Fact]
    public void FingerprintOf_differs_when_file_changes()
    {
        var a = MakeFinding(file: "src/Foo.cs");
        var b = MakeFinding(file: "src/Bar.cs");
        PublishHelpers.FingerprintOf(a).Should().NotBe(PublishHelpers.FingerprintOf(b));
    }

    [Fact]
    public void ComposeReviewBody_includes_authored_head_and_non_anchorables()
    {
        var na = new[]
        {
            new FindingToPost(
                Id: "f02", Severity: FindingSeverity.High, Confidence: FindingConfidence.Medium,
                FoundBy: Array.Empty<string>(),
                File: "src/Bar.cs", Line: 17, LineEnd: null, DiffAnchorable: false,
                Title: "Refactor opportunity", Body: "Could extract method.", SuggestedInline: null),
        };
        var body = PublishHelpers.ComposeReviewBody(
            header: "**Automated review from PrInbox**",
            nonAnchorable: na,
            headSha: "abcdef1234567890");

        body.Should().Contain("Automated review from PrInbox");
        body.Should().Contain("abcdef12");                       // short sha
        body.Should().Contain("Non-anchorable findings");
        body.Should().Contain("src/Bar.cs:17");
        body.Should().Contain("Refactor opportunity");
    }

    [Fact]
    public void ComposeInlineCommentBody_includes_title_body_and_suggestion()
    {
        var f = MakeFinding(
            title: "SQL injection",
            body: "Concatenated SQL.",
            suggested: "```suggestion\ncommand.Parameters.AddWithValue();\n```");

        var text = PublishHelpers.ComposeInlineCommentBody(f);
        text.Should().Contain("SQL injection");
        text.Should().Contain("Concatenated SQL.");
        text.Should().Contain("```suggestion");
        text.Should().Contain("AddWithValue");
    }

    private static FindingToPost MakeFinding(
        string file = "src/Foo.cs",
        int line = 42,
        string title = "Some issue",
        string body = "Details here.",
        string? suggested = null)
        => new(
            Id: "f01",
            Severity: FindingSeverity.Critical,
            Confidence: FindingConfidence.High,
            FoundBy: new[] { "opus" },
            File: file,
            Line: line,
            LineEnd: null,
            DiffAnchorable: true,
            Title: title,
            Body: body,
            SuggestedInline: suggested);
}
