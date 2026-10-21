using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AuctionVoice.NLU
{
    /// <summary>
    /// String normalisation shared by every NLU stage.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>auction_demo.nlu.numbers.strip_accents</c> and
    /// <c>auction_demo.nlu.intents.normalize</c>. Accents are removed for <i>matching only</i>;
    /// canonical lexicon keys and all output text stay accented, because Vosk silently drops
    /// unaccented grammar entries.
    /// </remarks>
    public static class TextNormalization
    {
        /// <summary>
        /// Lower-cases and removes diacritics. Used for matching, never for output.
        /// </summary>
        public static string StripAccents(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text ?? string.Empty;
            }

            string decomposed = text.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            for (int i = 0; i < decomposed.Length; i++)
            {
                char c = decomposed[i];
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(c);
                }
            }

            return builder.ToString().ToLowerInvariant();
        }

        /// <summary>
        /// Lower-cases, unifies hyphens and dashes into spaces, collapses whitespace and trims.
        /// Digits are preserved.
        /// </summary>
        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(text.Length);
            bool pendingSpace = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '-' || c == '\u2014')
                {
                    c = ' ';
                }

                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = builder.Length > 0;
                    continue;
                }

                if (pendingSpace)
                {
                    builder.Append(' ');
                    pendingSpace = false;
                }

                builder.Append(char.ToLowerInvariant(c));
            }

            return builder.ToString();
        }

        /// <summary>
        /// True when the token is pure digits.
        /// </summary>
        public static bool IsDigits(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            for (int i = 0; i < token.Length; i++)
            {
                if (token[i] < '0' || token[i] > '9')
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// True when no character in the token is a letter or digit (pure punctuation).
        /// </summary>
        public static bool IsPunctuationOnly(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return true;
            }

            for (int i = 0; i < token.Length; i++)
            {
                if (char.IsLetterOrDigit(token[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private const string Punctuation = ".,;:!?\u00a1\u00bf\"'`()[]{}";

        /// <summary>
        /// Strips the punctuation that can cling to a whitespace-split token
        /// (<c>"cinquenta,"</c> becomes <c>"cinquenta"</c>), then lower-cases.
        /// </summary>
        public static string TrimToken(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return string.Empty;
            }

            int start = 0;
            int end = token.Length - 1;
            while (start <= end && Punctuation.IndexOf(token[start]) >= 0)
            {
                start++;
            }

            while (end >= start && Punctuation.IndexOf(token[end]) >= 0)
            {
                end--;
            }

            return token.Substring(start, end - start + 1).ToLowerInvariant();
        }
    }
}
