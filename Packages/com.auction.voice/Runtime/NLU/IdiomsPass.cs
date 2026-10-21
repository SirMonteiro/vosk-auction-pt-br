using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace AuctionVoice.NLU
{
    /// <summary>
    /// Idiom and bingo phrase replacement.
    /// </summary>
    /// <remarks>
    /// Performs longest-match replacement of domain idioms, bingo nicknames and colloquial
    /// quantifiers into numeral tokens (<c>docs/03-nlu-spec.md</c> sections 1 and 2). The
    /// phrase table is ordered longest-first, so <c>dois patinhos na lagoa</c> wins over
    /// <c>dois patinhos</c>.
    /// </remarks>
    public static class IdiomsPass
    {
        private struct IdiomPattern
        {
            public string Phrase;
            public string Replacement;
            public Regex Pattern;
        }

        private static readonly IdiomPattern[] Patterns = BuildPatterns();

        private static IdiomPattern[] BuildPatterns()
        {
            var source = Lexicon.IdiomsLongestFirst;
            var result = new IdiomPattern[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                string phrase = source[i].Key;
                result[i] = new IdiomPattern
                {
                    Phrase = phrase,
                    Replacement = source[i].Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Pattern = new Regex(
                        @"(?<!\w)" + Regex.Escape(phrase) + @"(?!\w)",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                };
            }

            return result;
        }

        /// <summary>
        /// Replaces domain idioms with numeric tokens, returning the text and matched idioms.
        /// </summary>
        /// <param name="text">Normalised text.</param>
        /// <param name="found">Receives the matched phrases, in match order.</param>
        /// <returns>The rewritten text.</returns>
        public static string ReplaceIdioms(string text, out List<string> found)
        {
            found = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                return text ?? string.Empty;
            }

            string current = text;
            for (int i = 0; i < Patterns.Length; i++)
            {
                IdiomPattern idiom = Patterns[i];
                if (idiom.Pattern.IsMatch(current))
                {
                    current = idiom.Pattern.Replace(current, idiom.Replacement);
                    if (!found.Contains(idiom.Phrase))
                    {
                        found.Add(idiom.Phrase);
                    }
                }
            }

            return CollapseWhitespace(current);
        }

        internal static string CollapseWhitespace(string text)
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

                builder.Append(c);
            }

            return builder.ToString();
        }
    }
}
