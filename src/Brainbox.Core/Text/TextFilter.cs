using System.Text.RegularExpressions;

namespace Brainbox.Core.Text;

public enum FilterVerdict
{
    Translate,
    Empty,
    TooShort,
    Url,
    Email,
    FilePath,
    Code,
    Numeric,
    Symbols,
    KeyboardShortcut,
    AlreadyTargetLanguage,
    OwnOverlayOutput,
    Garbled,
    SourceLanguageMismatch,
}

/// <summary>
/// Decides whether a recognised line is worth translating (requirement §6): skips text already in
/// the target language, URLs, e-mail addresses, file paths, code, numbers, symbol noise, keyboard
/// shortcuts and — critically — text that matches what our own overlay is currently displaying
/// (defence in depth for §16 in case the OS ever lets the overlay leak into a capture).
/// </summary>
public sealed partial class TextFilter
{
    private readonly object _gate = new();
    private readonly HashSet<string> _ownOutputKeys = new(StringComparer.Ordinal);

    public string TargetLanguage { get; set; } = "en";

    /// <summary>"auto" or an ISO code; when set, lines confidently in another language are skipped.</summary>
    public string SourceLanguage { get; set; } = "auto";

    /// <summary>Registers text currently shown by the overlay so it is never fed back into translation.</summary>
    public void SetOwnOutputs(IEnumerable<string> translations)
    {
        lock (_gate)
        {
            _ownOutputKeys.Clear();
            foreach (var t in translations)
            {
                var k = TextNormalizer.FuzzyKey(t);
                if (k.Length > 0) _ownOutputKeys.Add(k);
            }
        }
    }

    public bool IsOwnOutput(string text)
    {
        var k = TextNormalizer.FuzzyKey(text);
        if (k.Length == 0) return false;
        lock (_gate)
        {
            if (_ownOutputKeys.Contains(k)) return true;
            // Overlay text read back by OCR may be slightly corrupted, cut, or prefixed.
            foreach (var own in _ownOutputKeys)
            {
                if (own.Length >= 6 && k.Length >= 6 && (own.Contains(k, StringComparison.Ordinal) || k.Contains(own, StringComparison.Ordinal)))
                    return true;
                if (own.Length >= 5 && k.Length >= 5 && Math.Abs(own.Length - k.Length) <= Math.Max(3, own.Length / 4) && TextNormalizer.Similarity(own, k) >= (Math.Min(own.Length, k.Length) >= 8 ? 0.7 : 0.8))
                    return true;
            }
        }

        return false;
    }

    public FilterVerdict Evaluate(string rawText, string? ocrLanguageTag = null)
    {
        var text = TextNormalizer.Normalize(rawText);
        if (text.Length == 0) return FilterVerdict.Empty;

        var letters = 0;
        var nonLatinLetters = 0;
        foreach (var c in text)
        {
            if (!char.IsLetter(c)) continue;
            letters++;
            if (LanguageId.ScriptOf(c) is not (Script.Latin or Script.Unknown)) nonLatinLetters++;
        }

        // CJK carries a lot of meaning per character; Latin needs a couple of letters.
        if (letters == 0)
        {
            return text.Any(char.IsDigit) ? FilterVerdict.Numeric : FilterVerdict.Symbols;
        }

        if (nonLatinLetters == 0 && letters < 2) return FilterVerdict.TooShort;
        if (letters * 3 < text.Count(c => !char.IsWhiteSpace(c)) && nonLatinLetters == 0) return FilterVerdict.Symbols;

        if (UrlRegex().IsMatch(text)) return FilterVerdict.Url;
        if (EmailRegex().IsMatch(text)) return FilterVerdict.Email;
        if (PathRegex().IsMatch(text)) return FilterVerdict.FilePath;
        if (ShortcutRegex().IsMatch(text)) return FilterVerdict.KeyboardShortcut;
        if (nonLatinLetters * 2 < letters && LooksLikeCode(text)) return FilterVerdict.Code;
        if (nonLatinLetters == 0 && LooksGarbled(text)) return FilterVerdict.Garbled;

        if (IsOwnOutput(text)) return FilterVerdict.OwnOverlayOutput;

        var guess = LanguageId.Detect(text);
        var target = LanguageId.Short(TargetLanguage);
        if (guess.Code == target) return FilterVerdict.AlreadyTargetLanguage;

        // A Latin recogniser reading Latin text that we cannot attribute to a foreign language is
        // treated as target-language text when the target is English (the common desktop case).
        if (guess.IsUndetermined && target == "en" && nonLatinLetters == 0) return FilterVerdict.AlreadyTargetLanguage;

        if (SourceLanguage != "auto" && !guess.IsUndetermined)
        {
            var src = LanguageId.Short(SourceLanguage);
            // Han-only text is shared by zh/ja; don't reject it on a zh/ja mismatch.
            var hanAmbiguous = guess.Script == Script.Han && (src is "ja" or "zh");
            if (guess.Code != src && !hanAmbiguous) return FilterVerdict.SourceLanguageMismatch;
        }

        return FilterVerdict.Translate;
    }

