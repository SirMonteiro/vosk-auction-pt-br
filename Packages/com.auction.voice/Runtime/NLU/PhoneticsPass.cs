using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace AuctionVoice.NLU
{
    /// <summary>
    /// Phonetic repair pass.
    /// </summary>
    /// <remarks>
    /// <para>Implements section 7 of <c>docs/03-nlu-spec.md</c>:</para>
    /// <list type="bullet">
    /// <item>explicit guarded replacements for common ASR misrecognitions</item>
    /// <item>a protected token guard (stopwords and intent tokens are never modified)</item>
    /// <item>a similarity fallback with a margin, so no false positive lands on the stoplist</item>
    /// </list>
    /// <para>
    /// The old Whisper implementation repaired <c>quero</c> to <c>zero</c> and
    /// <c>sem lance</c> to <c>cem lance</c>, silently turning a pass into a bid. That must
    /// never happen here.
    /// </para>
    /// </remarks>
    public static class PhoneticsPass
    {
        /// <summary>One applied repair: the original span and what replaced it.</summary>
        public struct RepairRecord
        {
            /// <summary>Text that was replaced.</summary>
            public string Original;

            /// <summary>Text that replaced it.</summary>
            public string Replacement;
        }

        private sealed class ExplicitRule
        {
            public Regex Pattern;
            public string Replacement;
        }

        private static readonly ExplicitRule[] ExplicitRules = BuildRules();

        private static readonly HashSet<string> StopWordsNormalized = BuildStopWordsNormalized();

        private static readonly Dictionary<string, string> NumbersByNormalized = BuildNumbersByNormalized();

        private static readonly string[] NumberCandidates = BuildNumberCandidates();

        private static ExplicitRule[] BuildRules()
        {
            // Sandhi augmentatives, acoustic variants, context-guarded replacements and simple
            // word misrecognitions, in the exact order the Python reference applies them.
            return new[]
            {
                Rule(@"\bduza\s+ent[aã]o\b", "duzentão"),
                Rule(@"\bquinza\s+ent[aã]o\b", "quinhentão"),
                Rule(@"\btresent[aã]o\b", "trezentão"),
                Rule(@"\bseissentos\b", "seiscentos"),
                Rule(@"\bcinco\s+itinhas\b", "cinquentinha"),
                Rule(@"\b(?:mew|mil)\s*[- ]\s*zom\b", "milzão"),
                Rule(@"\bmuzinho\b", "milzinho"),
                Rule(@"\b(?:sixenta|sisenta|seicenta)\b", "sessenta"),
                Rule(@"\bnovent[ea]n(?:ove|[oó]i)\b", "noventa e nove"),
                Rule(@"\bnoventa\s+em\s+nove\b", "noventa e nove"),
                Rule(@"\bnovem(?=\s+mil\b)", "nove"),
                Rule(
                    @"\bmas(?=\s+(?:mil|\d+|um|uma|dois|duas|três|tres|quatro|cinco|seis|sete|oito|nove|dez|"
                    + "onze|doze|treze|quatorze|quinze|vinte|trinta|quarenta|cinquenta|sessenta|setenta|"
                    + "oitenta|noventa|cem|cento|duzentos|trezentos|quatrocentos|quinhentos|seiscentos|"
                    + @"setecentos|oitocentos|novecentos|reais|conto|pila|bar[aã]o|bar[oõ]es|placa|placas|pau|paus))\b",
                    "mais"),
                Rule(
                    @"\bsem\s+(mil|\d+|reais|real|conto|pila|bar[aã]o|placa|pau|unidades?)\b",
                    "cem $1"),
                Rule(
                    @"\b(\d+|um|dois|duas|três|tres|quatro|cinco|seis|sete|oito|nove|dez|"
                    + @"vinte|trinta|quarenta|cinquenta|sessenta|setenta|oitenta|noventa|cem|duzentos)\s+(?:ca|k[aá]|k)\b",
                    "$1 mil"),
                Rule(@"\b(?:vindes|v[ií]deo|vite)\b", "vinte")
            };
        }

        private static ExplicitRule Rule(string pattern, string replacement)
        {
            return new ExplicitRule
            {
                Pattern = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                Replacement = replacement
            };
        }

        private static HashSet<string> BuildStopWordsNormalized()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (string word in Lexicon.StopWords)
            {
                set.Add(TextNormalization.StripAccents(word));
            }

            return set;
        }

        private static Dictionary<string, string> BuildNumbersByNormalized()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> pair in Lexicon.NumbersByNormalizedForm)
            {
                map[pair.Key] = pair.Value;
            }

            return map;
        }

        private static string[] BuildNumberCandidates()
        {
            var keys = new string[NumbersByNormalized.Count];
            NumbersByNormalized.Keys.CopyTo(keys, 0);
            Array.Sort(keys, StringComparer.Ordinal);
            return keys;
        }

        /// <summary>
        /// Applies the repair rules and returns the repaired text plus every applied repair.
        /// </summary>
        public static string Repair(string text, out List<RepairRecord> repairs)
        {
            repairs = new List<RepairRecord>();
            if (string.IsNullOrEmpty(text))
            {
                return text ?? string.Empty;
            }

            string current = text;

            // 1. Explicit guarded replacements first.
            for (int i = 0; i < ExplicitRules.Length; i++)
            {
                ExplicitRule rule = ExplicitRules[i];
                Match match = rule.Pattern.Match(current);
                if (match.Success)
                {
                    string original = match.Value;
                    current = rule.Pattern.Replace(current, rule.Replacement);
                    repairs.Add(new RepairRecord { Original = original, Replacement = rule.Replacement });
                }
            }

            // 2. Token-by-token guarded fuzzy matching for OOV tokens.
            string[] tokens = current.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            bool modified = false;
            var rebuilt = new List<string>(tokens.Length);

            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i];
                string norm = TextNormalization.StripAccents(token);

                // Protected checks:
                // a) stopwords ("quero" -> "zero" is strictly forbidden)
                // b) short tokens
                // c) tokens already in the numeric lexicon
                // d) digits
                if (StopWordsNormalized.Contains(norm)
                    || norm.Length < 4
                    || NumbersByNormalized.ContainsKey(norm)
                    || IsAllDigits(token))
                {
                    rebuilt.Add(token);
                    continue;
                }

                string best;
                float bestScore;
                float secondScore;
                int matchCount = FindCloseMatches(norm, 2, 0.80f, out best, out bestScore, out secondScore);

                if (matchCount == 1)
                {
                    if (bestScore >= 0.80f)
                    {
                        string replacement = NumbersByNormalized[best];
                        rebuilt.Add(replacement);
                        repairs.Add(new RepairRecord { Original = token, Replacement = replacement });
                        modified = true;
                        continue;
                    }
                }
                else if (matchCount >= 2)
                {
                    if (bestScore >= 0.80f && bestScore - secondScore >= 0.08f)
                    {
                        string replacement = NumbersByNormalized[best];
                        rebuilt.Add(replacement);
                        repairs.Add(new RepairRecord { Original = token, Replacement = replacement });
                        modified = true;
                        continue;
                    }
                }

                rebuilt.Add(token);
            }

            if (!modified)
            {
                return current;
            }

            return string.Join(" ", rebuilt);
        }

        private static bool IsAllDigits(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            for (int i = 0; i < token.Length; i++)
            {
                if (!char.IsDigit(token[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Equivalent of Python's <c>difflib.get_close_matches(word, possibilities, n, cutoff)</c>.
        /// </summary>
        /// <returns>The number of matches found (0, 1 or 2).</returns>
        private static int FindCloseMatches(
            string word,
            int n,
            float cutoff,
            out string best,
            out float bestScore,
            out float secondScore)
        {
            best = null;
            bestScore = 0f;
            secondScore = 0f;

            var matches = new List<KeyValuePair<float, string>>(4);
            for (int i = 0; i < NumberCandidates.Length; i++)
            {
                string candidate = NumberCandidates[i];
                float score = SequenceMatcherRatio(candidate, word);
                if (score >= cutoff)
                {
                    matches.Add(new KeyValuePair<float, string>(score, candidate));
                }
            }

            if (matches.Count == 0)
            {
                return 0;
            }

            // heapq.nlargest(n, [(score, word), ...]): descending by score, then by the string
            // itself on ties, exactly as Python's tuple ordering does.
            matches.Sort((a, b) =>
            {
                int byScore = b.Key.CompareTo(a.Key);
                return byScore != 0 ? byScore : string.CompareOrdinal(b.Value, a.Value);
            });

            int count = Math.Min(n, matches.Count);
            best = matches[0].Value;
            bestScore = matches[0].Key;
            if (count >= 2)
            {
                secondScore = matches[1].Key;
            }

            return count;
        }

        /// <summary>
        /// <c>difflib.SequenceMatcher(None, a, b).ratio()</c>.
        /// </summary>
        /// <remarks>
        /// Ratio is <c>2 * M / T</c>, where M is the total size of the matching blocks found by
        /// the Ratcliff/Obershelp recursion and T is <c>len(a) + len(b)</c>. The autojunk
        /// heuristic is not applied: it only kicks in for sequences of 200+ elements, and every
        /// candidate here is a short word.
        /// </remarks>
        internal static float SequenceMatcherRatio(string a, string b)
        {
            int total = a.Length + b.Length;
            if (total == 0)
            {
                return 1f;
            }

            var blocks = new List<MatchBlock>(8);
            GetMatchingBlocks(a, b, 0, a.Length, 0, b.Length, blocks);

            int matched = 0;
            for (int i = 0; i < blocks.Count; i++)
            {
                matched += blocks[i].Size;
            }

            return 2f * matched / total;
        }

        private struct MatchBlock
        {
            public int A;
            public int B;
            public int Size;
        }

        private static void GetMatchingBlocks(
            string a,
            string b,
            int aLow,
            int aHigh,
            int bLow,
            int bHigh,
            List<MatchBlock> blocks)
        {
            int aBest;
            int bBest;
            int size = FindLongestMatch(a, b, aLow, aHigh, bLow, bHigh, out aBest, out bBest);

            if (size > 0)
            {
                GetMatchingBlocks(a, b, aLow, aBest, bLow, bBest, blocks);
                blocks.Add(new MatchBlock { A = aBest, B = bBest, Size = size });
                GetMatchingBlocks(
                    a, b, aBest + size, aHigh, bBest + size, bHigh, blocks);
            }
        }

        /// <summary>
        /// Finds the longest matching block, preferring the earliest position in <paramref name="a"/>
        /// and then in <paramref name="b"/>.
        /// </summary>
        private static int FindLongestMatch(
            string a,
            string b,
            int aLow,
            int aHigh,
            int bLow,
            int bHigh,
            out int aBest,
            out int bBest)
        {
            aBest = aLow;
            bBest = bLow;
            int bestSize = 0;

            // b2j: character -> ascending positions in b, restricted to [bLow, bHigh).
            var b2J = new Dictionary<char, List<int>>(bHigh - bLow);
            for (int j = bLow; j < bHigh; j++)
            {
                char c = b[j];
                List<int> positions;
                if (!b2J.TryGetValue(c, out positions))
                {
                    positions = new List<int>(4);
                    b2J[c] = positions;
                }

                positions.Add(j);
            }

            var j2Len = new Dictionary<int, int>(8);
            for (int i = aLow; i < aHigh; i++)
            {
                var newJ2Len = new Dictionary<int, int>(8);
                List<int> positions;
                if (b2J.TryGetValue(a[i], out positions))
                {
                    for (int p = 0; p < positions.Count; p++)
                    {
                        int j = positions[p];
                        if (j < bLow)
                        {
                            continue;
                        }

                        if (j >= bHigh)
                        {
                            break;
                        }

                        int previous;
                        j2Len.TryGetValue(j - 1, out previous);
                        int k = previous + 1;
                        newJ2Len[j] = k;
                        if (k > bestSize)
                        {
                            aBest = i - k + 1;
                            bBest = j - k + 1;
                            bestSize = k;
                        }
                    }
                }

                j2Len = newJ2Len;
            }

            return bestSize;
        }
    }
}
