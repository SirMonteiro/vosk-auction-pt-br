using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AuctionVoice.NLU
{
    /// <summary>
    /// A detected intent plus the span that triggered it.
    /// </summary>
    public struct Intent
    {
        /// <summary>One of CHANT, CORRECT, PASS, QUERY, LOT, MULTIPLY, BID.</summary>
        public string Name;

        /// <summary>The exact text span that triggered the intent.</summary>
        public string Matched;

        /// <summary>The text with the matched span removed.</summary>
        public string Remainder;

        /// <summary>Multiplier factor for MULTIPLY, lot number for LOT, otherwise 0.</summary>
        public double Multiplier;

        /// <summary>True when <see cref="Multiplier"/> carries meaning.</summary>
        public bool HasMultiplier;
    }

    /// <summary>
    /// Intent detection.
    /// </summary>
    /// <remarks>
    /// The order is fixed and matters: <c>dou-lhe uma</c> must be recognised as the
    /// auctioneer's chant before <c>uma</c> can become a bid, and <c>sem lance</c> must be a
    /// PASS before any phonetic repair can turn <c>sem</c> into <c>cem</c>.
    /// </remarks>
    public static class IntentsDetector
    {
        /// <summary>Intent name: the auctioneer's chant.</summary>
        public const string Chant = "CHANT";

        /// <summary>Intent name: retract or amend a previous bid.</summary>
        public const string Correct = "CORRECT";

        /// <summary>Intent name: the bidder is out.</summary>
        public const string Pass = "PASS";

        /// <summary>Intent name: a question about the current state.</summary>
        public const string Query = "QUERY";

        /// <summary>Intent name: a lot selection.</summary>
        public const string Lot = "LOT";

        /// <summary>Intent name: a multiplier was spoken.</summary>
        public const string Multiply = "MULTIPLY";

        /// <summary>Intent name: numbers are the parser's job.</summary>
        public const string Bid = "BID";

        private static readonly Regex PassRegex = PhrasePattern(Lexicon.PassPhrases);
        private static readonly Regex CorrectRegex = PhrasePattern(Lexicon.CorrectPhrases);
        private static readonly Regex QueryRegex = PhrasePattern(Lexicon.QueryPhrases);
        private static readonly Regex ChantRegex = PhrasePattern(Lexicon.ChantPhrases);
        private static readonly Regex FillerRegex = PhrasePattern(Lexicon.Fillers);

        private static readonly Regex LotRegex = new Regex(
            @"(?<!\w)(?:lote|item)\s+(\d+|um|uma|dois|duas|três|tres|quatro|cinco|seis|sete|oito|nove|dez)(?!\w)",
            RegexOptions.CultureInvariant);

        private static readonly Regex OrdinalLotRegex = new Regex(
            @"(?<!\w)(primeiro|segundo|terceiro|quarto|quinto)\s+lote(?!\w)",
            RegexOptions.CultureInvariant);

        private static readonly Regex ClosingRegex = new Regex(
            @"(?<!\w)e\s+fechando(?!\w)", RegexOptions.CultureInvariant);

        private static readonly Dictionary<string, int> Ordinals = new Dictionary<string, int>
        {
            { "primeiro", 1 },
            { "segundo", 2 },
            { "terceiro", 3 },
            { "quarto", 4 },
            { "quinto", 5 }
        };

        private static readonly Dictionary<string, int> WordNumbers = new Dictionary<string, int>
        {
            { "um", 1 },
            { "uma", 1 },
            { "dois", 2 },
            { "duas", 2 },
            { "três", 3 },
            { "tres", 3 },
            { "quatro", 4 },
            { "cinco", 5 },
            { "seis", 6 },
            { "sete", 7 },
            { "oito", 8 },
            { "nove", 9 },
            { "dez", 10 }
        };

        private static readonly Regex MultiplierRegex = BuildMultiplierRegex();

        private static Regex BuildMultiplierRegex()
        {
            // Longest-first so "quadruplicar" beats "duplicar" when both could match.
            var words = new List<string>(Lexicon.MultiplierWords.Keys);
            words.Sort((a, b) => b.Length.CompareTo(a.Length));
            var builder = new System.Text.StringBuilder(@"(?<!\w)(");
            for (int i = 0; i < words.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append('|');
                }

                builder.Append(Regex.Escape(words[i]));
            }

            builder.Append(@")(?!\w)");
            return new Regex(builder.ToString(), RegexOptions.CultureInvariant);
        }

        /// <summary>
        /// Compiles an alternation of phrases, longest first so <c>sem lance</c> beats
        /// <c>lance</c>.
        /// </summary>
        private static Regex PhrasePattern(IEnumerable<string> phrases)
        {
            var ordered = new List<string>(phrases);
            ordered.Sort((a, b) => b.Length.CompareTo(a.Length));
            var builder = new System.Text.StringBuilder(@"(?<!\w)(");
            for (int i = 0; i < ordered.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append('|');
                }

                builder.Append(Regex.Escape(ordered[i]));
            }

            builder.Append(@")(?!\w)");
            return new Regex(builder.ToString(), RegexOptions.CultureInvariant);
        }

        /// <summary>
        /// Lower-cases, unifies hyphens and dashes, collapses whitespace and trims. Keeps digits.
        /// </summary>
        public static string Normalize(string text)
        {
            return TextNormalization.Normalize(text);
        }

        private static string StripMatch(string text, Match match)
        {
            string head = text.Substring(0, match.Index);
            string tail = text.Substring(match.Index + match.Length);
            return (head + " " + tail).Trim();
        }

        /// <summary>
        /// Detects the highest-priority intent in <paramref name="text"/>.
        /// </summary>
        /// <remarks>
        /// Returns <c>BID</c> with an empty match when nothing else fires, leaving the numeric
        /// work to the parser.
        /// </remarks>
        public static Intent Detect(string text)
        {
            string normalized = Normalize(text);
            Match match;

            // 1. CHANT
            match = ChantRegex.Match(normalized);
            if (match.Success)
            {
                return new Intent
                {
                    Name = Chant,
                    Matched = match.Groups[1].Value,
                    Remainder = StripMatch(normalized, match)
                };
            }

            // 2. CORRECT
            match = CorrectRegex.Match(normalized);
            if (match.Success)
            {
                return new Intent
                {
                    Name = Correct,
                    Matched = match.Groups[1].Value,
                    Remainder = StripMatch(normalized, match)
                };
            }

            // 3. PASS
            match = PassRegex.Match(normalized);
            if (match.Success)
            {
                return new Intent
                {
                    Name = Pass,
                    Matched = match.Groups[1].Value,
                    Remainder = StripMatch(normalized, match)
                };
            }

            // 4. QUERY
            match = QueryRegex.Match(normalized);
            if (match.Success)
            {
                return new Intent
                {
                    Name = Query,
                    Matched = match.Groups[1].Value,
                    Remainder = StripMatch(normalized, match)
                };
            }

            // 5. LOT
            match = LotRegex.Match(normalized);
            if (match.Success)
            {
                string token = match.Groups[1].Value;
                int number;
                if (!TextNormalization.IsDigits(token))
                {
                    WordNumbers.TryGetValue(token, out number);
                }
                else
                {
                    int.TryParse(token, out number);
                }

                return new Intent
                {
                    Name = Lot,
                    Matched = match.Value,
                    Remainder = StripMatch(normalized, match),
                    Multiplier = number,
                    HasMultiplier = true
                };
            }

            match = OrdinalLotRegex.Match(normalized);
            if (match.Success)
            {
                return new Intent
                {
                    Name = Lot,
                    Matched = match.Value,
                    Remainder = StripMatch(normalized, match),
                    Multiplier = Ordinals[match.Groups[1].Value],
                    HasMultiplier = true
                };
            }

            // 6. MULTIPLY
            match = MultiplierRegex.Match(normalized);
            if (match.Success)
            {
                return new Intent
                {
                    Name = Multiply,
                    Matched = match.Groups[1].Value,
                    Remainder = StripMatch(normalized, match),
                    Multiplier = Lexicon.MultiplierWords[match.Groups[1].Value],
                    HasMultiplier = true
                };
            }

            // 7. BID (numbers are the parser's job)
            return new Intent
            {
                Name = Bid,
                Matched = string.Empty,
                Remainder = normalized
            };
        }

        /// <summary>
        /// Removes filler words, returning the cleaned text and what was removed.
        /// </summary>
        public static string StripFillers(string text, out List<string> removed)
        {
            removed = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var captured = removed;
            string cleaned = FillerRegex.Replace(text, match =>
            {
                captured.Add(match.Groups[1].Value);
                return " ";
            });

            return IdiomsPass.CollapseWhitespace(cleaned);
        }

        /// <summary>
        /// Returns the incremental marker (<c>mais</c>, <c>cobre</c>, <c>em cima</c>, ...), if any.
        /// </summary>
        /// <remarks>
        /// Handles both leading markers (<c>mais vinte</c>) and the trailing
        /// <c>mil em cima</c> form.
        /// </remarks>
        public static string DeltaMarker(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            string[] tokens = text.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                return null;
            }

            if (tokens.Length >= 2 && tokens[tokens.Length - 2] == "em" && tokens[tokens.Length - 1] == "cima")
            {
                return "em cima";
            }

            if (Lexicon.DeltaMarkers.Contains(tokens[0]))
            {
                return tokens[0];
            }

            if (tokens.Length >= 2)
            {
                string twoWord = tokens[0] + " " + tokens[1];
                if (Lexicon.DeltaMarkers.Contains(twoWord))
                {
                    return twoWord;
                }
            }

            return null;
        }

        /// <summary>True when the speaker signalled they are closing the lot ("e fechando").</summary>
        public static bool ClosingMarker(string text)
        {
            return !string.IsNullOrEmpty(text) && ClosingRegex.IsMatch(text);
        }

        /// <summary>True when the token is a stopword (never phonetically repaired).</summary>
        public static bool IsStopwordToken(string token)
        {
            return Lexicon.StopWords.Contains(TextNormalization.StripAccents(token));
        }
    }
}
