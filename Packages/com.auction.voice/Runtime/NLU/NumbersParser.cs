using System;
using System.Collections.Generic;
using System.Text;

namespace AuctionVoice.NLU
{
    /// <summary>Scale class of a token. A lower number is a "smaller" magnitude class.</summary>
    public static class NumberScale
    {
        /// <summary>Connectors, currency words and note quantifiers.</summary>
        public const int Auxiliary = 0;

        /// <summary>Units (um..nove).</summary>
        public const int Unit = 1;

        /// <summary>Teens (dez..dezenove).</summary>
        public const int Teen = 2;

        /// <summary>Tens (vinte..noventa).</summary>
        public const int Ten = 3;

        /// <summary>Hundreds (cem, cento, duzentos..novecentos).</summary>
        public const int Hundred = 4;

        /// <summary>Scale words (mil, milhão, bilhão, trilhão).</summary>
        public const int Multiplier = 5;

        /// <summary>Banknote fauna and slang magnitudes.</summary>
        public const int Denomination = 6;

        /// <summary>Not a number word at all.</summary>
        public const int Unknown = -1;
    }

    /// <summary>What kind of contribution a token makes.</summary>
    public enum NumberTokenKind
    {
        /// <summary>Not recognised.</summary>
        Unknown = 0,

        /// <summary>A cardinal word (um, vinte, cento...).</summary>
        Base = 1,

        /// <summary>A literal digit string.</summary>
        Digit = 2,

        /// <summary>mil / milhão / bilhão / trilhão.</summary>
        Multiplier = 3,

        /// <summary>Banknote fauna (onça, lobo...).</summary>
        Denomination = 4,

        /// <summary>Slang magnitude (barão, pau, placa).</summary>
        Magnitude = 5,

        /// <summary>Currency synonym (reais, conto, pila...).</summary>
        Currency = 6,

        /// <summary>nota / cédula / moeda.</summary>
        Note = 7,

        /// <summary>e / de / com.</summary>
        Connector = 8,

        /// <summary>meia / meio.</summary>
        Half = 9
    }

    /// <summary>A classified token.</summary>
    public struct NumberToken
    {
        /// <summary>The token as it appeared in the input.</summary>
        public string Raw;

        /// <summary>Scale class, see <see cref="NumberScale"/>.</summary>
        public int Scale;

        /// <summary>Numeric contribution, when the token has one.</summary>
        public double Value;

        /// <summary>Token category.</summary>
        public NumberTokenKind Kind;

        /// <summary>True when <see cref="Value"/> carries meaning.</summary>
        public bool HasValue;
    }

    /// <summary>The result of parsing a token sequence.</summary>
    public sealed class ParseOutcome
    {
        /// <summary>Every number found, in spoken order.</summary>
        public readonly List<double> Values = new List<double>();

        /// <summary>Every token that was classified, in order.</summary>
        public readonly List<NumberToken> Tokens = new List<NumberToken>();

        /// <summary>Tokens the classifier could not recognise.</summary>
        public readonly List<string> Unknown = new List<string>();

        /// <summary>Sum of <see cref="Values"/>.</summary>
        public double Total
        {
            get
            {
                double sum = 0d;
                for (int i = 0; i < Values.Count; i++)
                {
                    sum += Values[i];
                }

                return sum;
            }
        }

        /// <summary>Last number found, or 0.</summary>
        public double Last => Values.Count > 0 ? Values[Values.Count - 1] : 0f;
    }

