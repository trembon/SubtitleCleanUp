using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SubtitleCleanUp.Core.Services;

public enum SrtCleanupReason
{
    BraceBlocks,
    DrawingData,
    KaraokeFragments,
    EmptyCues,
    OverlayDuplicates,
    PossibleKaraoke
}

public sealed record SrtCleanupExample(SrtCleanupReason Reason, string Timing, string Before, string After);

public sealed record SrtCleanupResult(
    byte[]? CleanedBytes,
    IReadOnlyDictionary<SrtCleanupReason, int> Counts,
    IReadOnlyList<SrtCleanupExample> Examples,
    int PossibleKaraokeCues);

public static partial class SrtContentCleaner
{
    public const int MaxFileBytes = 64 * 1024 * 1024;

    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly Encoding Utf16Le = new UnicodeEncoding(false, true, true);
    private static readonly Encoding Utf16Be = new UnicodeEncoding(true, true, true);
    private const string Markup = """</?(?:font|[bius])(?=[ \t/>])(?:[^<>"'\n]|"[^"\n]*"|'[^'\n]*')*>""";

    public static SrtCleanupResult Clean(byte[] source, CancellationToken cancellationToken = default)
    {
        if (source.Length > MaxFileBytes)
        {
            throw new InvalidDataException("The subtitle exceeds the 64 MiB cleanup limit.");
        }

        var (encoding, bomLength) = DetectEncoding(source);
        string text;
        try
        {
            text = encoding.GetString(source, bomLength, source.Length - bomLength);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("The subtitle must use UTF-8 or BOM-marked UTF-16.", ex);
        }

        if (text.Contains('\0'))
        {
            throw new InvalidDataException("The subtitle contains null characters or uses an unsupported encoding.");
        }

        var lineEnding = DominantLineEnding(text);
        var cues = Parse(text.Replace("\r\n", "\n").Replace('\r', '\n'), cancellationToken);
        var karaoke = FindKaraoke(cues);
        var possibleKaraoke = 0;
        var counts = new Dictionary<SrtCleanupReason, int>();
        var examples = new List<SrtCleanupExample>();
        var overlays = new HashSet<(long Start, long End, string Settings, int Alignment, string Visible)>();
        var surviving = new List<Cue>();

        foreach (var cue in cues)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SrtCleanupReason? removed = null;
            if (cue.Visible.Length == 0)
            {
                removed = SrtCleanupReason.EmptyCues;
            }
            else if (karaoke.Contains(cue))
            {
                removed = SrtCleanupReason.KaraokeFragments;
            }
            else if (cue.Alignment != 0 && cue.HasFont &&
                     !overlays.Add((cue.Start, cue.End, cue.Settings, cue.Alignment, cue.Visible)))
            {
                removed = SrtCleanupReason.OverlayDuplicates;
            }

            if (cue.HadBraces)
            {
                Record(SrtCleanupReason.BraceBlocks, cue, removed is not null);
            }
            if (cue.HadDrawing)
            {
                Record(SrtCleanupReason.DrawingData, cue, removed is not null);
            }
            if (removed is { } reason)
            {
                Record(reason, cue, true);
                continue;
            }

            if (cue.FragmentStyle is not null)
            {
                if (++possibleKaraoke <= 2)
                {
                    examples.Add(new(SrtCleanupReason.PossibleKaraoke, Abbreviate(cue.Timing),
                        Abbreviate(cue.Original), Abbreviate(cue.Cleaned)));
                }
            }
            surviving.Add(cue);
        }

        byte[]? bytes = null;
        if (counts.Count > 0)
        {
            var output = new StringBuilder();
            for (var i = 0; i < surviving.Count; i++)
            {
                var cue = surviving[i];
                output.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(lineEnding)
                    .Append(cue.Timing).Append(lineEnding)
                    .Append(cue.Cleaned.Replace("\n", lineEnding)).Append(lineEnding).Append(lineEnding);
            }
            var cleaned = output.ToString();
            bytes = new byte[bomLength + encoding.GetByteCount(cleaned)];
            source.AsSpan(0, bomLength).CopyTo(bytes);
            encoding.GetBytes(cleaned, bytes.AsSpan(bomLength));
            if (bytes.AsSpan().SequenceEqual(source))
            {
                bytes = null;
            }
        }
        return new SrtCleanupResult(bytes, counts, examples, possibleKaraoke);

