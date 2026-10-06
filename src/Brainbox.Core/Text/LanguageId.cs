using System.Globalization;

namespace Brainbox.Core.Text;

public enum Script
{
    Unknown,
    Latin,
    Cyrillic,
    Greek,
    Arabic,
    Hebrew,
    Han,
    Kana,
    Hangul,
    Thai,
    Devanagari,
}

/// <summary>Result of language identification. <see cref="Code"/> is ISO 639-1 ("en", "ja", ...) or "und".</summary>
public readonly record struct LanguageGuess(string Code, Script Script, double Confidence)
{
    public static readonly LanguageGuess Undetermined = new("und", Script.Unknown, 0);
    public bool IsUndetermined => Code == "und";
}

/// <summary>
/// Small, dependency-free language identifier tuned for short on-screen strings.
/// Non-Latin scripts are identified by Unicode block; Latin-script languages by stop-word and
/// diacritic evidence. It is deliberately <b>conservative about calling Latin text non-English</b>:
/// a typical desktop is full of short English UI labels, and translating those would waste
/// resources and clutter the screen, so unmarked short Latin text defaults to English.
/// </summary>
public static class LanguageId
{
    private static readonly Dictionary<string, HashSet<string>> StopWords = new(StringComparer.Ordinal)
    {
        ["en"] = Set("the and of to in is you that it for on with as are this be at by not or from have an your was will can all we my our they if but more new about what when how which there their has had been would should could do does did just get no yes one out up so than then them these those its into only also here very file edit view help open close save settings search home sign log next back cancel ok please welcome hello world video watch play pause menu account privacy terms"),
        ["fr"] = Set("le la les des un une et est que qui dans pour pas sur au aux du ce cette sont avec ne se il elle nous vous ils je tu mon ma mes ton ta son sa leur plus mais ou où donc bonjour merci oui non tout tous toute monde avez êtes être faire très bien voici comment pourquoi"),
        ["es"] = Set("el la los las un una y es que en de del por para con no se lo le su sus al como más pero muy este esta estos hola gracias sí mundo todo todos usted ustedes yo tú él ella nosotros es está están ser hacer qué cómo dónde cuando porque"),
        ["de"] = Set("der die das und ist nicht ein eine zu den mit von dem im auf für sich des auch es an als wie wir ich sie er du ihr bei aus nach oder aber wenn noch nur kann hallo danke bitte welt alle sehr gut wird werden sind haben hat"),
        ["pt"] = Set("o a os as um uma e é que de do da dos das em no na nos nas por para com não se mais mas como muito este esta isso olá obrigado obrigada sim você vocês eu ele ela nós mundo todos está estão ser fazer"),
        ["it"] = Set("il lo la i gli le un una e è che di del della dei in per con non si più ma come molto questo questa ciao grazie sì io tu lui lei noi voi loro sono essere fare tutto tutti mondo buongiorno"),
        ["nl"] = Set("de het een en is van dat die in te zijn op met voor niet aan er ook als bij maar om ik je jij hij zij we wij jullie ze hallo dank bedankt wereld alle goed heel wordt worden"),
        ["tr"] = Set("ve bir bu da de ile için ne çok daha gibi ama ben sen o biz siz onlar var yok merhaba teşekkür evet hayır dünya her şey nasıl neden"),
        ["pl"] = Set("i w na z nie się do to że jest jak co tak ale o od po dla przez czy jestem jesteś są być mieć cześć dziękuję tak nie świat wszystko bardzo"),
        ["id"] = Set("dan yang di ke dari ini itu untuk dengan tidak ada akan juga atau saya kamu anda kami kita mereka halo terima kasih ya dunia semua sangat"),
        ["sv"] = Set("och att det som en på är av för med till den har inte om ett jag du han hon vi ni de hej tack ja nej världen alla mycket"),
        ["vi"] = Set("và của là có không được trong cho một những này với các người tôi bạn anh chị em xin chào cảm ơn thế giới rất"),
    };

