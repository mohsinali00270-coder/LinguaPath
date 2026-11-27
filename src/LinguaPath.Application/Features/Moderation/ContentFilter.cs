using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LinguaPath.Shared;

namespace LinguaPath.Application.Features.Moderation;

public sealed record FilterWord(string Term, ModerationSeverity Severity, int? LanguageId = null);

public sealed record FilterPattern(string Pattern, bool IsException);

public enum ContactKind
{
    Url = 1,
    Domain = 2,
    Email = 3,
    Phone = 4,
    SocialHandle = 5,
    Custom = 6
}

public sealed record ContactMatch(ContactKind Kind, string Value);

public sealed record WordHit(string Term, ModerationSeverity Severity, int Start, int Length);

public sealed record FilterResult(ModerationSeverity Severity, IReadOnlyList<WordHit> Words, IReadOnlyList<ContactMatch> Contacts, string MaskedText)
{
    public bool HasContacts => Contacts.Count > 0;
    public string MatchedTerms => string.Join(", ", Words.Select(w => w.Term).Distinct().Take(20));
}

/// <summary>
/// The word and link/contact filter (FR-M01, FR-M05 .. FR-M09, FR-M25). Pure and thread-safe once built.
/// Words are matched after normalising: lower case, accents removed, leetspeak ("1d10t"), repeated letters ("stuuupid")
/// and spaced or dotted letters ("s t u p i d", "s.t.u.p.i.d"). Links and contacts are found even when disguised
/// ("example dot com", "name at gmail", phone numbers written with spaces or as words).
/// </summary>
public sealed class ContentFilter
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(150);

    private readonly List<(string[] Tokens, FilterWord Word)> words;
    private readonly List<Regex> blocked = [];
    private readonly List<Regex> exceptions = [];

    public ContentFilter(IEnumerable<FilterWord> words, IEnumerable<FilterPattern>? patterns = null)
    {
        this.words = words
            .Where(w => w.Severity > ModerationSeverity.None && !string.IsNullOrWhiteSpace(w.Term))
            .Select(w => (Tokens: Tokenize(w.Term).Select(t => t.Norm).Where(t => t.Length > 0).ToArray(), Word: w))
            .Where(w => w.Tokens.Length > 0)
            // Longer phrases first so "piece of trash" wins over "trash".
            .OrderByDescending(w => w.Tokens.Length).ThenByDescending(w => w.Word.Severity)
            .ToList();

        foreach (var p in patterns ?? [])
        {
            var regex = TryRegex(p.Pattern);
            if (regex is null) continue;
            (p.IsException ? exceptions : blocked).Add(regex);
        }
    }

    /// <summary>Returns null when the pattern isn't a valid regular expression (shown to admins as an error).</summary>
    public static Regex? TryRegex(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public FilterResult Check(string? text, int? languageId = null)
    {
        text ??= "";
        var contacts = FindContacts(text);
        var hits = FindWords(text, languageId);
        var severity = hits.Count == 0 ? ModerationSeverity.None : hits.Max(h => h.Severity);
        return new FilterResult(severity, hits, contacts, Mask(text, hits.Where(h => h.Severity == ModerationSeverity.Mild)));
    }

    // ------------------------------------------------------------------ words

    private List<WordHit> FindWords(string text, int? languageId)
    {
        var tokens = MergeSpelledOut(Tokenize(text));
        var hits = new List<WordHit>();
        var used = new bool[tokens.Count];

        foreach (var (termTokens, word) in words)
        {
            if (word.LanguageId is not null && languageId is not null && word.LanguageId != languageId) continue;
            for (var i = 0; i < tokens.Count; i++)
            {
                if (used[i]) continue;
                var length = Matches(tokens, used, i, termTokens);
                if (length == 0) continue;
                var first = tokens[i];
                var last = tokens[i + length - 1];
                hits.Add(new WordHit(word.Term, word.Severity, first.Start, last.Start + last.Length - first.Start));
                for (var k = i; k < i + length; k++) used[k] = true;
            }
        }
        return hits.OrderBy(h => h.Start).ToList();
    }

    /// <summary>Number of tokens matched at <paramref name="at"/>, or 0. Phrases also match when split or joined differently ("your-self", "killyourself").</summary>
    private static int Matches(List<Token> tokens, bool[] used, int at, string[] term)
    {
        if (term.Length == 1)
        {
            var t = tokens[at].Norm;
            var w = term[0];
            if (t == w) return 1;
            // Compounds and inflections ("idiots", "stupidface") only for longer terms, so "ass" never hits "class".
            if (w.Length >= 5 && (t.StartsWith(w, StringComparison.Ordinal) || t.EndsWith(w, StringComparison.Ordinal))) return 1;
        }
        else
        {
            var exact = at + term.Length <= tokens.Count;
            for (var k = 0; exact && k < term.Length; k++)
                if (used[at + k] || tokens[at + k].Norm != term[k]) exact = false;
            if (exact) return term.Length;
        }

        // Same letters, different spacing: compare the joined forms over a window of up to one token more than the term.
        var joined = string.Concat(term);
        if (joined.Length < 4) return 0;
        var sb = new System.Text.StringBuilder();
        for (var len = 1; len <= term.Length + 1 && at + len <= tokens.Count; len++)
        {
            if (used[at + len - 1]) break;
            sb.Append(tokens[at + len - 1].Norm);
            if (sb.Length > joined.Length) break;
            if (len > 1 || term.Length > 1)
                if (sb.ToString() == joined) return len;
        }
        return 0;
    }

    private static string Mask(string text, IEnumerable<WordHit> hits)
    {
        var chars = text.ToCharArray();
        foreach (var h in hits)
        {
            var first = true;
            for (var i = h.Start; i < h.Start + h.Length && i < chars.Length; i++)
            {
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] is not ('@' or '$')) continue;
                if (first) { first = false; continue; }
                chars[i] = '*';
            }
        }
        return new string(chars);
    }

    internal readonly record struct Token(string Raw, string Norm, int Start, int Length);

    private static readonly Regex TokenRegex = new(@"[\p{L}\p{M}\p{N}@$]+", RegexOptions.CultureInvariant);

    internal static List<Token> Tokenize(string text) =>
        TokenRegex.Matches(text).Select(m => new Token(m.Value, NormalizeWord(m.Value), m.Index, m.Length)).ToList();

    /// <summary>"s t u p i d" and "s.t.u.p.i.d" become one token when the letters are separated by at most two non-letters.</summary>
    private static List<Token> MergeSpelledOut(List<Token> tokens)
    {
        var result = new List<Token>(tokens.Count);
        var i = 0;
        while (i < tokens.Count)
        {
            var j = i;
            while (j + 1 < tokens.Count && tokens[j].Raw.Length == 1 && tokens[j + 1].Raw.Length == 1
                   && tokens[j + 1].Start - (tokens[j].Start + 1) <= 2) j++;
            if (j - i + 1 >= 3)
            {
                var raw = string.Concat(tokens.Skip(i).Take(j - i + 1).Select(t => t.Raw));
                var start = tokens[i].Start;
                result.Add(new Token(raw, NormalizeWord(raw), start, tokens[j].Start + 1 - start));
                i = j + 1;
            }
            else
            {
                result.Add(tokens[i]);
                i++;
            }
        }
        return result;
    }

    /// <summary>Lower case, accents removed, leetspeak mapped (only in words with letters), runs of a letter collapsed to one.</summary>
    public static string NormalizeWord(string word)
    {
        var lower = RemoveDiacritics(word.ToLowerInvariant());
        var hasLetter = lower.Any(char.IsLetter);
        var sb = new StringBuilder(lower.Length);
        foreach (var c in lower)
        {
            var mapped = hasLetter ? c switch
            {
                '0' => 'o', '1' => 'i', '3' => 'e', '4' => 'a', '5' => 's', '7' => 't', '8' => 'b', '9' => 'g',
                '@' => 'a', '$' => 's',
                _ => c
            } : c;
            if (!char.IsLetterOrDigit(mapped)) continue;
            if (sb.Length > 0 && sb[^1] == mapped) continue;
            sb.Append(mapped);
        }
        return sb.ToString();
    }

    public static string NormalizeText(string? text) =>
        string.Join(' ', Tokenize(text ?? "").Select(t => t.Norm).Where(t => t.Length > 0));

    private static string RemoveDiacritics(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    // ------------------------------------------------------------------ links and contacts (BR-25)

    private const string Tlds = "com|net|org|io|co|me|ly|gg|app|xyz|info|biz|ru|pk|in|uk|de|fr|tk|link|site|online|store|shop|live|tv|club|top|cc|us|ca|au|eu|ai|dev";
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex UrlRegex = new(@"\b(?:https?://|www\.)\S+", Opt, RegexTimeout);
    private static readonly Regex EmailRegex = new(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+", Opt, RegexTimeout);
    // "example.com" (TLD in lower case, so "happy.In the morning" isn't a domain) or "example dot com" / "example (dot) com".
    private static readonly Regex DomainRegex = new(
        $@"\b[\p{{L}}\p{{N}}-]{{2,}}(?:\.(?-i:(?:{Tlds}))|\s*(?:\(\s*dot\s*\)|\[\s*dot\s*\]|\s+d[o0]t\s+)\s*(?:{Tlds}))\b", Opt, RegexTimeout);
    private static readonly Regex DisguisedEmailRegex = new(
        @"\b[\w.+-]{2,}\s*(?:\(\s*at\s*\)|\[\s*at\s*\]|\s+at\s+|@\s*)\s*(?:gmail|g\s*mail|yahoo|hotmail|outlook|icloud|live|proton\w*|aol|yandex|mail)\b", Opt, RegexTimeout);
    private static readonly Regex ShortLinkRegex = new(@"\b(?:t\.me|wa\.me|bit\.ly|discord\.gg|tinyurl|goo\.gl)\b", Opt, RegexTimeout);
    private static readonly Regex SocialRegex = new(
        @"\b(?:add|find|text|message|msg|dm|call|contact|follow|reach)\s+me\s+(?:on|at|via)\s+(?:whats\s*app|telegram|snap\s*chat|snap|insta(?:gram)?|ig|kik|we\s*chat|discord|signal|viber|line|facebook|fb|tiktok)\b"
        + @"|\bmy\s+(?:whats\s*app|telegram|snap(?:\s*chat)?|insta(?:gram)?|ig|kik|we\s*chat|discord|signal|viber|facebook|fb|tiktok)\s*(?:id|handle|username|number|account)?\s*(?:is|=|:)\s*@?[\w.]{3,}", Opt, RegexTimeout);
    private static readonly Regex DigitRun = new(@"\+?\d[\d\s\-().]{5,}\d", Opt, RegexTimeout);

    private static readonly Dictionary<string, char> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = '0', ["oh"] = '0', ["one"] = '1', ["two"] = '2', ["three"] = '3', ["four"] = '4', ["five"] = '5',
        ["six"] = '6', ["seven"] = '7', ["eight"] = '8', ["nine"] = '9'
    };

    public IReadOnlyList<ContactMatch> FindContacts(string text)
    {
        var found = new List<ContactMatch>();
        try
        {
            Add(found, UrlRegex, text, ContactKind.Url);
            Add(found, EmailRegex, text, ContactKind.Email);
            Add(found, DisguisedEmailRegex, text, ContactKind.Email);
            Add(found, ShortLinkRegex, text, ContactKind.Url);
            Add(found, DomainRegex, text, ContactKind.Domain);
            Add(found, SocialRegex, text, ContactKind.SocialHandle);
            foreach (Match m in DigitRun.Matches(text))
                if (LooksLikePhone(m.Value)) found.Add(new ContactMatch(ContactKind.Phone, m.Value.Trim()));
            var spoken = SpokenDigits(text);
            if (spoken is not null) found.Add(new ContactMatch(ContactKind.Phone, spoken));
            foreach (var regex in blocked) Add(found, regex, text, ContactKind.Custom);
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological message: treat as containing a contact rather than letting it through unchecked.
            found.Add(new ContactMatch(ContactKind.Custom, "(unreadable)"));
        }

        if (exceptions.Count > 0)
            found = found.Where(f => !exceptions.Any(e => SafeIsMatch(e, f.Value))).ToList();

        return found.GroupBy(f => f.Value.ToLowerInvariant()).Select(g => g.First()).ToList();
    }

    private static bool SafeIsMatch(Regex regex, string value)
    {
        try { return regex.IsMatch(value); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static void Add(List<ContactMatch> list, Regex regex, string text, ContactKind kind)
    {
        foreach (Match m in regex.Matches(text)) list.Add(new ContactMatch(kind, m.Value.Trim()));
    }

    /// <summary>9+ digits, or 7-8 digits written like a number ("+92 300", "0300-1234"). Years like "1990-2000" pass.</summary>
    private static bool LooksLikePhone(string value)
    {
        var digits = value.Count(char.IsDigit);
        var trimmed = value.TrimStart();
        return digits >= 9 || (digits >= 7 && (trimmed.StartsWith('+') || trimmed.StartsWith('0') || trimmed.StartsWith('(')));
    }

    /// <summary>Counting practice ("one two three four five six seven") isn't a phone number.</summary>
    private static bool IsCounting(string digits)
    {
        var up = true;
        var down = true;
        for (var i = 1; i < digits.Length; i++)
        {
            if (digits[i] - digits[i - 1] != 1) up = false;
            if (digits[i - 1] - digits[i] != 1) down = false;
        }
        return up || down;
    }

    /// <summary>"zero three one two ..." or mixed "03 one two 4567": seven or more digits in a row.</summary>
    private static string? SpokenDigits(string text)
    {
        var digits = new StringBuilder();
        var best = "";
        var anyWord = false;
        foreach (var t in Regex.Split(text.ToLowerInvariant(), @"[\s,.\-]+"))
        {
            if (t.Length == 0) continue;
            if (NumberWords.TryGetValue(t, out var d)) { digits.Append(d); anyWord = true; }
            else if (t.All(char.IsDigit)) digits.Append(t);
            else
            {
                if (anyWord && digits.Length > best.Length) best = digits.ToString();
                digits.Clear();
                anyWord = false;
            }
        }
        if (anyWord && digits.Length > best.Length) best = digits.ToString();
        return best.Length >= 7 && !IsCounting(best) ? best : null;
    }
}
