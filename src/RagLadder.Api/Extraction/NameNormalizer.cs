using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RagLadder.Api.Configuration;

namespace RagLadder.Api.Extraction;

/// <summary>
/// Domain-specific name handling for entity resolution (spec §6.4 rules 3, 4 and 7).
/// Generic lowercase-and-compare loses to this domain immediately: "Thaw, The" and "The Thaw" are
/// the same title, "Part II" and "Part 2" are the same film, and "Bob"/"Robert" are the same person.
/// </summary>
public sealed partial class NameNormalizer
{
    private readonly DomainOptions _options;
    private readonly Dictionary<string, string> _diminutives;

    public NameNormalizer(DomainOptions options)
    {
        _options = options;
        _diminutives = LoadDiminutives(options.DiminutivesPath);
    }

    public IReadOnlyDictionary<string, string> Diminutives => _diminutives;

    // ----- rule 3: titles -------------------------------------------------

    [GeneratedRegex(@"^\s*(.*?),\s*(The|A|An)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingArticle();

    [GeneratedRegex(@"\b(Part|Chapter|Volume|Vol\.?)\s+([IVXLC]+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RomanNumeralPart();

    public string NormalizeTitle(string title)
    {
        var text = Fold(title);

        var trailing = TrailingArticle().Match(text);
        if (trailing.Success) text = trailing.Groups[1].Value;

        foreach (var article in _options.TitleArticles)
        {
            var prefix = article.ToLowerInvariant() + " ";
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                text = text[prefix.Length..];
                break;
            }
        }

        text = RomanNumeralPart().Replace(text, m => $"{m.Groups[1].Value} {RomanToArabic(m.Groups[2].Value)}");
        text = Regex.Replace(text, @"[:\-–—]+", " ");
        return Collapse(text);
    }

    // ----- rule 4: people -------------------------------------------------

    private static readonly string[] Suffixes = ["jr", "sr", "ii", "iii", "iv"];

    public string NormalizePerson(string name)
    {
        var text = Fold(name);
        text = Regex.Replace(text, @"[^\p{L}\p{Nd}\s\.]", " ");

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !Suffixes.Contains(p.TrimEnd('.')))
            .Select(p => _diminutives.TryGetValue(p.TrimEnd('.'), out var canonical) ? canonical : p)
            .ToArray();

        return Collapse(string.Join(' ', parts));
    }

    /// <summary>
    /// "J. R. Vance" and "James Robert Vance" are the same person when the initials line up and
    /// the surname matches. Two different people sharing a surname will not satisfy both.
    /// </summary>
    public bool InitialsCompatible(string left, string right)
    {
        var a = NormalizePerson(left).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var b = NormalizePerson(right).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (a.Length < 2 || b.Length < 2) return false;
        if (a[^1] != b[^1]) return false;
        if (a.Length != b.Length) return false;

        var sawInitial = false;
        for (var i = 0; i < a.Length - 1; i++)
        {
            var x = a[i].TrimEnd('.');
            var y = b[i].TrimEnd('.');
            if (x == y) continue;
            if (x.Length == 1 && y.StartsWith(x, StringComparison.Ordinal)) { sawInitial = true; continue; }
            if (y.Length == 1 && x.StartsWith(y, StringComparison.Ordinal)) { sawInitial = true; continue; }
            return false;
        }
        return sawInitial;
    }

    // ----- rule 7: studios ------------------------------------------------

    public string NormalizeStudio(string name)
    {
        var text = Fold(name);
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var suffixes = _options.StudioSuffixes.Select(s => s.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        while (words.Count > 1 && suffixes.Contains(words[^1].Trim('.', ',')))
            words.RemoveAt(words.Count - 1);
        return Collapse(string.Join(' ', words));
    }

    public string Normalize(string type, string name) => type switch
    {
        "Person" => NormalizePerson(name),
        "Studio" => NormalizeStudio(name),
        "Film" or "TVSeries" or "Episode" or "Work" => NormalizeTitle(name),
        _ => Collapse(Fold(name))
    };

    // ----- helpers --------------------------------------------------------

    /// <summary>Case-fold and strip diacritics.</summary>
    public static string Fold(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static string Collapse(string value) =>
        Regex.Replace(value, @"\s+", " ").Trim();

    private static int RomanToArabic(string roman)
    {
        var values = new Dictionary<char, int> { ['i'] = 1, ['v'] = 5, ['x'] = 10, ['l'] = 50, ['c'] = 100 };
        var lower = roman.ToLowerInvariant();
        var total = 0;
        for (var i = 0; i < lower.Length; i++)
        {
            if (!values.TryGetValue(lower[i], out var value)) return 0;
            var next = i + 1 < lower.Length && values.TryGetValue(lower[i + 1], out var n) ? n : 0;
            total += value < next ? -value : value;
        }
        return total;
    }

    private static Dictionary<string, string> LoadDiminutives(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (File.Exists(path))
        {
            try
            {
                var pairs = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                if (pairs is not null)
                    foreach (var (k, v) in pairs)
                        map[k.ToLowerInvariant()] = v.ToLowerInvariant();
            }
            catch (JsonException)
            {
                // A malformed lookup table must not take the app down; fall through to the default.
            }
        }
        if (map.Count == 0)
            foreach (var (k, v) in DefaultDiminutives)
                map[k] = v;
        return map;
    }

    /// <summary>
    /// Roughly forty pairs, which is enough to prevent most person-name fragmentation. Ships with
    /// the app so the resolver behaves the same on a fresh clone (spec §6.4 rule 4).
    /// </summary>
    public static readonly (string From, string To)[] DefaultDiminutives =
    [
        ("bob", "robert"), ("bobby", "robert"), ("rob", "robert"), ("robbie", "robert"),
        ("bill", "william"), ("billy", "william"), ("will", "william"), ("willie", "william"),
        ("dick", "richard"), ("rick", "richard"), ("ricky", "richard"), ("rich", "richard"),
        ("jim", "james"), ("jimmy", "james"), ("jamie", "james"),
        ("joe", "joseph"), ("joey", "joseph"),
        ("jack", "john"), ("johnny", "john"), ("jon", "john"),
        ("mike", "michael"), ("mick", "michael"), ("micky", "michael"),
        ("tom", "thomas"), ("tommy", "thomas"),
        ("dave", "david"), ("davey", "david"),
        ("dan", "daniel"), ("danny", "daniel"),
        ("chris", "christopher"), ("kit", "christopher"),
        ("steve", "stephen"), ("stevie", "stephen"), ("steven", "stephen"),
        ("tony", "anthony"), ("ant", "anthony"),
        ("ed", "edward"), ("eddie", "edward"), ("ted", "edward"), ("ned", "edward"),
        ("kate", "katherine"), ("katie", "katherine"), ("kathy", "katherine"), ("catherine", "katherine"),
        ("liz", "elizabeth"), ("beth", "elizabeth"), ("betty", "elizabeth"), ("eliza", "elizabeth"),
        ("meg", "margaret"), ("peggy", "margaret"), ("maggie", "margaret"),
        ("sue", "susan"), ("susie", "susan"),
        ("pat", "patricia"), ("patty", "patricia"), ("trish", "patricia"),
        ("nick", "nicholas"), ("nicky", "nicholas"),
        ("alex", "alexander"), ("sandy", "alexander"),
        ("sam", "samuel"), ("sammy", "samuel"),
        ("ben", "benjamin"), ("benny", "benjamin"),
        ("andy", "andrew"), ("drew", "andrew"),
        ("matt", "matthew"), ("greg", "gregory"), ("jeff", "jeffrey"),
        ("ken", "kenneth"), ("larry", "lawrence"), ("gerry", "gerald"), ("terry", "terence"),
        ("frank", "francis"), ("charlie", "charles"), ("chuck", "charles"),
        ("annie", "anne"), ("ann", "anne"), ("nan", "anne"),
        ("jenny", "jennifer"), ("jen", "jennifer"),
        ("becky", "rebecca"), ("bec", "rebecca"),
        ("vicky", "victoria"), ("tori", "victoria"),
    ];
}

/// <summary>Jaro-Winkler similarity — short person names need the common-prefix boost.</summary>
public static class JaroWinkler
{
    public static double Similarity(string a, string b)
    {
        if (a == b) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;

        var window = Math.Max(a.Length, b.Length) / 2 - 1;
        if (window < 0) window = 0;

        var aMatched = new bool[a.Length];
        var bMatched = new bool[b.Length];
        var matches = 0;

        for (var i = 0; i < a.Length; i++)
        {
            var start = Math.Max(0, i - window);
            var end = Math.Min(i + window + 1, b.Length);
            for (var j = start; j < end; j++)
            {
                if (bMatched[j] || a[i] != b[j]) continue;
                aMatched[i] = bMatched[j] = true;
                matches++;
                break;
            }
        }
        if (matches == 0) return 0.0;

        double transpositions = 0;
        var k = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (!aMatched[i]) continue;
            while (!bMatched[k]) k++;
            if (a[i] != b[k]) transpositions++;
            k++;
        }
        transpositions /= 2;

        var m = (double)matches;
        var jaro = (m / a.Length + m / b.Length + (m - transpositions) / m) / 3;

        var prefix = 0;
        while (prefix < Math.Min(4, Math.Min(a.Length, b.Length)) && a[prefix] == b[prefix]) prefix++;

        return jaro + prefix * 0.1 * (1 - jaro);
    }
}