    // Characters that are strong evidence for a specific Latin-script language.
    private static readonly (string Chars, string Code, double Weight)[] DiacriticHints =
    {
        ("ñ¿¡", "es", 3),
        ("ß", "de", 3),
        ("äöü", "de", 1),
        ("çèêëàâîïôûùœ", "fr", 1.2),
        ("ãõ", "pt", 3),
        ("ąęłńśźż", "pl", 3),
        ("ğışİ", "tr", 3),
        ("ăđơưạảấầẩẫậắằẳẵặẹẻẽếềểễệỉịọỏốồổỗộớờởỡợụủứừửữựỳỵỷỹ", "vi", 3),
        ("åø", "sv", 1.5),
        ("ìò", "it", 1),
        ("áéíóú", "es", 0.4),
    };

    public static LanguageGuess Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return LanguageGuess.Undetermined;

        var counts = CountScripts(text, out var letters);
        if (letters == 0) return LanguageGuess.Undetermined;

        // Dominant non-Latin script wins. Kana anywhere => Japanese (Kanji-only text is ambiguous).
        if (counts[(int)Script.Kana] > 0) return new LanguageGuess("ja", Script.Kana, Ratio(counts[(int)Script.Kana] + counts[(int)Script.Han], letters));
        if (counts[(int)Script.Hangul] > 0 && counts[(int)Script.Hangul] * 3 >= letters) return new LanguageGuess("ko", Script.Hangul, Ratio(counts[(int)Script.Hangul], letters));
        if (counts[(int)Script.Han] * 3 >= letters) return new LanguageGuess("zh", Script.Han, Ratio(counts[(int)Script.Han], letters));
        if (counts[(int)Script.Cyrillic] * 2 >= letters) return new LanguageGuess(CyrillicVariant(text), Script.Cyrillic, Ratio(counts[(int)Script.Cyrillic], letters));
        if (counts[(int)Script.Arabic] * 2 >= letters) return new LanguageGuess(text.AsSpan().IndexOfAny(PersianLetters) >= 0 ? "fa" : "ar", Script.Arabic, Ratio(counts[(int)Script.Arabic], letters));
        if (counts[(int)Script.Greek] * 2 >= letters) return new LanguageGuess("el", Script.Greek, Ratio(counts[(int)Script.Greek], letters));
        if (counts[(int)Script.Hebrew] * 2 >= letters) return new LanguageGuess("he", Script.Hebrew, Ratio(counts[(int)Script.Hebrew], letters));
        if (counts[(int)Script.Thai] * 2 >= letters) return new LanguageGuess("th", Script.Thai, Ratio(counts[(int)Script.Thai], letters));
        if (counts[(int)Script.Devanagari] * 2 >= letters) return new LanguageGuess("hi", Script.Devanagari, Ratio(counts[(int)Script.Devanagari], letters));
        if (counts[(int)Script.Latin] * 2 < letters) return LanguageGuess.Undetermined;

