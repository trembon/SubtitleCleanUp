using System.Globalization;

namespace SubtitleCleanUp.Core.Services;

public sealed class IsoLanguageCatalog
{
    private readonly Dictionary<string, string> _codes = new(StringComparer.OrdinalIgnoreCase);

    public IsoLanguageCatalog()
    {
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            var alpha2 = culture.TwoLetterISOLanguageName;
            var alpha3 = culture.ThreeLetterISOLanguageName;
            if (alpha3.Length == 3)
            {
                _codes.TryAdd(alpha3, alpha3.ToLowerInvariant());
                if (alpha2.Length == 2)
                {
                    _codes.TryAdd(alpha2, alpha3.ToLowerInvariant());
                }
            }
        }

        Add("en", "eng");
        Add("sv", "swe");
        Add("hi", "hin");
        Add("de", "deu");
        Add("fr", "fra");
        Add("cs", "ces");
        Add("nl", "nld");
        Add("el", "ell");
        Add("ro", "ron");
        Add("sk", "slk");
        Add("is", "isl");
        Add("mk", "mkd");
        Add("sq", "sqi");
        Add("hy", "hye");
        Add("ka", "kat");
        Add("eu", "eus");
        Add("my", "mya");
        Add("zh", "zho");

        foreach (var (legacy, canonical) in new Dictionary<string, string>
        {
            ["ger"] = "deu",
            ["fre"] = "fra",
            ["cze"] = "ces",
            ["dut"] = "nld",
            ["gre"] = "ell",
            ["rum"] = "ron",
            ["slo"] = "slk",
            ["ice"] = "isl",
            ["mac"] = "mkd",
            ["alb"] = "sqi",
            ["arm"] = "hye",
            ["geo"] = "kat",
            ["baq"] = "eus",
            ["bur"] = "mya",
            ["chi"] = "zho"
        })
        {
            _codes[legacy] = canonical;
        }
    }

    public bool TryNormalize(string code, out string normalized) =>
        _codes.TryGetValue(code, out normalized!);

    public static string DisplayName(string code)
    {
        var culture = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .FirstOrDefault(x => x.ThreeLetterISOLanguageName.Equals(code, StringComparison.OrdinalIgnoreCase));
        return culture?.EnglishName ?? code;
    }

    private void Add(string alpha2, string alpha3)
    {
        _codes[alpha2] = alpha3;
        _codes[alpha3] = alpha3;
    }
}
