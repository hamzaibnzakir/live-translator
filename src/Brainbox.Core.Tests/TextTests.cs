using Brainbox.Core.Geometry;
using Brainbox.Core.Text;
using Xunit;

namespace Brainbox.Core.Tests;

public class LanguageIdTests
{
    [Theory]
    [InlineData("こんにちは、世界", "ja")]
    [InlineData("設定を保存しました", "ja")]
    [InlineData("你好，世界。今天天气很好", "zh")]
    [InlineData("안녕하세요 세계", "ko")]
    [InlineData("Привет, мир! Как дела?", "ru")]
    [InlineData("Привіт, як справи? Їжак", "uk")]
    [InlineData("مرحبا بالعالم", "ar")]
    [InlineData("Bonjour tout le monde, comment allez-vous ?", "fr")]
    [InlineData("¿Dónde está la biblioteca?", "es")]
    [InlineData("Hola mundo, gracias por todo", "es")]
    [InlineData("Die Straße ist sehr schön und groß", "de")]
    [InlineData("Ich habe keine Zeit, aber danke", "de")]
    [InlineData("Obrigado, você está muito bem", "pt")]
    [InlineData("Γεια σου κόσμε", "el")]
    [InlineData("สวัสดีครับ", "th")]
    [InlineData("The quick brown fox jumps over the lazy dog", "en")]
    [InlineData("Settings", "en")]
    [InlineData("File Edit View Help", "en")]
    [InlineData("Download", "en")]
    public void Detects_language(string text, string expected)
    {
        Assert.Equal(expected, LanguageId.Detect(text).Code);
    }

    [Fact]
    public void Empty_and_symbols_are_undetermined()
    {
        Assert.True(LanguageId.Detect("").IsUndetermined);
        Assert.True(LanguageId.Detect("12:45 — 3/4").IsUndetermined);
    }

    [Fact]
    public void Short_tags()
    {
        Assert.Equal("ja", LanguageId.Short("ja-JP"));
        Assert.Equal("zh", LanguageId.Short("zh_Hans"));
        Assert.Equal("und", LanguageId.Short(""));
    }
}

public class TextFilterTests
{
    private static TextFilter Filter() => new() { TargetLanguage = "en", SourceLanguage = "auto" };

    [Theory]
    [InlineData("https://example.com/path?q=1", FilterVerdict.Url)]
    [InlineData("www.google.co.jp", FilterVerdict.Url)]
    [InlineData("someone@example.org", FilterVerdict.Email)]
    [InlineData(@"C:\Users\David\Documents\report.docx", FilterVerdict.FilePath)]
    [InlineData("/usr/local/bin/python3", FilterVerdict.FilePath)]
    [InlineData("const x = foo(bar);", FilterVerdict.Code)]
    [InlineData("if (a == b) { return c; }", FilterVerdict.Code)]
    [InlineData("12,345.67", FilterVerdict.Numeric)]
    [InlineData("2026-10-06 15:35", FilterVerdict.Numeric)]
    [InlineData("→ ★ ✓ ••", FilterVerdict.Symbols)]
    [InlineData("Ctrl+Shift+S", FilterVerdict.KeyboardShortcut)]
    [InlineData("Alt + F4", FilterVerdict.KeyboardShortcut)]
    [InlineData("Save your changes before closing", FilterVerdict.AlreadyTargetLanguage)]
    [InlineData("Settings", FilterVerdict.AlreadyTargetLanguage)]
    [InlineData("a", FilterVerdict.TooShort)]
    [InlineData("   ", FilterVerdict.Empty)]
    [InlineData("こんにちは", FilterVerdict.Translate)]
    [InlineData("Bonjour tout le monde", FilterVerdict.Translate)]
    [InlineData("Привет", FilterVerdict.Translate)]
    [InlineData("x x x x q", FilterVerdict.Garbled)]
    public void Verdicts(string text, FilterVerdict expected)
    {
        Assert.Equal(expected, Filter().Evaluate(text));
    }