        return DetectLatin(text);
    }

    /// <summary>True when the text is (or should be treated as) the given language.</summary>
    public static bool IsLanguage(string text, string isoCode)
    {
        var g = Detect(text);
        return string.Equals(g.Code, Short(isoCode), StringComparison.OrdinalIgnoreCase);
    }

    public static string Short(string tagOrCode) =>
        string.IsNullOrEmpty(tagOrCode) ? "und" : tagOrCode.Split('-', '_')[0].ToLowerInvariant();

    public static Script ScriptOf(char c)
    {
        if (c < 0x80) return char.IsLetter(c) ? Script.Latin : Script.Unknown;
        if (c is >= 'À' and <= 'ɏ' || c is >= 'Ḁ' and <= 'ỿ') return Script.Latin;
        if (c is >= 'Ѐ' and <= 'ӿ') return Script.Cyrillic;
        if (c is >= 'Ͱ' and <= 'Ͽ') return Script.Greek;
        if (c is >= '֐' and <= '׿') return Script.Hebrew;
        if (c is >= '؀' and <= 'ۿ' || c is >= 'ݐ' and <= 'ݿ' || c is >= 'ﭐ' and <= '﷿' || c is >= 'ﹰ' and <= '﻿') return Script.Arabic;
        if (c is >= 'ऀ' and <= 'ॿ') return Script.Devanagari;
        if (c is >= '฀' and <= '๿') return Script.Thai;
        if (c is >= '぀' and <= 'ヿ' || c is >= 'ㇰ' and <= 'ㇿ' || c is >= 'ｦ' and <= 'ﾟ') return Script.Kana;
        if (c is >= '가' and <= '힣' || c is >= 'ᄀ' and <= 'ᇿ' || c is >= '㄰' and <= '㆏') return Script.Hangul;
        if (c is >= '一' and <= '鿿' || c is >= '㐀' and <= '䶿' || c is >= '豈' and <= '﫿') return Script.Han;
        return Script.Unknown;
    }

    /// <summary>Proportion of letters belonging to <paramref name="script"/>.</summary>
    public static double ScriptRatio(string text, Script script)
    {
        var counts = CountScripts(text, out var letters);
        return letters == 0 ? 0 : (double)counts[(int)script] / letters;
    }

    private static int[] CountScripts(string text, out int letters)
    {
        var counts = new int[Enum.GetValues<Script>().Length];
        letters = 0;
        foreach (var c in text)
        {
            var s = ScriptOf(c);
            if (s == Script.Unknown) continue;
            counts[(int)s]++;
            letters++;
        }

        return counts;
    }

    private static double Ratio(int part, int whole) => whole == 0 ? 0 : Math.Min(1.0, (double)part / whole);

    private static readonly System.Buffers.SearchValues<char> UkrainianLetters = System.Buffers.SearchValues.Create("іїєґІЇЄҐ");
    private static readonly System.Buffers.SearchValues<char> BelarusianLetters = System.Buffers.SearchValues.Create("ўЎ");
    private static readonly System.Buffers.SearchValues<char> PersianLetters = System.Buffers.SearchValues.Create("پچژگ");

    private static string CyrillicVariant(string text)
    {
        if (text.AsSpan().IndexOfAny(UkrainianLetters) >= 0) return "uk";
        if (text.AsSpan().IndexOfAny(BelarusianLetters) >= 0) return "be";
        return "ru";
    }

    private static LanguageGuess DetectLatin(string text)
    {
        var lower = text.ToLower(CultureInfo.InvariantCulture);
        var words = Tokenize(lower);
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var w in words)
        {
            foreach (var (code, set) in StopWords)
            {
                if (set.Contains(w)) scores[code] = scores.GetValueOrDefault(code) + 1;
            }
        }

        var diacritics = 0;
        foreach (var c in lower)
        {
            foreach (var (chars, code, weight) in DiacriticHints)
            {
                if (chars.Contains(c, StringComparison.Ordinal))
                {
                    scores[code] = scores.GetValueOrDefault(code) + weight;
                    diacritics++;
                }
            }
        }

        // Words that appear in several lists ("de", "a", "la") must not out-vote English evidence.
        var en = scores.GetValueOrDefault("en");
        var best = scores.Where(kv => kv.Key != "en").OrderByDescending(kv => kv.Value).FirstOrDefault();

        if (best.Key == null || best.Value <= 0)
        {
            // No evidence for a foreign language: English (or English-like UI text).
            var conf = words.Count == 0 ? 0.3 : Math.Min(1, 0.5 + en / Math.Max(1, words.Count));
            return new LanguageGuess("en", Script.Latin, conf);
        }

        // Foreign evidence must clearly beat English evidence; diacritics are strong evidence
        // because plain English almost never contains them.
        var margin = diacritics > 0 ? 0 : 1;
        if (best.Value > en + margin || (diacritics > 0 && best.Value >= en))
        {
            var conf = Math.Min(1, best.Value / Math.Max(2.0, words.Count));
            return new LanguageGuess(best.Key, Script.Latin, Math.Max(0.35, conf));
        }

        return new LanguageGuess("en", Script.Latin, Math.Min(1, 0.5 + en / Math.Max(1, words.Count)));
    }

    private static List<string> Tokenize(string lower)
    {
        var list = new List<string>();
        var start = -1;
        for (var i = 0; i <= lower.Length; i++)
        {
            var isLetter = i < lower.Length && (char.IsLetter(lower[i]) || lower[i] == '\'');
            if (isLetter && start < 0) start = i;
            else if (!isLetter && start >= 0)
            {
                list.Add(lower[start..i].Trim('\''));
                start = -1;
            }
        }

        return list;
    }

    private static HashSet<string> Set(string words) =>
        new(words.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
}