        void Record(SrtCleanupReason reason, Cue cue, bool removed)
        {
            counts.TryGetValue(reason, out var count);
            counts[reason] = count + 1;
            if (count < 2)
            {
                examples.Add(new(reason, Abbreviate(cue.Timing), Abbreviate(cue.Original),
                    removed ? "(cue removed)" : Abbreviate(cue.Cleaned)));
            }
        }
    }

    private static (Encoding Encoding, int BomLength) DetectEncoding(byte[] source)
    {
        var bytes = source.AsSpan();
        if (bytes.StartsWith(Encoding.UTF32.Preamble) || bytes.StartsWith(new byte[] { 0, 0, 254, 255 }))
        {
            throw new InvalidDataException("UTF-32 subtitles are unsupported.");
        }
        if (bytes.StartsWith(Encoding.UTF8.Preamble))
        {
            return (Utf8, 3);
        }
        if (bytes.StartsWith(Utf16Le.Preamble))
        {
            return (Utf16Le, 2);
        }
        if (bytes.StartsWith(Utf16Be.Preamble))
        {
            return (Utf16Be, 2);
        }
        return (Utf8, 0);
    }

    private static string DominantLineEnding(string text)
    {
        var crlf = 0;
        var lf = 0;
        var cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                crlf++;
                i++;
            }
            else if (text[i] == '\n')
            {
                lf++;
            }
            else if (text[i] == '\r')
            {
                cr++;
            }
        }
        return crlf > lf && crlf >= cr ? "\r\n" : cr > lf ? "\r" : "\n";
    }

    private static List<Cue> Parse(string text, CancellationToken cancellationToken)
    {
        var cues = new List<Cue>();
        var offset = 0;
        while (offset < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var number = ReadLine(text, ref offset).Trim();
            if (number.IsEmpty)
            {
                continue;
            }
            if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                throw new InvalidDataException($"Expected an SRT cue number at block {cues.Count + 1}.");
            }

            var timing = ReadLine(text, ref offset).ToString();
            var match = TimingPattern().Match(timing);
            if (!match.Success || !TryReadTime(match.Groups["start"].ValueSpan, out var start) ||
                !TryReadTime(match.Groups["end"].ValueSpan, out var end) || end < start)
            {
                throw new InvalidDataException($"Invalid SRT timing at block {cues.Count + 1}.");
            }

            var bodyStart = offset;
            var bodyEnd = offset;
            var previousWasNumber = false;
            while (offset < text.Length)
            {
                var line = ReadLine(text, ref offset).Trim();
                if (line.IsEmpty)
                {
                    break;
                }
                if (previousWasNumber && TimingPattern().IsMatch(line.ToString()))
                {
                    throw new InvalidDataException($"Missing SRT cue separator at block {cues.Count + 1}.");
                }
                previousWasNumber = int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out _);
                bodyEnd = offset;
            }
            var body = text.AsSpan(bodyStart, bodyEnd - bodyStart).TrimEnd('\n').ToString();
            cues.Add(CleanCue(timing, match.Groups["settings"].Value.Trim(), start, end, body));
        }
        return cues;
    }

    private static ReadOnlySpan<char> ReadLine(string text, ref int offset)
    {
        var start = offset;
        var next = text.IndexOf('\n', start);
        offset = next < 0 ? text.Length : next + 1;
        return text.AsSpan(start, (next < 0 ? text.Length : next) - start);
    }

    private static bool TryReadTime(ReadOnlySpan<char> value, out long time)
    {
        var colon = value.IndexOf(':');
        time = 0;
        if (!int.TryParse(value[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out var hours))
        {
            return false;
        }
        var minutes = int.Parse(value.Slice(colon + 1, 2), CultureInfo.InvariantCulture);
        var seconds = int.Parse(value.Slice(colon + 4, 2), CultureInfo.InvariantCulture);
        var milliseconds = int.Parse(value.Slice(colon + 7, 3), CultureInfo.InvariantCulture);
        time = ((hours * 60L + minutes) * 60 + seconds) * 1000 + milliseconds;
        return minutes < 60 && seconds < 60;
    }

    private static Cue CleanCue(string timing, string settings, long start, long end, string body)
    {
        var cleaned = new StringBuilder(body.Length);
        var hadBraces = false;
        var hadDrawing = false;
        var hasFont = false;
        string? fontStyle = null;
        var alignment = 0;
        var braceDepth = 0;
        var braceStart = 0;
        var offset = 0;
        foreach (var token in CueTokenPattern().EnumerateMatches(body))
        {
            var value = body.AsSpan(token.Index, token.Length);
            if (value[0] == '{')
            {
                if (braceDepth++ == 0)
                {
                    braceStart = token.Index;
                }
                continue;
            }

            if (value[0] == '}')
            {
                if (braceDepth > 0 && --braceDepth == 0)
                {
                    AppendText(body.AsSpan(offset, braceStart - offset));
                    offset = token.Index + 1;
                    hadBraces = true;
                    var block = body.AsSpan(braceStart, offset - braceStart);
                    if (block.StartsWith(@"{\"))
                    {
                        foreach (var command in OverridePattern().EnumerateMatches(block))
                        {
                            alignment = block[command.Index + 3] - '0';
                        }
                    }
                }
                continue;
            }

            if (braceDepth == 0)
            {
                AppendText(body.AsSpan(offset, token.Index - offset));
                offset = token.Index + token.Length;
                cleaned.Append(value);
                if (value.StartsWith("<font", StringComparison.OrdinalIgnoreCase))
                {
                    hasFont = true;
                    if (fontStyle is null)
                    {
                        var tag = value.ToString();
                        var face = FontFacePattern().Match(tag);
                        var size = FontSizePattern().Match(tag);
                        if (face.Success && size.Success)
                        {
                            fontStyle = $"{face.Groups["face"].Value.ToUpperInvariant()}\0{size.Groups["size"].Value}";
                        }
                    }
                }
            }
        }
        AppendText(body.AsSpan(offset));

        var result = hadBraces || hadDrawing
            ? BlankLinePattern().Replace(EmptyFontPattern().Replace(cleaned.ToString(), "").Trim(), "\n")
            : body;
        var visible = WebUtility.HtmlDecode(MarkupPattern().Replace(result, "")).Trim();
        string? fragmentStyle = null;
        if (alignment != 0 && fontStyle is not null && IsFragment(visible) && !EmphasisPattern().IsMatch(body))
        {
            fragmentStyle = $"{fontStyle}\0{alignment}";
        }
        return new Cue(timing, settings, start, end, body, result, visible, alignment, hasFont,
            fragmentStyle, hadBraces, hadDrawing);

        void AppendText(ReadOnlySpan<char> run)
        {
            if (RemoveDrawing(run))
            {
                return;
            }
            if (!run.Contains('\n'))
            {
                cleaned.Append(run);
                return;
            }

            foreach (var line in run.Split('\n'))
            {
                if (!RemoveDrawing(run[line]))
                {
                    cleaned.Append(run[line]);
                }
                if (line.End.GetOffset(run.Length) < run.Length)
                {
                    cleaned.Append('\n');
                }
            }
        }

        bool RemoveDrawing(ReadOnlySpan<char> run)
        {
            var candidate = run.TrimStart();
            var drawingLength = DrawingLength(candidate.TrimEnd(), hasFont && alignment != 0);
            if (drawingLength == 0)
            {
                return false;
            }
            hadDrawing = true;
            cleaned.Append(candidate[drawingLength..]);
            return true;
        }
    }

    private static int DrawingLength(ReadOnlySpan<char> text, bool allowAttachedText)
    {
        if (text.Length < 9 || text[0] is not ('m' or 'n') || !char.IsWhiteSpace(text[1]))
        {
            return 0;
        }
        var totalLength = text.Length;
        var command = '\0';
        var arguments = 0;
        var hadSegment = false;
        while (!text.IsEmpty)
        {
            var offset = totalLength - text.Length;
            var end = text.IndexOfAny(' ', '\t', '\n');
            var token = end < 0 ? text : text[..end];
            text = end < 0 ? [] : text[end..].TrimStart();
            if (token.Length == 1 && "mnlbspc".Contains(token[0]))
            {
                if (command != '\0' && !ValidArguments(command, arguments))
                {
                    return 0;
                }
                hadSegment |= command != '\0';
                command = token[0];
                arguments = 0;
            }
            else if (IsCoordinate(token))
            {
                arguments++;
            }
            else
            {
                // A converted karaoke layer can append its text directly to the final coordinate.
                if (allowAttachedText && hadSegment && ValidArguments(command, arguments + 1))
                {
                    var suffixStart = CoordinatePrefixLength(token);
                    if (suffixStart > 0 && suffixStart < token.Length && IsCoordinate(token[..suffixStart]))
                    {
                        return offset + suffixStart;
                    }
                }
                return 0;
            }
        }
        return hadSegment && ValidArguments(command, arguments) ? totalLength : 0;

        static bool IsCoordinate(ReadOnlySpan<char> value) => decimal.TryParse(value,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _);
        static bool ValidArguments(char command, int count) => command switch
        {
            'b' => count >= 6 && count % 6 == 0,
            's' => count >= 6 && count % 2 == 0,
            'c' => count == 0,
            _ => count >= 2 && count % 2 == 0
        };
    }

    private static int CoordinatePrefixLength(ReadOnlySpan<char> value)
    {
        var offset = value[0] is '+' or '-' ? 1 : 0;
        while (offset < value.Length && char.IsAsciiDigit(value[offset]))
        {
            offset++;
        }
        if (offset + 1 < value.Length && value[offset] == '.' && char.IsAsciiDigit(value[offset + 1]))
        {
            offset += 2;
            while (offset < value.Length && char.IsAsciiDigit(value[offset]))
            {
                offset++;
            }
        }
        return offset;
    }

    private static bool IsFragment(string text)
    {
        var characters = StringInfo.GetTextElementEnumerator(text);
        var count = 0;
        while (characters.MoveNext())
        {
            if (++count > 6)
            {
                return false;
            }
        }
        return count > 0 && FragmentPattern().IsMatch(text);
    }

    private static HashSet<Cue> FindKaraoke(IEnumerable<Cue> cues)
    {
        var removals = new HashSet<Cue>(ReferenceEqualityComparer.Instance);
        foreach (var style in cues.Where(c => c.FragmentStyle is not null).GroupBy(c => c.FragmentStyle))
        {
            var cluster = new List<Cue>();
            var clusterEnd = -1001L;
            foreach (var cue in style.OrderBy(c => c.Start))
            {
                if (cue.Start > clusterEnd + 1000)
                {
                    MarkCluster(cluster, removals);
                    cluster.Clear();
                }
                cluster.Add(cue);
                clusterEnd = Math.Max(clusterEnd, cue.End);
            }
            MarkCluster(cluster, removals);
        }
        return removals;
    }

    private static void MarkCluster(List<Cue> cluster, HashSet<Cue> removals)
    {
        if (cluster.Count >= 20 &&
            cluster.Select(c => c.Visible).Distinct(StringComparer.Ordinal).Take(4).Count() == 4 &&
            cluster.GroupBy(c => (c.Visible, c.Start, c.End)).Where(g => g.Skip(1).Any()).Take(4).Count() == 4)
        {
            removals.UnionWith(cluster);
        }
    }

    private static string Abbreviate(string text) =>
        text.Length <= 240 ? text : text[..(char.IsHighSurrogate(text[239]) ? 239 : 240)] + "…";

    [GeneratedRegex(@"^\s*(?<start>[0-9]{2,}:[0-9]{2}:[0-9]{2},[0-9]{3})[ \t]*-->[ \t]*(?<end>[0-9]{2,}:[0-9]{2}:[0-9]{2},[0-9]{3})(?<settings>[ \t]+(?:(?:[XY][12]:-?[0-9]+)[ \t]*)*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TimingPattern();

    [GeneratedRegex(Markup + @"|[{}]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CueTokenPattern();

    [GeneratedRegex(@"\\an[1-9](?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OverridePattern();

    [GeneratedRegex("""[ \t]face[ \t]*=[ \t]*(?:"(?<face>[^"]+)"|'(?<face>[^']+)'|(?<face>[^ \t"'/>]+))""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FontFacePattern();

    [GeneratedRegex("""[ \t]size[ \t]*=[ \t]*(?:"(?<size>[0-9]+)"|'(?<size>[0-9]+)'|(?<size>[0-9]+)(?=[ \t/>]))""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FontSizePattern();

    [GeneratedRegex(Markup, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MarkupPattern();

    [GeneratedRegex(@"<font\b[^<>\n]*>[ \t]*</font>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmptyFontPattern();

    [GeneratedRegex(@"\n(?:[ \t]*\n)+", RegexOptions.CultureInvariant)]
    private static partial Regex BlankLinePattern();

    [GeneratedRegex(@"<[bius](?:\s|>)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmphasisPattern();

    [GeneratedRegex(@"\A[\p{L}\p{M}\p{P}]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex FragmentPattern();

    private sealed record Cue(string Timing, string Settings, long Start, long End, string Original, string Cleaned,
        string Visible, int Alignment, bool HasFont, string? FragmentStyle, bool HadBraces, bool HadDrawing);
}
