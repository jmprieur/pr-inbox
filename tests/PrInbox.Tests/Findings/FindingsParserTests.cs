using PrInbox.Core.Findings;

namespace PrInbox.Tests.Findings;

public class FindingsParserTests
{
    private readonly FindingsParser _parser = new();

    private const string ValidSample = """
        schema_version: 1
        pr_url: https://github.com/owner/repo/pull/42
        pr_stable_identity: gh.com:123#456
        head_sha: deadbeefcafebabe1234567890abcdef12345678
        base_sha: 8a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b
        generated_at_utc: 2026-05-15T21:00:13Z
        models: [opus-4.7, gpt-5.5]
        asymmetry:
          both_found: 4
          opus_only: 2
          gpt_only: 3
        findings:
          - id: f01
            severity: critical
            confidence: high
            found_by: [opus, gpt]
            file: src/auth/TokenStore.cs
            line: 142
            line_end: 148
            diff_anchorable: true
            title: SQL injection in token lookup
            body: |
              Multi-line markdown explanation.
              Second line.
            suggested_inline: |
              ```suggestion
              command.Parameters.AddWithValue("@id", userId);
              ```
          - id: f02
            severity: low
            confidence: medium
            found_by: [gpt]
            file: docs/README.md
            line: 1
            diff_anchorable: false
            title: typo
        """;

    [Fact]
    public void ParseStrict_Accepts_Valid_Document()
    {
        var doc = _parser.ParseStrict(ValidSample);
        doc.SchemaVersion.Should().Be(1);
        doc.PrUrl.Should().Be("https://github.com/owner/repo/pull/42");
        doc.PrStableIdentity.Should().Be("gh.com:123#456");
        doc.HeadSha.Should().Be("deadbeefcafebabe1234567890abcdef12345678");
        doc.Models.Should().Equal("opus-4.7", "gpt-5.5");
        doc.Asymmetry.Should().NotBeNull();
        doc.Asymmetry!.BothFound.Should().Be(4);
        doc.Asymmetry.OpusOnly.Should().Be(2);
        doc.Findings.Should().HaveCount(2);
    }

    [Fact]
    public void Critical_Finding_Is_Parsed_With_Full_Fields()
    {
        var doc = _parser.ParseStrict(ValidSample);
        var critical = doc.Findings.Single(f => f.Severity == FindingSeverity.Critical);
        critical.Id.Should().Be("f01");
        critical.Confidence.Should().Be(FindingConfidence.High);
        critical.FoundBy.Should().Equal("opus", "gpt");
        critical.File.Should().Be("src/auth/TokenStore.cs");
        critical.Line.Should().Be(142);
        critical.LineEnd.Should().Be(148);
        critical.DiffAnchorable.Should().BeTrue();
        critical.Body.Should().Contain("Multi-line markdown");
        critical.SuggestedInline.Should().Contain("```suggestion");
    }

    [Fact]
    public void Low_Severity_Finding_With_Minimal_Fields_Parses()
    {
        var doc = _parser.ParseStrict(ValidSample);
        var low = doc.Findings.Single(f => f.Severity == FindingSeverity.Low);
        low.Id.Should().Be("f02");
        low.LineEnd.Should().BeNull();
        low.DiffAnchorable.Should().BeFalse();
        low.Body.Should().BeNull();
    }

    [Fact]
    public void ParseStrict_Rejects_Missing_Required_Top_Level_Field()
    {
        const string missingHead = """
            schema_version: 1
            pr_url: https://github.com/owner/repo/pull/42
            generated_at_utc: 2026-05-15T21:00:13Z
            findings: []
            """;
        var act = () => _parser.ParseStrict(missingHead);
        act.Should().Throw<FormatException>()
            .WithMessage("*head_sha*");
    }

    [Fact]
    public void ParseStrict_Rejects_Unknown_Severity()
    {
        const string badSeverity = """
            schema_version: 1
            pr_url: https://github.com/owner/repo/pull/42
            head_sha: deadbeef
            generated_at_utc: 2026-05-15T21:00:13Z
            findings:
              - id: f01
                severity: blocker
                confidence: high
                file: x.cs
                title: ...
            """;
        var act = () => _parser.ParseStrict(badSeverity);
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void ParseStrict_Rejects_Wrong_Schema_Version()
    {
        const string wrongVersion = """
            schema_version: 2
            pr_url: https://github.com/owner/repo/pull/42
            head_sha: deadbeef
            generated_at_utc: 2026-05-15T21:00:13Z
            findings: []
            """;
        var act = () => _parser.ParseStrict(wrongVersion);
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void ParseLenient_Returns_Errors_Without_Throwing()
    {
        const string bad = """
            schema_version: 99
            pr_url: not-a-url
            findings: []
            """;
        var result = _parser.ParseLenient(bad);
        result.Errors.Should().NotBeEmpty();
        result.Document.Should().NotBeNull("lenient mode preserves whatever could be parsed");
    }

    [Fact]
    public void ParseLenient_On_Empty_String_Returns_Empty_Error()
    {
        var result = _parser.ParseLenient("");
        result.Errors.Should().HaveCount(1);
        result.Document.Should().BeNull();
    }

    [Fact]
    public void Serialize_Then_ParseStrict_Round_Trips()
    {
        var original = _parser.ParseStrict(ValidSample);
        var yaml = _parser.Serialize(original);

        yaml.Should().Contain("schema_version: 1");
        yaml.Should().Contain("pr_url:");
        yaml.Should().Contain("diff_anchorable:");

        var round = _parser.ParseStrict(yaml);
        round.SchemaVersion.Should().Be(original.SchemaVersion);
        round.PrUrl.Should().Be(original.PrUrl);
        round.HeadSha.Should().Be(original.HeadSha);
        round.Findings.Should().HaveCount(original.Findings.Count);
        round.Findings[0].Severity.Should().Be(original.Findings[0].Severity);
        round.Findings[0].FoundBy.Should().Equal(original.Findings[0].FoundBy);
        round.Findings[0].DiffAnchorable.Should().Be(original.Findings[0].DiffAnchorable);
    }

    [Fact]
    public void SchemaJson_Is_Available_From_Embedded_Resource()
    {
        FindingsParser.SchemaJson.Should().Contain("\"title\": \"pr-inbox findings\"");
        FindingsParser.SchemaJson.Should().Contain("\"$defs\"");
    }

    [Fact]
    public void Enum_Roundtrip_Through_String_Form()
    {
        FindingEnumExtensions.ParseSeverity("critical").Should().Be(FindingSeverity.Critical);
        FindingEnumExtensions.ParseSeverity("HIGH").Should().Be(FindingSeverity.High);
        FindingSeverity.Medium.ToYamlValue().Should().Be("medium");
        FindingConfidence.High.ToYamlValue().Should().Be("high");
    }
}