    /// <summary>
    /// Brazilian Portuguese cardinal parsing.
    /// </summary>
    /// <remarks>
    /// <para>Implements section 3 of <c>docs/03-nlu-spec.md</c>:</para>
    /// <list type="bullet">
    /// <item>tokenisation into scale classes (units, teens, tens, hundreds, multipliers)</item>
    /// <item>chunking, where an illegal scale regression starts a new number
    /// (<c>trinta e dez</c> is 30 then 10, not 40)</item>
    /// <item>cardinal evaluation with <c>mil</c> / <c>milhão</c> handling</item>
    /// <item>spelled digits (<c>sete seis</c> to 76), centavos, and the slang magnitudes
    /// (<c>barão</c> = 1000, <c>placa</c> = 100000, <c>conto</c>/<c>pila</c>/<c>prata</c> = 1)</item>
    /// </list>
    /// <para>
    /// The parser never throws on unknown input; unknown tokens are skipped and reported in
    /// <see cref="ParseOutcome.Unknown"/> for tracing.
    /// </para>
    /// </remarks>
    public static class NumbersParser
    {
        /// <summary>
        /// Maps one token to its scale class and numeric contribution. Attached punctuation is
        /// stripped (<c>cinquenta,</c> is <c>cinquenta</c>) because the tokeniser works on
        /// whitespace-split text.
        /// </summary>
        public static NumberToken Classify(string token)
        {
            string lowered = TextNormalization.TrimToken(token);

            if (lowered.Length > 0 && IsNumericLiteral(lowered))
            {
                double number = ParseLiteral(lowered);
                int scale;
                if (number < 10f)
                {
                    scale = NumberScale.Unit;
                }
                else if (number < 20f)
                {
                    scale = NumberScale.Teen;
                }
                else if (number < 100f)
                {
                    scale = NumberScale.Ten;
                }
                else if (number < 1000f)
                {
                    scale = NumberScale.Hundred;
                }
                else
                {
                    scale = NumberScale.Multiplier;
                }

                return new NumberToken
                {
                    Raw = token,
                    Scale = scale,
                    Value = number,
                    Kind = NumberTokenKind.Digit,
                    HasValue = true
                };
            }

            if (lowered == "meia" || lowered == "meio")
            {
                return new NumberToken
                {
                    Raw = token,
                    Scale = NumberScale.Unit,
                    Kind = NumberTokenKind.Half,
                    HasValue = false
                };
            }

            int intValue;
            if (Lexicon.SingleDigits.TryGetValue(lowered, out intValue))
            {
                return new NumberToken
                {
                    Raw = token,
                    Scale = NumberScale.Unit,
                    Value = intValue,
                    Kind = NumberTokenKind.Base,
                    HasValue = true
                };
            }

            if (Lexicon.Bases.TryGetValue(lowered, out intValue))
            {
                double value = intValue;
                int scale;
                if (value < 10f)
                {
                    scale = NumberScale.Unit;
                }
                else if (value < 20f)
                {
                    scale = NumberScale.Teen;
                }
                else if (value < 100f)
                {
                    scale = NumberScale.Ten;
                }
                else
                {
                    scale = NumberScale.Hundred;
                }

                return new NumberToken
                {
                    Raw = token,
                    Scale = scale,
                    Value = value,
                    Kind = NumberTokenKind.Base,
                    HasValue = true
                };
            }

            float floatValue;
            if (Lexicon.Multipliers.TryGetValue(lowered, out floatValue))
            {
                return new NumberToken
                {
                    Raw = token,
                    Scale = NumberScale.Multiplier,
                    Value = floatValue,
                    Kind = NumberTokenKind.Multiplier,
                    HasValue = true
                };
            }

            if (Lexicon.Denominations.TryGetValue(lowered, out floatValue))
            {
                return new NumberToken
                {
                    Raw = token,
                    Scale = NumberScale.Denomination,
                    Value = floatValue,
                    Kind = NumberTokenKind.Denomination,
                    HasValue = true
                };
            }

            if (Lexicon.Magnitudes.TryGetValue(lowered, out floatValue))
            {
                return new NumberToken
                {
                    Raw = token,
                    Scale = NumberScale.Denomination,
                    Value = floatValue,
                    Kind = NumberTokenKind.Magnitude,
                    HasValue = true
                };
            }

            if (Lexicon.CurrencyUnits.Contains(lowered))
            {
                return new NumberToken
                {
                    Raw = token,
                    Scale = NumberScale.Auxiliary,
                    Value = 1f,
                    Kind = NumberTokenKind.Currency,
                    HasValue = true
                };
            }

            if (Lexicon.NoteDenominators.Contains(lowered))
            {
                return new NumberToken
                {
                    Raw = token,
                    Scale = NumberScale.Auxiliary,
                    Kind = NumberTokenKind.Note,
                    HasValue = false
                };
            }

            if (lowered == "e" || lowered == "de" || lowered == "com")
            {
                return new NumberToken
                {
                    Raw = token,
                    Scale = NumberScale.Auxiliary,
                    Kind = NumberTokenKind.Connector,
                    HasValue = false
                };
            }

            return new NumberToken
            {
                Raw = token,
                Scale = NumberScale.Unknown,
                Kind = NumberTokenKind.Unknown,
                HasValue = false
            };
        }

        /// <summary>True when the token contributes any numeric meaning.</summary>
        public static bool IsNumberToken(string token)
        {
            return Classify(token).Kind != NumberTokenKind.Unknown;
        }

        private static bool IsNumericLiteral(string token)
        {
            bool seenSeparator = false;
            bool seenDigit = false;
            for (int i = 0; i < token.Length; i++)
            {
                char c = token[i];
                if (c >= '0' && c <= '9')
                {
                    seenDigit = true;
                }
                else if ((c == '.' || c == ',') && seenDigit && !seenSeparator)
                {
                    seenSeparator = true;
                }
                else
                {
                    return false;
                }
            }

            return seenDigit && token[token.Length - 1] != '.' && token[token.Length - 1] != ',';
        }