    private static readonly char[] TokenSeparators = { ' ', '-', '_', ':', '/', '.', ',', '"' };

    private static bool LooksLikeCode(string text)
    {
        if (CodeRegex().IsMatch(text)) return true;

        // Identifiers, hashes, GUIDs, log noise: most tokens mix letters and digits ("e42382c6-f7aa").
        var tokens = text.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 0)
        {
            var mixed = tokens.Count(t => t.Any(char.IsDigit) && t.Any(c => char.IsLetter(c) && LanguageId.ScriptOf(c) == Script.Latin));
            var digits = text.Count(char.IsDigit);
            var alnum = text.Count(char.IsLetterOrDigit);
            if (mixed * 2 >= tokens.Length || (alnum > 0 && digits * 10 >= alnum * 3)) return true;
        }

        var symbols = text.Count(c => "{}[];=<>()_$#\\|&*".Contains(c, StringComparison.Ordinal));
        return symbols >= 3 && symbols * 6 >= text.Length;
    }

    /// <summary>OCR noise: mostly 1-character tokens or very low vowel content in "words".</summary>
    private static bool LooksGarbled(string text)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length >= 3 && tokens.Count(t => t.Length == 1) * 2 > tokens.Length) return true;

        var letters = text.Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray();
        if (letters.Length >= 6)
        {
            var vowels = letters.Count(c => "aeiouyàáâãäåèéêëìíîïòóôõöùúûüýœæ".Contains(c, StringComparison.Ordinal));
            if (vowels * 10 < letters.Length) return true;
        }

        return false;
    }

    [GeneratedRegex(@"^\s*((https?|ftp)://|www\.)\S+\s*$|^\s*[\w-]+(\.[\w-]+)+(/\S*)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"^\s*[\w.+-]+@[\w-]+(\.[\w-]+)+\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"^\s*([a-zA-Z]:\\|\\\\|~/|/(usr|home|etc|var|opt|mnt|tmp)/|\.{1,2}[\\/])\S*", RegexOptions.CultureInvariant)]
    private static partial Regex PathRegex();

    [GeneratedRegex(@"^\s*((ctrl|control|alt|shift|win|cmd|⌘|⌥|⇧)\s*[+\-]\s*)+(\w{1,6}|f\d{1,2}|[^\s])\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShortcutRegex();

    [GeneratedRegex(@"(\b(function|var|const|let|return|public|private|static|void|import|from|def|class)\b.*[({=;])|(=>)|(\w+\(\w*\)\s*[;{])|(^\s*(#include|using\s+[\w.]+;|<\/?\w+[^>]*>))", RegexOptions.CultureInvariant)]
    private static partial Regex CodeRegex();
}
