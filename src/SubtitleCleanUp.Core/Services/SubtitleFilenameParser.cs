using System.Text.RegularExpressions;
using SubtitleCleanUp.Core.Abstractions;
using SubtitleCleanUp.Core.Models;

namespace SubtitleCleanUp.Core.Services;

public sealed partial class SubtitleFilenameParser(IsoLanguageCatalog languages) : ISubtitleFilenameParser
{
    private static readonly HashSet<string> Variants = new(
        ["forced", "sdh", "hi", "cc", "commentary", "signs", "foreign"],
        StringComparer.OrdinalIgnoreCase);

    public ParsedSubtitleName? Parse(string subtitleFileName, IReadOnlyCollection<string> mediaStems)
    {
        if (!subtitleFileName.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var subtitleStem = Path.GetFileNameWithoutExtension(subtitleFileName);
        var candidates = mediaStems
            .Where(stem => subtitleStem.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Length)
            .ToArray();

        if (candidates.Length == 0 || (candidates.Length > 1 && candidates[0].Length == candidates[1].Length))
        {
            return null;
        }

        var mediaStem = candidates[0];
        var suffix = subtitleStem[(mediaStem.Length + 1)..];
        var tokens = suffix.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (tokens.Count == 0)
        {
            return null;
        }

        if (tokens.Count > 1 && tokens[^1].All(char.IsDigit))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        if (tokens.Count is < 1 or > 2)
        {
            return null;
        }

        tokens[^1] = TrailingNumber().Replace(tokens[^1], string.Empty);
        if (tokens[^1].Length == 0 || !languages.TryNormalize(tokens[0], out var language))
        {
            return null;
        }

        string? variant = null;
        if (tokens.Count == 2)
        {
            if (!Variants.Contains(tokens[1]))
            {
                return null;
            }

            variant = tokens[1].Equals("hi", StringComparison.OrdinalIgnoreCase)
                ? "sdh"
                : tokens[1].ToLowerInvariant();
        }

        var canonical = $"{mediaStem}.{language}{(variant is null ? string.Empty : "." + variant)}.srt";
        return new ParsedSubtitleName(
            mediaStem,
            language,
            variant,
            canonical,
            string.Equals(subtitleFileName, canonical, StringComparison.Ordinal));
    }

    public ManualSubtitleName? Analyze(string subtitleFileName, IReadOnlyCollection<string> mediaStems)
    {
        if (!subtitleFileName.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var subtitleStem = Path.GetFileNameWithoutExtension(subtitleFileName);
        var candidates = mediaStems
            .Where(stem => string.Equals(subtitleStem, stem, StringComparison.OrdinalIgnoreCase) ||
                          subtitleStem.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Length)
            .ToArray();

        if (candidates.Length == 0 || (candidates.Length > 1 && candidates[0].Length == candidates[1].Length))
        {
            return null;
        }

        var mediaStem = candidates[0];
        var suffix = subtitleStem.Length == mediaStem.Length
            ? string.Empty
            : subtitleStem[(mediaStem.Length + 1)..];
        var tokens = suffix.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? language = null;
        string? variant = null;

        foreach (var token in tokens)
        {
            var candidate = TrailingNoise().Replace(token, string.Empty);
            if (language is null && languages.TryNormalize(candidate, out var normalized))
            {
                language = normalized;
                continue;
            }

            if (variant is null && Variants.Contains(candidate))
            {
                variant = candidate.Equals("hi", StringComparison.OrdinalIgnoreCase)
                    ? "sdh"
                    : candidate.ToLowerInvariant();
            }
        }

        var canonical = language is null
            ? null
            : $"{mediaStem}.{language}{(variant is null ? string.Empty : "." + variant)}.srt";
        return new ManualSubtitleName(mediaStem, language, variant, canonical);
    }

    [GeneratedRegex(@"\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingNumber();

    [GeneratedRegex(@"(?:\(\d+\)|\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingNoise();
}