        private static double ParseLiteral(string token)
        {
            var builder = new StringBuilder(token.Length);
            for (int i = 0; i < token.Length; i++)
            {
                builder.Append(token[i] == ',' ? '.' : token[i]);
            }

            double parsed;
            return double.TryParse(
                builder.ToString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out parsed)
                ? parsed
                : 0d;
        }

        /// <summary>
        /// True when <paramref name="current"/> starts a new number after <paramref name="previous"/>.
        /// </summary>
        /// <remarks>
        /// The order of these checks matters: the specific ungrammatical pairs must be tested
        /// before the generic "descending magnitudes compose" rule, otherwise
        /// <c>trinta e dez</c> would be read as 40 instead of two numbers (30, 10).
        /// </remarks>
        private static bool IsBoundary(int previous, int current)
        {
            if (previous == NumberScale.Auxiliary || previous == NumberScale.Denomination)
            {
                return false;
            }

            if (current == NumberScale.Auxiliary)
            {
                return false;
            }

            // A count before a multiplier or denomination always composes:
            // "doze mil", "duas onças", "nove mil".
            if (current == NumberScale.Multiplier || current == NumberScale.Denomination)
            {
                return false;
            }

            // Ungrammatical pairs that speakers use to correct themselves. Checked *before*
            // the descending rule below.
            if (previous == NumberScale.Teen
                && (current == NumberScale.Unit
                    || current == NumberScale.Teen
                    || current == NumberScale.Ten
                    || current == NumberScale.Hundred))
            {
                return true;
            }

            if (previous == NumberScale.Ten
                && (current == NumberScale.Teen || current == NumberScale.Ten))
            {
                return true;
            }

            // Descending magnitudes compose: "mil duzentos", "cento e vinte".
            if (current < previous)
            {
                return false;
            }

            // Two bare units form a spelled-digit sequence ("sete seis").
            if (previous == NumberScale.Unit && current == NumberScale.Unit)
            {
                return false;
            }

            return true;
        }

        private static bool IsBareDigit(NumberToken token)
        {
            return (token.Kind == NumberTokenKind.Base || token.Kind == NumberTokenKind.Digit)
                   && token.Scale == NumberScale.Unit;
        }