    [Fact]
    public void Never_translates_own_overlay_output()
    {
        var f = Filter();
        f.SetOwnOutputs(new[] { "Hello, how are you doing today?", "Save the file" });
        Assert.True(f.IsOwnOutput("Hello, how are you doing today?"));
        Assert.True(f.IsOwnOutput("hello how are you doing today")); // OCR lost punctuation/case
        Assert.True(f.IsOwnOutput("Hello, how are you doing"));       // cut off
        Assert.False(f.IsOwnOutput("こんにちは"));
    }

    [Fact]
    public void Own_output_check_runs_before_language_detection()
    {
        var f = new TextFilter { TargetLanguage = "fr" };
        f.SetOwnOutputs(new[] { "Bonjour tout le monde" });
        Assert.Equal(FilterVerdict.OwnOverlayOutput, f.Evaluate("Bonjour tout le monde"));
    }

    [Fact]
    public void Target_language_other_than_english_skips_that_language()
    {
        var f = new TextFilter { TargetLanguage = "fr" };
        Assert.Equal(FilterVerdict.AlreadyTargetLanguage, f.Evaluate("Bonjour tout le monde, merci beaucoup"));
        Assert.Equal(FilterVerdict.Translate, f.Evaluate("こんにちは"));
    }

    [Fact]
    public void Explicit_source_language_skips_other_languages()
    {
        var f = new TextFilter { TargetLanguage = "en", SourceLanguage = "ja" };
        Assert.Equal(FilterVerdict.Translate, f.Evaluate("こんにちは"));
        Assert.Equal(FilterVerdict.Translate, f.Evaluate("設定")); // Han-only is ja/zh ambiguous: allowed
        Assert.Equal(FilterVerdict.SourceLanguageMismatch, f.Evaluate("Привет мир"));
    }
}

public class TextNormalizerTests
{
    [Fact]
    public void Normalizes_width_and_whitespace()
    {
        Assert.Equal("ABC 123 ｶ", TextNormalizer.Normalize("ＡＢＣ\u3000１２３   ｶ").Replace("カ", "ｶ"));
        Assert.Equal("a b", TextNormalizer.Normalize("  a\u200B \n\t b "));
    }

    [Fact]
    public void Fuzzy_key_ignores_case_and_punctuation()
    {
        Assert.Equal(TextNormalizer.FuzzyKey("Hello, World!"), TextNormalizer.FuzzyKey("hello world"));
    }

    [Fact]
    public void Cache_key_depends_on_text_and_target()
    {
        var a = TextNormalizer.CacheKey("こんにちは", "en");
        Assert.Equal(a, TextNormalizer.CacheKey("こんにちは", "EN"));
        Assert.NotEqual(a, TextNormalizer.CacheKey("こんにちは", "fr"));
        Assert.NotEqual(a, TextNormalizer.CacheKey("こんばんは", "en"));
        Assert.Equal(32, a.Length);
    }

    [Fact]
    public void Similarity()
    {
        Assert.Equal(1.0, TextNormalizer.Similarity("abc", "ABC"));
        Assert.True(TextNormalizer.Similarity("Bonjour le monde", "Bonjour le rnonde") > 0.85);
        Assert.True(TextNormalizer.Similarity("abc", "xyz") < 0.1);
    }
}

public class PixelRectTests
{
    [Fact]
    public void Intersect_union_iou()
    {
        var a = new PixelRect(0, 0, 100, 100);
        var b = new PixelRect(50, 50, 100, 100);
        Assert.Equal(new PixelRect(50, 50, 50, 50), a.Intersect(b));
        Assert.Equal(new PixelRect(0, 0, 150, 150), a.Union(b));
        Assert.Equal(2500.0 / 17500.0, a.IoU(b), 6);
        Assert.True(a.Intersect(new PixelRect(200, 200, 10, 10)).IsEmpty);
        Assert.Equal(0.25, a.CoveredBy(b), 6);
    }

    [Fact]
    public void Negative_virtual_desktop_coordinates()
    {
        var left = new PixelRect(-1920, 0, 1920, 1080);
        Assert.True(left.Contains(-10, 10));
        Assert.False(left.Contains(0, 10));
        Assert.Equal(-1920, left.Left);
        Assert.Equal(0, left.Right);
    }
}
