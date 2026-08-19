using Shouldly;
using SubtitleCleanUp.Core.Services;

namespace SubtitleCleanUp.Core.Tests;

public sealed class SubtitleFilenameParserTests
{
    private readonly SubtitleFilenameParser _parser = new(new IsoLanguageCatalog());
    private static readonly string[] Media = ["Movie"];

    [Theory]
    [InlineData("Movie.en.srt", "eng", null, "Movie.eng.srt", false)]
    [InlineData("Movie.eng.srt", "eng", null, "Movie.eng.srt", true)]
    [InlineData("Movie.sv.srt", "swe", null, "Movie.swe.srt", false)]
    [InlineData("Movie.en2.srt", "eng", null, "Movie.eng.srt", false)]
    [InlineData("Movie.eng.2.srt", "eng", null, "Movie.eng.srt", false)]
    [InlineData("Movie.en.hi.srt", "eng", "sdh", "Movie.eng.sdh.srt", false)]
    [InlineData("Movie.EN.HI2.SRT", "eng", "sdh", "Movie.eng.sdh.srt", false)]
    [InlineData("Movie.eng.sdh.srt", "eng", "sdh", "Movie.eng.sdh.srt", true)]
    [InlineData("Movie.hi.srt", "hin", null, "Movie.hin.srt", false)]
    [InlineData("Movie.fr.forced.srt", "fra", "forced", "Movie.fra.forced.srt", false)]
    [InlineData("Movie.ger.srt", "deu", null, "Movie.deu.srt", false)]
    public void Parse_normalizes_supported_suffixes(
        string fileName,
        string language,
        string? variant,
        string canonical,
        bool isCanonical)
    {
        var result = _parser.Parse(fileName, Media);

        result.ShouldNotBeNull();
        result.Language.ShouldBe(language);
        result.Variant.ShouldBe(variant);
        result.CanonicalFileName.ShouldBe(canonical);
        result.IsCanonical.ShouldBe(isCanonical);
    }

    [Theory]
    [InlineData("Movie.srt")]
    [InlineData("Movie.zz.srt")]
    [InlineData("Movie.en.unknown.srt")]
    [InlineData("Other.en.srt")]
    [InlineData("Movie.en.forced.extra.srt")]
    [InlineData("Movie.en.ass")]
    public void Parse_rejects_unsupported_or_unassociated_names(string fileName)
    {
        _parser.Parse(fileName, Media).ShouldBeNull();
    }

    [Fact]
    public void Parse_uses_the_longest_matching_media_stem()
    {
        var result = _parser.Parse("A.Movie.en.srt", ["A", "A.Movie"]);

        result.ShouldNotBeNull();
        result.MediaStem.ShouldBe("A.Movie");
        result.CanonicalFileName.ShouldBe("A.Movie.eng.srt");
    }

    [Theory]
    [InlineData("file.17.srt", null, null)]
    [InlineData("file.2.es.srt", "spa", "file.spa.srt")]
    [InlineData("file.srt", null, null)]
    [InlineData("file..eng(2).srt", "eng", "file.eng.srt")]
    public void Analyze_recovers_language_from_manual_names(
        string fileName,
        string? language,
        string? canonical)
    {
        var result = _parser.Analyze(fileName, ["file"]);

        result.ShouldNotBeNull();
        result.Language.ShouldBe(language);
        result.CanonicalFileName.ShouldBe(canonical);
    }

    [Theory]
    [InlineData("file.ass")]
    [InlineData("file.srt")]
    public void Analyze_handles_unsupported_or_language_free_names(string fileName)
    {
        var result = _parser.Analyze(fileName, ["file"]);

        if (fileName.EndsWith(".ass", StringComparison.OrdinalIgnoreCase))
        {
            result.ShouldBeNull();
        }
        else
        {
            result.ShouldNotBeNull();
            result.Language.ShouldBeNull();
            result.CanonicalFileName.ShouldBeNull();
        }
    }

    [Fact]
    public void Analyze_normalizes_variant_and_supports_exact_media_stem()
    {
        var result = _parser.Analyze("Movie.sdh.srt", ["Movie"]);

        result.ShouldNotBeNull();
        result.MediaStem.ShouldBe("Movie");
        result.Variant.ShouldBe("sdh");
        result.CanonicalFileName.ShouldBeNull();
    }

    [Fact]
    public void Analyze_rejects_missing_or_ambiguous_media_stems()
    {
        _parser.Analyze("Other.en.srt", Media).ShouldBeNull();
        _parser.Analyze("A.en.srt", ["A", "A"]).ShouldBeNull();
    }
}