        /// <summary>Evaluates one chunk of tokens into a single number.</summary>
        private static double EvaluateChunk(List<NumberToken> tokens)
        {
            if (tokens.Count == 0)
            {
                return 0d;
            }

            // Spelled digits: two or more bare digits with no connector concatenate
            // ("sete seis" -> 76, "cinco zero zero" -> 500).
            if (tokens.Count >= 2)
            {
                bool allBareDigits = true;
                bool hasConnector = false;
                for (int i = 0; i < tokens.Count; i++)
                {
                    NumberToken token = tokens[i];
                    if (!IsBareDigit(token))
                    {
                        allBareDigits = false;
                        break;
                    }

                    if (string.Equals(token.Raw, "e", StringComparison.OrdinalIgnoreCase))
                    {
                        hasConnector = true;
                    }
                }

                if (allBareDigits && !hasConnector)
                {
                    var digits = new StringBuilder(tokens.Count);
                    for (int i = 0; i < tokens.Count; i++)
                    {
                        digits.Append((int)(tokens[i].Value));
                    }

                    double concatenated;
                    return double.TryParse(
                        digits.ToString(),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out concatenated)
                        ? concatenated
                        : 0d;
                }
            }

            double total = 0d;
            double current = 0d;
            double lastMultiplier = 1d;
            bool inCents = false;
            double cents = 0d;
            bool hasNoteCount = false;
            double noteCount = 0d;

            int index = 0;
            while (index < tokens.Count)
            {
                NumberToken token = tokens[index];
                string lowered = token.Raw.ToLowerInvariant();
                NumberToken? following = index + 1 < tokens.Count ? tokens[index + 1] : (NumberToken?)null;

                if (token.Kind == NumberTokenKind.Note)
                {
                    noteCount = current != 0d ? current : 1d;
                    hasNoteCount = true;
                    current = 0f;
                    index++;
                    continue;
                }

                if (token.Kind == NumberTokenKind.Currency)
                {
                    if (lowered == "centavo" || lowered == "centavos")
                    {
                        if (inCents)
                        {
                            total += cents / 100d;
                            cents = 0d;
                            inCents = false;
                        }
                        else if (current != 0d)
                        {
                            total += current / 100d;
                            current = 0d;
                        }

                        inCents = true;
                    }
                    else
                    {
                        double scaled = current != 0d ? current : 0d;
                        if (hasNoteCount)
                        {
                            scaled *= noteCount;
                            hasNoteCount = false;
                        }

                        total += scaled;
                        current = 0d;
                        lastMultiplier = 1d;
                    }

                    index++;
                    continue;
                }

                if (token.Kind == NumberTokenKind.Half)
                {
                    // "meio pau" = 500, "meia dúzia" = 6: half of the *following* magnitude.
                    if (following.HasValue)
                    {
                        NumberToken next = following.Value;
                        if (next.Kind == NumberTokenKind.Multiplier
                            || next.Kind == NumberTokenKind.Denomination
                            || next.Kind == NumberTokenKind.Magnitude)
                        {
                            total += 0.5d * (next.HasValue ? next.Value : 1d);
                            lastMultiplier = next.HasValue ? next.Value : 1d;
                            current = 0d;
                            index += 2;
                            continue;
                        }
                    }

                    // "cinquenta e meia" = 50.5: half of the previous magnitude.
                    if (index > 0 && tokens[index - 1].Kind == NumberTokenKind.Connector)
                    {
                        if (lastMultiplier > 1d)
                        {
                            total += 0.5d * lastMultiplier;
                        }
                        else if (current != 0d)
                        {
                            current += 0.5d;
                        }
                        else if (total != 0d)
                        {
                            total += 0.5d;
                        }

                        index++;
                        continue;
                    }

                    // Standalone "meia" in bingo/auction means 6 ("meia dúzia" spoken alone).
                    current += 6d;
                    index++;
                    continue;
                }

                if (token.Kind == NumberTokenKind.Multiplier)
                {
                    double factor = token.HasValue ? token.Value : 1d;
                    double baseValue = current != 0d ? current : 1d;
                    total += baseValue * factor;
                    lastMultiplier = factor;
                    current = 0d;
                    index++;
                    continue;
                }

                if (token.Kind == NumberTokenKind.Denomination || token.Kind == NumberTokenKind.Magnitude)
                {
                    double factor = token.HasValue ? token.Value : 1d;
                    double count = current != 0d ? current : 1d;
                    total += count * factor;
                    lastMultiplier = factor;
                    current = 0d;
                    index++;
                    continue;
                }

                if (token.Kind == NumberTokenKind.Connector)
                {
                    index++;
                    continue;
                }

                // Plain base number or literal digit.
                double value = token.HasValue ? token.Value : 0d;
                if (inCents)
                {
                    cents += value;
                }
                else
                {
                    current += value;
                }

                index++;
            }

            total += current;
            if (cents != 0d)
            {
                total += cents / 100d;
            }

            return total;
        }

