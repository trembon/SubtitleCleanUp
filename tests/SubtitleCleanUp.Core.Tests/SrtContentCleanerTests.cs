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
    [InlineData("""<FONT SIZE = '54' FACE = 'Song Font'>""", 1)]
    [InlineData("""<font size=54 color='#fff' face=SongFont>""", 1)]
    [InlineData("""<font face='Song Font' size=54>""", 1)]
    [InlineData("""<font face='Song Font' size=54px>""", 0)]
    [InlineData("""<font face="" size='54'>""", 0)]
    [InlineData("""<font face="Song Font" size='big'>""", 0)]
    [InlineData("""<font color='#fff'>""", 0)]
    public void Keeps_font_markup_and_only_flags_fragments_with_a_complete_style(string fontTag, int possibleFragments)
    {
        const string timing = "00:00:01,000 --> 00:00:02,000";
        var input = Cue(1, timing, fontTag + @"{\an5}Hi</font>");

        var result = SrtContentCleaner.Clean(Encoding.UTF8.GetBytes(input));

        result.PossibleKaraokeCues.ShouldBe(possibleFragments);
        Encoding.UTF8.GetString(result.CleanedBytes.ShouldNotBeNull())
            .ShouldBe(Cue(1, timing, fontTag + "Hi</font>"));
    }

    [Fact]
    public void Preserves_legacy_line_endings_and_timing_settings_at_end_of_file()
    {
        const string timing = " \t001:02:03,040\t-->\t001:02:04,060 x1:-20 Y2:32  ";
        var input = Encoding.UTF8.GetBytes($"\r9\r{timing}\rHello{{note}}");

        var result = SrtContentCleaner.Clean(input);

        Encoding.UTF8.GetString(result.CleanedBytes.ShouldNotBeNull()).ShouldBe($"1\r{timing}\rHello\r\r");
    }

    [Theory]
    [InlineData("m 0 0 s 1 1 2 2 3 3 p 4 4 c", "")]
    [InlineData("m 0 l 2 2", "m 0 l 2 2")]
    [InlineData("m 0 0 b 1 2 3 4", "m 0 0 b 1 2 3 4")]
    [InlineData("m 0 0 1 1", "m 0 0 1 1")]
    [InlineData("n 0 0 l 2 -.5Title", "Title")]
    public void Removes_only_valid_drawing_commands_and_keeps_attached_text(string body, string remaining)
    {
        const string timing = "00:00:00,000 --> 00:00:01,000";
        const string font = """<font size="54" face="Song Font">""";
        var input = Encoding.UTF8.GetBytes(Cue(1, timing, font + @"{\an5}" + body + "</font>"));

        var result = SrtContentCleaner.Clean(input);

        var expected = remaining.Length == 0 ? "" : Cue(1, timing, font + remaining + "</font>");
        Encoding.UTF8.GetString(result.CleanedBytes.ShouldNotBeNull()).ShouldBe(expected);
    }

    [Fact]
    public void Bounds_preview_examples_without_splitting_a_surrogate_pair()
    {
        const string timing = "00:00:00,000 --> 00:00:01,000";
        var first = new string('a', 239);
        var second = new string('b', 240);
        var input = Cue(1, timing, first + "😀{note}") + Cue(2, timing, second + "{note}");

        var result = SrtContentCleaner.Clean(Encoding.UTF8.GetBytes(input));

        result.Examples.Select(example => example.Before).ShouldBe([first + "…", second + "…"]);
        result.Examples[0].After.ShouldBe(first + "…");
        Encoding.UTF8.GetString(result.CleanedBytes.ShouldNotBeNull()).ShouldContain(first + "😀");
    }

    [Theory]
    [InlineData("not a cue\n00:00:00,000 --> 00:00:01,000\nText")]
    [InlineData("1\nbad timing\nText")]
    [InlineData("1\n00:00:00,000 --> 00:00:01,000 X1:-\nText")]
    [InlineData("1\n00:00:00,000 --> 00:00:01,000 X3:12\nText")]
    [InlineData("1\n00:00:00,000 --> 00:00:01,000 X1:12 extra\nText")]
    [InlineData("1\n00:00:00,000 --> 00:00:01,000X1:12\nText")]
    [InlineData("1\n00:00:00,000 -> 00:00:01,000\nText")]
    [InlineData("1\n2147483648:00:00,000 --> 2147483648:00:01,000\nText")]
    [InlineData("1\n00:00:02,000 --> 00:00:01,000\nText")]
    [InlineData("1\n00:61:00,000 --> 00:61:01,000\nText")]
    [InlineData("1\n00:00:00,000 --> 00:00:61,000\nText")]
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rejects_utf32_in_either_byte_order(bool bigEndian)
    {
        var encoding = new UTF32Encoding(bigEndian, byteOrderMark: true);
        var input = encoding.GetPreamble().Concat(encoding.GetBytes("1\n00:00:00,000 --> 00:00:01,000\nText")).ToArray();

        Should.Throw<InvalidDataException>(() => SrtContentCleaner.Clean(input)).Message.ShouldContain("UTF-32");
    }

    private static string Cue(int number, string timing, string body) => $"{number}\n{timing}\n{body}\n\n";
}
