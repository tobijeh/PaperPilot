using System.Globalization;
using System.Text;

namespace PaperPilot.Core;

/// <summary>
/// Basic word normalization. Milestone 4 extends this with numbers, scripture references
/// and contractions; for now: lowercase and strip punctuation.
/// </summary>
public static class TextNormalizer
{
    public static string NormalizeWord(string word)
    {
        if (string.IsNullOrEmpty(word)) return string.Empty;
        var sb = new StringBuilder(word.Length);
        foreach (var ch in word.Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            // Keep the separator in things like 3:16 so Milestone 4 can expand references.
            else if (ch == ':' && sb.Length > 0) sb.Append(':');
        }
        return sb.ToString().TrimEnd(':');
    }
}
