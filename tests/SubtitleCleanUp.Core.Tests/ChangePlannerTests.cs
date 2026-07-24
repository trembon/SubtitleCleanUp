using Shouldly;
using SubtitleCleanUp.Core.Models;
using SubtitleCleanUp.Core.Services;

namespace SubtitleCleanUp.Core.Tests;

public sealed class ChangePlannerTests
{
    private readonly ChangePlanner _planner = new();

    [Fact]
    public void CreatePlan_recommends_largest_duplicate()
    {
        var small = File("Movie.en.srt", 100, "A", isCanonical: false);
        var large = File("Movie.eng.srt", 250, "B", isCanonical: true);

        var result = _planner.CreatePlan(new SubtitleDiscovery([small, large], []));

        var proposal = result.ShouldHaveSingleItem();
        proposal.Kind.ShouldBe(ProposalKind.Duplicate);
        proposal.Files[proposal.RecommendedIndex].FileName.ShouldBe("Movie.eng.srt");
        proposal.CanonicalPath.ShouldEndWith("Movie.eng.srt");
    }

    [Fact]
    public void CreatePlan_uses_canonical_name_to_break_size_tie()
    {
        var noncanonical = File("Movie.en.srt", 100, "A", isCanonical: false);
        var canonical = File("Movie.eng.srt", 100, "B", isCanonical: true);

        var proposal = _planner.CreatePlan(new SubtitleDiscovery([noncanonical, canonical], []))
            .ShouldHaveSingleItem();

        proposal.Files[proposal.RecommendedIndex].ShouldBe(canonical);
    }

    [Fact]
    public void CreatePlan_does_not_propose_an_already_canonical_single_file()
    {
        var result = _planner.CreatePlan(new SubtitleDiscovery(
            [File("Movie.eng.srt", 100, "A", isCanonical: true)],
            []));

        result.ShouldBeEmpty();
    }

    [Fact]
    public void CreatePlan_creates_manual_review_for_unrecognized_file()
    {
        var file = File("Mystery.srt", 50, "A", issue: "No matching video.");

        var proposal = _planner.CreatePlan(new SubtitleDiscovery([file], [])).ShouldHaveSingleItem();

        proposal.Kind.ShouldBe(ProposalKind.ManualReview);
        proposal.CanonicalPath.ShouldBeNull();
        proposal.Reason.ShouldBe("No matching video.");
    }

    [Fact]
    public void CreatePlan_keeps_sdh_separate_from_default_subtitles()
    {
        var normal = File("Movie.en.srt", 100, "A", variant: null);
        var sdh = File("Movie.en.hi.srt", 120, "B", variant: "sdh");

        var proposals = _planner.CreatePlan(new SubtitleDiscovery([normal, sdh], []));

        proposals.Count.ShouldBe(2);
        proposals.ShouldAllBe(x => x.Kind == ProposalKind.Rename);
    }

    private static DiscoveredSubtitle File(
        string fileName,
        long size,
        string hash,
        bool isCanonical = false,
        string? variant = null,
        string? issue = null)
    {
        var fullPath = Path.Combine(Path.GetTempPath(), fileName);
        return new DiscoveredSubtitle(
            "media",
            Path.GetTempPath(),
            fullPath,
            fileName,
            fileName,
            fileName.StartsWith("Mystery") ? null : "Movie",
            issue is null ? "eng" : null,
            variant,
            issue is null ? Path.Combine(Path.GetTempPath(), variant is null ? "Movie.eng.srt" : "Movie.eng.sdh.srt") : null,
            isCanonical,
            new SubtitleFingerprint(size, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), hash),
            issue);
    }
}