        /// <summary>
        /// Parses a token list into the numbers it contains.
        /// </summary>
        /// <remarks>
        /// <c>notas</c>/<c>cédulas</c>/<c>moedas</c> are a prefix quantifier: in
        /// <c>duas notas de cem</c> the <c>duas</c> multiplies the <c>cem</c> that follows. The
        /// count is carried across the connector tokens instead of being evaluated where it
        /// appears.
        /// </remarks>
        public static ParseOutcome ParseNumbers(IReadOnlyList<string> tokens)
        {
            var outcome = new ParseOutcome();
            if (tokens == null || tokens.Count == 0)
            {
                return outcome;
            }

            var chunk = new List<NumberToken>(8);
            int lastScale = NumberScale.Unknown;
            bool hasLastScale = false;

            bool pendingNote = false;
            double noteCount = 1d;

            for (int i = 0; i < tokens.Count; i++)
            {
                string raw = tokens[i];

                // Punctuation splits numbers ("50, 40, 20" is three bids, not one).
                if (string.IsNullOrEmpty(raw) || TextNormalization.IsPunctuationOnly(raw))
                {
                    Flush(outcome, chunk, ref lastScale, ref hasLastScale);
                    pendingNote = false;
                    continue;
                }

                NumberToken token = Classify(raw);
                outcome.Tokens.Add(token);

                if (token.Kind == NumberTokenKind.Note)
                {
                    // Remember the count that precedes the note word; it applies to the number
                    // that follows.
                    for (int c = chunk.Count - 1; c >= 0; c--)
                    {
                        NumberToken candidate = chunk[c];
                        if ((candidate.Kind == NumberTokenKind.Base
                             || candidate.Kind == NumberTokenKind.Digit
                             || candidate.Kind == NumberTokenKind.Multiplier)
                            && candidate.HasValue
                            && candidate.Value != 0d)
                        {
                            noteCount = candidate.Value;
                            chunk.RemoveAt(c);
                            break;
                        }
                    }

                    pendingNote = true;
                    continue;
                }

                if (token.Kind == NumberTokenKind.Unknown)
                {
                    outcome.Unknown.Add(raw);
                    Flush(outcome, chunk, ref lastScale, ref hasLastScale);
                    pendingNote = false;
                    continue;
                }

                if (token.Kind == NumberTokenKind.Connector)
                {
                    chunk.Add(token);
                    continue;
                }

                if (chunk.Count > 0 && hasLastScale && IsBoundary(lastScale, token.Scale))
                {
                    Flush(outcome, chunk, ref lastScale, ref hasLastScale);
                }

                if (pendingNote)
                {
                    // Emit the multiplication directly: "duas notas de cem" -> 200, not the
                    // digit-concatenation "2" + "cem" -> 210 that the spelled-digit fast path
                    // would produce.
                    if (token.Kind == NumberTokenKind.Base)
                    {
                        outcome.Values.Add(noteCount * (token.HasValue ? token.Value : 1d));
                        pendingNote = false;
                        noteCount = 1d;
                        chunk.Clear();
                        lastScale = NumberScale.Unknown;
                        hasLastScale = false;
                        continue;
                    }

                    // Non-numeric quantity ("duas notas velhas") falls back to the accumulated
                    // count.
                    chunk.Clear();
                    chunk.Add(new NumberToken
                    {
                        Raw = noteCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Scale = NumberScale.Unit,
                        Value = noteCount,
                        Kind = NumberTokenKind.Digit,
                        HasValue = true
                    });
                    chunk.Add(token);
                    lastScale = token.Scale;
                    hasLastScale = true;
                    pendingNote = false;
                    noteCount = 1d;
                    continue;
                }

                chunk.Add(token);
                lastScale = token.Scale;
                hasLastScale = true;
            }

            Flush(outcome, chunk, ref lastScale, ref hasLastScale);
            return outcome;
        }

        private static void Flush(
            ParseOutcome outcome,
            List<NumberToken> chunk,
            ref int lastScale,
            ref bool hasLastScale)
        {
            if (chunk.Count > 0)
            {
                double value = EvaluateChunk(chunk);
                if (value != 0d)
                {
                    outcome.Values.Add(value);
                }
            }

            chunk.Clear();
            lastScale = NumberScale.Unknown;
            hasLastScale = false;
        }

        /// <summary>
        /// Renders a cardinal in Portuguese. Used by tools and tests, not by the runtime.
        /// </summary>
        public static string NumberToWords(int value)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "negative values are not supported");
            }

            string[] units =
            {
                "zero", "um", "dois", "três", "quatro", "cinco", "seis", "sete", "oito", "nove"
            };
            string[] teens =
            {
                "dez", "onze", "doze", "treze", "quatorze", "quinze", "dezesseis", "dezessete",
                "dezoito", "dezenove"
            };
            string[] tens =
            {
                "", "", "vinte", "trinta", "quarenta", "cinquenta", "sessenta", "setenta",
                "oitenta", "noventa"
            };
            string[] hundreds =
            {
                "", "cento", "duzentos", "trezentos", "quatrocentos", "quinhentos", "seiscentos",
                "setecentos", "oitocentos", "novecentos"
            };

            if (value < 10)
            {
                return units[value];
            }

            if (value < 20)
            {
                return teens[value - 10];
            }

            if (value < 100)
            {
                int ten = value / 10;
                int unit = value % 10;
                return unit == 0 ? tens[ten] : tens[ten] + " e " + units[unit];
            }

            if (value < 1000)
            {
                if (value == 100)
                {
                    return "cem";
                }

                int hundred = value / 100;
                int rest = value % 100;
                string head = hundreds[hundred];
                return rest == 0 ? head : head + " e " + NumberToWords(rest);
            }

            if (value < 1000000)
            {
                int thousands = value / 1000;
                int rest = value % 1000;
                string head = thousands == 1 ? "mil" : NumberToWords(thousands) + " mil";
                return rest == 0 ? head : head + " e " + NumberToWords(rest);
            }

            if (value < 1000000000)
            {
                int millions = value / 1000000;
                int rest = value % 1000000;
                string head = millions == 1 ? "um milhão" : NumberToWords(millions) + " milhões";
                return rest == 0 ? head : head + " e " + NumberToWords(rest);
            }

            throw new ArgumentOutOfRangeException(
                nameof(value), "values above one billion are not supported");
        }
    }
}
