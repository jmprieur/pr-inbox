using System.Text.Json;
using PrInbox.Core.Findings;
using PrInbox.Web.Services;

namespace PrInbox.Tests.Reviewing;

/// <summary>
/// Tests for the editable-comment story on <see cref="ReviewRunStore"/>:
/// the per-finding body-override dictionary on <see cref="ReviewRun"/>,
/// its on-disk persistence as <c>comment-overrides.json</c>, and the
/// invariant the rubber-duck review flagged as highest risk -- a
/// findings.yaml reload during the same run must not lose user edits.
/// </summary>
public class ReviewRunStoreBodyOverridesTests
{
    private static (string Dir, Action Cleanup) FreshRunDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prinbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return (dir, () =>
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        });
    }

    private static FindingsDocument DocWithFindings(params (string Id, string Body)[] items)
    {
        return new FindingsDocument
        {
            SchemaVersion = 1,
            PrUrl = "https://github.com/owner/repo/pull/1",
            HeadSha = "deadbeef",
            Findings = items.Select(t => new Finding
            {
                Id = t.Id,
                Severity = FindingSeverity.High,
                File = "src/x.cs",
                Line = 10,
                DiffAnchorable = true,
                Title = $"Finding {t.Id}",
                Body = t.Body,
            }).ToArray(),
        };
    }

    private static ReviewRun MakeRun(string runDir, string url = "https://github.com/owner/repo/pull/1")
        => new(
            RunId: 1,
            PrUrl: url,
            RunDirectory: runDir,
            HeadSha: "deadbeef",
            StartedAtUtc: DateTimeOffset.UtcNow,
            FindingsAtUtc: null,
            Findings: null,
            FindingsErrors: Array.Empty<string>());

    [Fact]
    public void SetBodyOverride_Writes_Override_To_Memory_And_Disk()
    {
        var (dir, cleanup) = FreshRunDir();
        try
        {
            var store = new ReviewRunStore();
            var run = MakeRun(dir);
            store.StartedRun(run);

            store.SetBodyOverride(run.PrUrl, "f01", "tweaked body");

            var got = store.Get(run.PrUrl);
            got!.BodyOverrides.Should().ContainKey("f01").WhoseValue.Should().Be("tweaked body");

            var diskPath = Path.Combine(dir, "comment-overrides.json");
            File.Exists(diskPath).Should().BeTrue();
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(diskPath));
            dict.Should().ContainKey("f01").WhoseValue.Should().Be("tweaked body");
        }
        finally { cleanup(); }
    }

    [Fact]
    public void ClearBodyOverride_Removes_Entry_And_Deletes_Empty_File()
    {
        var (dir, cleanup) = FreshRunDir();
        try
        {
            var store = new ReviewRunStore();
            store.StartedRun(MakeRun(dir));
            store.SetBodyOverride("https://github.com/owner/repo/pull/1", "f01", "edit1");

            store.ClearBodyOverride("https://github.com/owner/repo/pull/1", "f01");

            store.Get("https://github.com/owner/repo/pull/1")!.BodyOverrides.Should().BeEmpty();
            File.Exists(Path.Combine(dir, "comment-overrides.json")).Should().BeFalse(
                "the disk file should be deleted when the in-memory override dict is empty");
        }
        finally { cleanup(); }
    }

    [Fact]
    public void Multiple_Overrides_Preserved_When_One_Cleared()
    {
        var (dir, cleanup) = FreshRunDir();
        try
        {
            var store = new ReviewRunStore();
            var run = MakeRun(dir);
            store.StartedRun(run);

            store.SetBodyOverride(run.PrUrl, "f01", "edit1");
            store.SetBodyOverride(run.PrUrl, "f02", "edit2");
            store.SetBodyOverride(run.PrUrl, "f03", "edit3");

            store.ClearBodyOverride(run.PrUrl, "f02");

            var got = store.Get(run.PrUrl)!.BodyOverrides;
            got.Should().HaveCount(2);
            got.Should().ContainKey("f01").WhoseValue.Should().Be("edit1");
            got.Should().ContainKey("f03").WhoseValue.Should().Be("edit3");
            got.Should().NotContainKey("f02");
        }
        finally { cleanup(); }
    }

    [Fact]
    public void UpdateFindings_Preserves_Overrides_Across_Yaml_Reload()
    {
        // RUBBER-DUCK HIGHEST-RISK TEST: the FindingsWatcher fires on every
        // findings.yaml write -- the agent typically rewrites the file
        // multiple times during a run as it adds/refines findings. A user's
        // saved edits must NOT vanish on those reloads.
        var (dir, cleanup) = FreshRunDir();
        try
        {
            var store = new ReviewRunStore();
            var run = MakeRun(dir);
            store.StartedRun(run);

            // Simulate first findings.yaml write + a user edit.
            store.UpdateFindings(run.PrUrl,
                DocWithFindings(("f01", "original body 01"), ("f02", "original body 02")),
                Array.Empty<string>());
            store.SetBodyOverride(run.PrUrl, "f01", "user-edited body");

            // Simulate a second findings.yaml write (e.g., agent added a finding).
            store.UpdateFindings(run.PrUrl,
                DocWithFindings(
                    ("f01", "original body 01"),
                    ("f02", "original body 02"),
                    ("f03", "new finding")),
                Array.Empty<string>());

            var got = store.Get(run.PrUrl);
            got!.BodyOverrides.Should().ContainKey("f01").WhoseValue.Should().Be("user-edited body",
                "the override survives the findings.yaml reparse");
            got.Findings!.Findings.Should().HaveCount(3, "the new finding is visible");
        }
        finally { cleanup(); }
    }

    [Fact]
    public void StartedRun_Loads_Existing_OverrideFile_From_Disk()
    {
        // Simulates a web restart: comment-overrides.json was written by a
        // prior process, and the same run dir is re-attached to a fresh
        // ReviewRunStore. Edits should reappear.
        var (dir, cleanup) = FreshRunDir();
        try
        {
            var fileJson = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["f01"] = "carried across restart",
            });
            File.WriteAllText(Path.Combine(dir, "comment-overrides.json"), fileJson);

            var store = new ReviewRunStore();
            store.StartedRun(MakeRun(dir));

            var got = store.Get("https://github.com/owner/repo/pull/1");
            got!.BodyOverrides.Should().ContainKey("f01").WhoseValue.Should().Be("carried across restart");
        }
        finally { cleanup(); }
    }

    [Fact]
    public void StartedRun_Tolerates_Corrupt_OverrideFile()
    {
        var (dir, cleanup) = FreshRunDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "comment-overrides.json"), "{ this is not :: json");

            var store = new ReviewRunStore();
            store.StartedRun(MakeRun(dir));

            var got = store.Get("https://github.com/owner/repo/pull/1");
            got!.BodyOverrides.Should().BeEmpty("a corrupt file is ignored, not fatal");

            // And we deliberately do NOT delete the bad file -- user may want to fix it.
            File.Exists(Path.Combine(dir, "comment-overrides.json")).Should().BeTrue();
        }
        finally { cleanup(); }
    }

    [Fact]
    public void SetBodyOverride_Raises_Changed()
    {
        var (dir, cleanup) = FreshRunDir();
        try
        {
            var store = new ReviewRunStore();
            store.StartedRun(MakeRun(dir));
            var fired = 0;
            store.Changed += () => Interlocked.Increment(ref fired);

            store.SetBodyOverride("https://github.com/owner/repo/pull/1", "f01", "edit");
            store.ClearBodyOverride("https://github.com/owner/repo/pull/1", "f01");

            fired.Should().Be(2);
        }
        finally { cleanup(); }
    }
}
