namespace Brainbox.Core.Translation;

/// <summary>ISO 639-1 codes ⇄ English language names (used in LLM prompts and the settings UI).</summary>
public static class LanguageNames
{
    public static IReadOnlyList<(string Code, string Name)> All { get; } = new (string, string)[]
    {
        ("en", "English"), ("ja", "Japanese"), ("zh", "Chinese (Simplified)"), ("zh-TW", "Chinese (Traditional)"),
        ("ko", "Korean"), ("ar", "Arabic"), ("de", "German"), ("fr", "French"), ("es", "Spanish"),
        ("ru", "Russian"), ("pt", "Portuguese"), ("it", "Italian"), ("nl", "Dutch"), ("tr", "Turkish"),
        ("pl", "Polish"), ("uk", "Ukrainian"), ("vi", "Vietnamese"), ("th", "Thai"), ("id", "Indonesian"),
        ("hi", "Hindi"), ("fa", "Persian"), ("he", "Hebrew"), ("el", "Greek"), ("sv", "Swedish"),
        ("cs", "Czech"), ("ro", "Romanian"), ("hu", "Hungarian"), ("fi", "Finnish"), ("da", "Danish"),
        ("no", "Norwegian"), ("bg", "Bulgarian"), ("be", "Belarusian"), ("yo", "Yoruba"), ("ig", "Igbo"),
        ("ha", "Hausa"), ("sw", "Swahili"),
    };

    public static string NameOf(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code == "auto") return "the source language";
        foreach (var (c, n) in All)
        {
            if (string.Equals(c, code, StringComparison.OrdinalIgnoreCase)) return n;
        }

        var shortCode = code.Split('-', '_')[0];
        foreach (var (c, n) in All)
        {
            if (string.Equals(c, shortCode, StringComparison.OrdinalIgnoreCase)) return n;
        }

        return code;
    }
}
