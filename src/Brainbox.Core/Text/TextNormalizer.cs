using System.Security.Cryptography;
using System.Text;

namespace Brainbox.Core.Text;

/// <summary>Canonical form of OCR text used for comparisons and cache keys.</summary>
public static class TextNormalizer
{
    /// <summary>
    /// NFKC (folds full-width Latin, half-width Katakana, ligatures), strips zero-width/control
    /// characters and collapses whitespace. Case is preserved (it matters for translation).
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var s = text.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(s.Length);
        var lastSpace = true;
        foreach (var c in s)
        {
            if (c is '​' or '‌' or '‍' or '﻿') continue;
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                if (!lastSpace) sb.Append(' ');
                lastSpace = true;
                continue;
            }

            sb.Append(c);
            lastSpace = false;
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Looser key for "is this the same on-screen text?" checks between frames: ignores case,
    /// spaces and punctuation that OCR tends to flicker on.
    /// </summary>
    public static string FuzzyKey(string? text)
    {
        var n = Normalize(text);
        var sb = new StringBuilder(n.Length);
        foreach (var c in n)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    /// <summary>Stable SHA-256 based key (hex, 32 chars) of normalized text + target language.</summary>
    public static string CacheKey(string normalizedText, string targetLanguage)
    {
        var bytes = Encoding.UTF8.GetBytes(targetLanguage.ToLowerInvariant() + "\u0001" + normalizedText);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 16);
    }

    /// <summary>Similarity 0..1 between two strings (normalized Levenshtein on fuzzy keys).</summary>
    public static double Similarity(string a, string b)
    {
        var x = FuzzyKey(a);
        var y = FuzzyKey(b);
        if (x.Length == 0 && y.Length == 0) return 1;
        if (x.Length == 0 || y.Length == 0) return 0;
        if (x == y) return 1;

        var prev = new int[y.Length + 1];
        var cur = new int[y.Length + 1];
        for (var j = 0; j <= y.Length; j++) prev[j] = j;
        for (var i = 1; i <= x.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= y.Length; j++)
            {
                var cost = x[i - 1] == y[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, cur) = (cur, prev);
        }

        return 1.0 - (double)prev[y.Length] / Math.Max(x.Length, y.Length);
    }
}
