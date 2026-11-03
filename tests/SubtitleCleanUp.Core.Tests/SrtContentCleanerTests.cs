using System.Text;
using Shouldly;
using SubtitleCleanUp.Core.Services;

namespace SubtitleCleanUp.Core.Tests;

public sealed class SrtContentCleanerTests
{
    [Fact]
    public void Cleans_converted_episode_cues_and_preserves_readable_text_and_timing()
    {
        const string drawing = "m 0 0 l 0 -49 b 3 -12 2 0 49 0";
        var cues = new List<string>
        {
            Cue(1, "00:01:57,277 --> 00:02:00,487",
                """<font face="Gandhi Sans" size="75"><b>expand our horizons more. {editor note}</b></font>"""),
            Cue(2, "00:02:06,277 --> 00:02:06,407", drawing),
            Cue(3, "00:02:06,277 --> 00:02:06,407",
                """<font face="Song Font" size="54">{\an5}<font color="#fff">""" + drawing + "</font>♪</font>"),
            Cue(4, "00:02:06,277 --> 00:02:07,407",
                """<font face="Song Font" size="54">{\an5}♪ A readable lyric line ♪</font>"""),
            Cue(5, "00:02:06,277 --> 00:02:07,407",
                """<font face="Song Font" size="54">{\an5}<i>Yes!</i></font>"""),
            Cue(6, "00:02:06,277 --> 00:02:07,407",
                """<font face="Song Font" size="54">{\an5}+101 XP</font>"""),
            Cue(7, "00:03:38,037 --> 00:03:40,837",
                """<font face="Sign Font" size="72">{=464}{\an4}<b>We Went Looking for a Mandragora</b></font>"""),
            Cue(8, "00:03:38,037 --> 00:03:40,837",
                """<font face="Sign Font" size="72">{\an4}<font color="#fff">We Went Looking for a Mandragora</font></font>"""),
            Cue(9, "00:10:00,000 --> 00:10:02,000", "<i>日本語 / café / ☀️</i>"),
            Cue(10, "00:11:00,000 --> 00:11:02,000", "First line\n{note {nested}}\nSecond line"),
            Cue(11, "00:12:00,000 --> 00:12:02,000", "A literal {unclosed brace"),
            Cue(12, "00:02:06,277 --> 00:02:07,407",
                """<font size="54" face="Song Font">{\an5}""" + drawing + "The whole lyric stays</font>"),
            Cue(13, "00:12:00,000 --> 00:12:02,000", drawing + "\nDialogue on the next line.")
        };
        for (var i = 0; i < 20; i++)
        {
            var glyph = "abc-"[i % 4];
            cues.Add(Cue(i + 14, "00:02:06,277 --> 00:02:06,407",
                $$"""<font face="Song Font" size="54">{\an5}{{drawing}}{{glyph}}</font>"""));
        }

        var result = SrtContentCleaner.Clean(Encoding.UTF8.GetBytes(string.Concat(cues)));
        var cleaned = Encoding.UTF8.GetString(result.CleanedBytes.ShouldNotBeNull());
        result.Counts[SrtCleanupReason.DrawingData].ShouldBe(24);
        result.Counts[SrtCleanupReason.KaraokeFragments].ShouldBe(20);
        result.Counts[SrtCleanupReason.OverlayDuplicates].ShouldBe(1);
        result.Counts[SrtCleanupReason.EmptyCues].ShouldBe(1);
        cleaned.ShouldNotContain(drawing);
        cleaned.ShouldNotContain("editor note");
        cleaned.ShouldNotContain(@"{\an");
        cleaned.ShouldContain("00:01:57,277 --> 00:02:00,487\n<font face=");
        cleaned.ShouldContain("expand our horizons more.");
        cleaned.ShouldContain(">♪</font>");
        cleaned.ShouldContain("♪ A readable lyric line ♪");
        cleaned.ShouldContain(">The whole lyric stays</font>");
        cleaned.ShouldContain("<i>Yes!</i>");
        cleaned.ShouldContain("+101 XP");
        cleaned.ShouldContain("00:03:38,037 --> 00:03:40,837");
        cleaned.Split("We Went Looking for a Mandragora").Length.ShouldBe(2);
        cleaned.ShouldContain("<i>日本語 / café / ☀️</i>");
        cleaned.ShouldContain("First line\nSecond line");
        cleaned.ShouldContain("Dialogue on the next line.");
        cleaned.ShouldContain("{unclosed brace");
        SrtContentCleaner.Clean(result.CleanedBytes!).CleanedBytes.ShouldBeNull();
    }

    [Fact]
    public void Keeps_short_unconfirmed_fragments_and_plain_repeated_dialogue()
    {
        const string timing = "00:00:00,000 --> 00:00:01,000";
        var source = Cue(1, timing, """<font face="Ordinary Font" size="50">{\an5}No!</font>""") +
            Cue(2, timing, "No!") + Cue(3, timing, "No!");

        var result = SrtContentCleaner.Clean(Encoding.UTF8.GetBytes(source));
        result.PossibleKaraokeCues.ShouldBe(1);
        Encoding.UTF8.GetString(result.CleanedBytes.ShouldNotBeNull()).Split("No!").Length.ShouldBe(4);
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    [InlineData("utf-16BE")]
    public void Preserves_byte_order_mark_and_dominant_line_ending(string encodingName)
    {
        var encoding = Encoding.GetEncoding(encodingName);
        const string timing = "00:00:00,000 --> 00:00:01,000 X1:1 X2:2 Y1:3 Y2:4";
        var unchanged = encoding.GetPreamble().Concat(encoding.GetBytes($"4\r\n{timing}\r\nCafé\r\n\r\n")).ToArray();
        var original = encoding.GetPreamble().Concat(encoding.GetBytes($"4\r\n{timing}\r\nCafé{{note}}\r\n\r\n")).ToArray();
        var expected = encoding.GetPreamble().Concat(encoding.GetBytes($"1\r\n{timing}\r\nCafé\r\n\r\n")).ToArray();

        SrtContentCleaner.Clean(unchanged).CleanedBytes.ShouldBeNull();
        SrtContentCleaner.Clean(original).CleanedBytes.ShouldBe(expected);
    }

    [Theory]
    [InlineData("1\nbad timing\nText")]
    [InlineData("1\n00:00:02,000 --> 00:00:01,000\nText")]
    [InlineData("1\n00:61:00,000 --> 00:61:01,000\nText")]
    [InlineData("1\n00:00:00,000 --> 00:00:01,000\nText\n2\n00:00:02,000 --> 00:00:03,000\nText")]
    public void Rejects_malformed_cues(string subtitle)
    {
        Should.Throw<InvalidDataException>(() => SrtContentCleaner.Clean(Encoding.UTF8.GetBytes(subtitle)));
    }

    [Fact]
    public void Rejects_ambiguous_encoding()
    {
        Should.Throw<InvalidDataException>(() => SrtContentCleaner.Clean([0xFF, 0xFF]));
        Should.Throw<InvalidDataException>(() => SrtContentCleaner.Clean(Encoding.Unicode.GetBytes("1\n")));
    }

    private static string Cue(int number, string timing, string body) => $"{number}\n{timing}\n{body}\n\n";
}
