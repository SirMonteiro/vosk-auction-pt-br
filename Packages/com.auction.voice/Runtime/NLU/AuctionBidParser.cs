using System;
using System.Collections.Generic;
using System.Diagnostics;
using AuctionVoice.Core;

namespace AuctionVoice.NLU
{
    /// <summary>Parses one transcript into a <see cref="Core.BidResult"/>.</summary>
    public interface INluParser
    {
        /// <summary>
        /// Parses one utterance. Pure and deterministic: no I/O, no model, no mutable state.
        /// </summary>
        /// <param name="rawTranscript">Exactly what the recogniser produced.</param>
        /// <param name="debug">When true, populates <see cref="Core.BidResult.Trace"/>.</param>
        Core.BidResult Parse(string rawTranscript, bool debug = false);

        /// <summary>
        /// Parses every decoder alternative and keeps the most confident valid one.
        /// </summary>
        /// <remarks>
        /// Vosk's own confidence ranking is unreliable, so the parser scores each hypothesis
        /// itself. Ties keep the decoder's order.
        /// </remarks>
        Core.BidResult ParseNBest(IReadOnlyList<string> texts, bool debug = false);
    }

    /// <summary>
    /// Bid parser: text in, <see cref="Core.BidResult"/> out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pure and deterministic: no I/O, no model, no globals beyond compiled tables. Implements
    /// section 5 of <c>docs/03-nlu-spec.md</c>.
    /// </para>
    /// <para>
    /// The semantics intentionally mirror <c>vosk-python</c>'s evaluator, the only
    /// implementation that scores 40/40 on the text suite:
    /// </para>
    /// <list type="bullet">
    /// <item>a single number is an absolute bid</item>
    /// <item>a leading marker (<c>mais</c>, <c>cobre</c>, ...) makes it incremental</item>
    /// <item>several numbers joined by <c>mais</c> are summed</item>
    /// <item>several numbers <i>not</i> joined are a correction: the last one wins</item>
    /// <item>a multiplier scales the number, or stands alone</item>
    /// </list>
    /// <para>
    /// The legacy Whisper parser dropped chained <c>mais</c>
    /// (<c>"mais dez mais dez"</c> to 10, <c>"mais vinte mais vinte mais sete"</c> to 7). This
    /// implementation keeps them, which is what makes it 40/40.
    /// </para>
    /// </remarks>
    public sealed class AuctionBidParser : INluParser
    {
        private readonly bool _debugByDefault;

        /// <summary>Creates a parser.</summary>
        /// <param name="debugByDefault">
        /// When true, every parse populates <see cref="Core.BidResult.Trace"/>. Off in release.
        /// </param>
        public AuctionBidParser(bool debugByDefault = false)
        {
            _debugByDefault = debugByDefault;
        }

        /// <summary>Shared stateless instance for callers that do not need a custom default.</summary>
        public static readonly AuctionBidParser Default = new AuctionBidParser();

        private static Core.BidResult Empty(string raw, Core.BidRejectReason reason)
        {
            return new Core.BidResult
            {
                RawTranscript = raw ?? string.Empty,
                NormalizedText = string.Empty,
                Value = 0f,
                Mode = Core.ValueMode.Absolute,
                Kind = Core.BidKind.Unknown,
                RejectReason = reason
            };
        }

        /// <summary>
        /// Deterministic parse confidence in [0, 1].
        /// </summary>
        /// <remarks>
        /// Rewards a valid bid and a clean token stream, penalises tokens the parser had to
        /// ignore. This is what ranks n-best alternatives, because Vosk's own confidence
        /// ordering is unreliable (<c>docs/08</c> section 8).
        /// </remarks>
        private static double Confidence(Core.BidResult parsed, int dropped, int repairs)
        {
            double score = 0d;
            Core.BidKind kind = parsed.Kind;
            if (kind.IsBid()
                || kind == Core.BidKind.MultiplyOnly
                || kind == Core.BidKind.Pass
                || kind == Core.BidKind.Correct
                || kind == Core.BidKind.Query
                || kind == Core.BidKind.Chant
                || kind == Core.BidKind.Lot)
            {
                score += 1d;
            }

            if (dropped == 0)
            {
                score += 0.3d;
            }

            if (repairs == 0)
            {
                score += 0.2d;
            }

            score -= Math.Min(0.5d, 0.1d * dropped);
            return Math.Max(0d, Math.Min(1d, score));
        }

        private static Core.BidKind KindFromMode(Core.ValueMode mode)
        {
            switch (mode)
            {
                case Core.ValueMode.Delta: return Core.BidKind.BidDelta;
                case Core.ValueMode.Sum: return Core.BidKind.BidSum;
                case Core.ValueMode.Multiply: return Core.BidKind.BidMultiply;
                default: return Core.BidKind.BidAbsolute;
            }
        }

        /// <inheritdoc />
        public Core.BidResult Parse(string rawTranscript, bool debug = false)
        {
            long started = Stopwatch.GetTimestamp();
            bool trace = debug || _debugByDefault;
            string raw = rawTranscript ?? string.Empty;

            if (raw.Trim().Length == 0)
            {
                Core.BidResult silent = Empty(raw, Core.BidRejectReason.Silence);
                silent.LatencyMs = ElapsedMs(started);
                return silent;
            }

            // 1. Normalise.
            string normalized = IntentsDetector.Normalize(raw);

            // 2. Idiom pass (longest-match replacement: bingo, fauna, colloquial quantifiers).
            List<string> idiomsFound;
            string afterIdioms = IdiomsPass.ReplaceIdioms(normalized, out idiomsFound);

            // 3. Phonetic pass (guarded acoustic and sandhi repairs).
            List<PhoneticsPass.RepairRecord> repairs;
            string repaired = PhoneticsPass.Repair(afterIdioms, out repairs);

            // 4. Intent pass (CHANT -> CORRECT -> PASS -> QUERY -> LOT -> MULTIPLY -> BID).
            Intent intent = IntentsDetector.Detect(repaired);

            // --- non-bid intents ------------------------------------------------
            if (intent.Name == IntentsDetector.Pass
                || intent.Name == IntentsDetector.Correct
                || intent.Name == IntentsDetector.Query
                || intent.Name == IntentsDetector.Chant)
            {
                var result = new Core.BidResult
                {
                    RawTranscript = raw,
                    NormalizedText = normalized,
                    Value = 0f,
                    Mode = Core.ValueMode.Absolute,
                    Kind = KindFromName(intent.Name),
                    Closing = IntentsDetector.ClosingMarker(normalized)
                };
                CopyIdioms(result, idiomsFound);

                // "errei, era cinquenta" carries the corrected value.
                if (intent.Name == IntentsDetector.Correct)
                {
                    ParseOutcome numbers = NumbersParser.ParseNumbers(SplitTokens(intent.Remainder));
                    if (numbers.Values.Count > 0)
                    {
                        result.TargetValue = numbers.Last;
                    }
                }

                result.Confidence = Confidence(result, 0, repairs.Count);
                Finish(result, started, trace, intent, idiomsFound, repairs);
                return result;
            }

            if (intent.Name == IntentsDetector.Lot)
            {
                var result = new Core.BidResult
                {
                    RawTranscript = raw,
                    NormalizedText = normalized,
                    Value = 0f,
                    Mode = Core.ValueMode.Absolute,
                    Kind = Core.BidKind.Lot,
                    Lot = (int)(intent.HasMultiplier ? intent.Multiplier : 0f)
                };
                CopyIdioms(result, idiomsFound);
                result.Confidence = Confidence(result, 0, repairs.Count);
                Finish(result, started, trace, intent, idiomsFound, repairs);
                return result;
            }

            // --- numbers --------------------------------------------------------
            List<string> fillers;
            string cleaned = IntentsDetector.StripFillers(intent.Remainder, out fillers);
            double? multiplier = intent.Name == IntentsDetector.Multiply && intent.HasMultiplier
                ? intent.Multiplier
                : (double?)null;
            string marker = IntentsDetector.DeltaMarker(cleaned);

            // Remove the marker before parsing so "mais vinte" is not "more 20". Handles both
            // leading markers ("mais vinte") and the trailing form ("mil em cima").
            string numberText = cleaned;
            if (!string.IsNullOrEmpty(marker))
            {
                if (cleaned.StartsWith(marker, StringComparison.Ordinal))
                {
                    numberText = cleaned.Substring(marker.Length).Trim();
                }
                else if (cleaned.EndsWith(marker, StringComparison.Ordinal))
                {
                    numberText = cleaned.Substring(0, cleaned.Length - marker.Length).Trim();
                }
            }

            ParseOutcome parsedNumbers = NumbersParser.ParseNumbers(SplitTokens(numberText));
            List<double> values = parsedNumbers.Values;
            int dropped = parsedNumbers.Unknown.Count + fillers.Count;

            string traceCleaned = trace ? cleaned : null;
            string traceMarker = trace ? marker ?? string.Empty : null;
            string traceValues = trace ? JoinFloats(values) : null;
            string traceUnknown = trace ? string.Join(",", parsedNumbers.Unknown.ToArray()) : null;
            string traceFillers = trace ? string.Join(",", fillers.ToArray()) : null;
            string traceMultiplier = trace && multiplier.HasValue
                ? multiplier.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                : null;

            // --- multiplier with no number: "dobro" ------------------------------
            if (values.Count == 0 && multiplier.HasValue)
            {
                double factor = multiplier.Value;
                var result = new Core.BidResult
                {
                    RawTranscript = raw,
                    NormalizedText = normalized,
                    Value = factor,
                    Mode = Core.ValueMode.Multiply,
                    Kind = Core.BidKind.MultiplyOnly,
                    HasMultiplication = true
                };
                CopyIdioms(result, idiomsFound);
                result.Confidence = Confidence(result, dropped, repairs.Count);
                Finish(result, started, trace, intent, idiomsFound, repairs);
                AddNumberTrace(result, trace, traceCleaned, traceMarker, traceValues,
                    traceUnknown, traceFillers, traceMultiplier);
                return result;
            }

            // --- no numbers at all -----------------------------------------------
            if (values.Count == 0)
            {
                Core.BidResult result = Empty(raw, Core.BidRejectReason.Unknown);
                result.NormalizedText = normalized;
                CopyIdioms(result, idiomsFound);
                Finish(result, started, trace, intent, idiomsFound, repairs);
                AddNumberTrace(result, trace, traceCleaned, traceMarker, traceValues,
                    traceUnknown, traceFillers, traceMultiplier);
                return result;
            }

            // --- value assembly ---------------------------------------------------
            int plusCount = CountOccurrences(cleaned, " mais ");
            if (marker == "mais")
            {
                plusCount++;
            }

            double value;
            Core.ValueMode mode;

            if (values.Count == 1)
            {
                value = values[0];
                mode = marker != null ? Core.ValueMode.Delta : Core.ValueMode.Absolute;
            }
            else if (marker != null && plusCount >= values.Count)
            {
                // "mais vinte mais vinte mais sete": an incremental chain.
                value = Sum(values);
                mode = Core.ValueMode.Delta;
            }
            else if (plusCount == values.Count - 1)
            {
                // "vinte mais vinte": a sum stated as one bid.
                value = Sum(values);
                mode = Core.ValueMode.Sum;
            }
            else
            {
                // Numbers without connectors: the speaker corrected themselves.
                value = values[values.Count - 1];
                mode = Core.ValueMode.Absolute;
            }

            bool hasMultiplication = multiplier.HasValue;
            if (multiplier.HasValue)
            {
                value *= multiplier.Value;
                mode = Core.ValueMode.Multiply;
            }

            var bid = new Core.BidResult
            {
                RawTranscript = raw,
                NormalizedText = normalized,
                Value = value,
                Mode = mode,
                Kind = KindFromMode(mode),
                HasMultiplication = hasMultiplication,
                Closing = IntentsDetector.ClosingMarker(normalized)
            };

            for (int i = 0; i < values.Count; i++)
            {
                bid.NumbersFound.Add(values[i]);
            }

            CopyIdioms(bid, idiomsFound);
            bid.Confidence = Confidence(bid, dropped, repairs.Count);
            Finish(bid, started, trace, intent, idiomsFound, repairs);
            AddNumberTrace(bid, trace, traceCleaned, traceMarker, traceValues,
                traceUnknown, traceFillers, traceMultiplier);
            return bid;
        }

        /// <inheritdoc />
        public Core.BidResult ParseNBest(IReadOnlyList<string> texts, bool debug = false)
        {
            if (texts == null || texts.Count == 0)
            {
                return Empty(string.Empty, Core.BidRejectReason.Silence);
            }

            Core.BidResult best = null;
            int bestIndex = -1;
            var items = new List<Core.NBestItem>(texts.Count);

            for (int i = 0; i < texts.Count; i++)
            {
                Core.BidResult parsed = Parse(texts[i], debug);
                items.Add(new Core.NBestItem
                {
                    Text = texts[i],
                    Score = parsed.Confidence,
                    Chosen = false
                });

                if (best == null || parsed.Confidence > best.Confidence)
                {
                    best = parsed;
                    bestIndex = i;
                }
            }

            for (int i = 0; i < items.Count; i++)
            {
                items[i].Chosen = i == bestIndex;
            }

            best.NBest.Clear();
            best.NBest.AddRange(items);
            return best;
        }

        private static Core.BidKind KindFromName(string intentName)
        {
            switch (intentName)
            {
                case IntentsDetector.Pass: return Core.BidKind.Pass;
                case IntentsDetector.Correct: return Core.BidKind.Correct;
                case IntentsDetector.Query: return Core.BidKind.Query;
                case IntentsDetector.Chant: return Core.BidKind.Chant;
                case IntentsDetector.Lot: return Core.BidKind.Lot;
                default: return Core.BidKind.Unknown;
            }
        }

        private static void CopyIdioms(Core.BidResult result, List<string> idiomsFound)
        {
            if (idiomsFound == null)
            {
                return;
            }

            for (int i = 0; i < idiomsFound.Count; i++)
            {
                result.IdiomsFound.Add(idiomsFound[i]);
            }
        }

        private static void AddNumberTrace(
            Core.BidResult result,
            bool trace,
            string cleaned,
            string marker,
            string values,
            string unknown,
            string fillers,
            string multiplier)
        {
            if (!trace)
            {
                return;
            }

            result.AddTrace("cleaned", cleaned);
            result.AddTrace("marker", marker);
            result.AddTrace("values", values);
            result.AddTrace("unknown", unknown);
            result.AddTrace("fillers", fillers);
            result.AddTrace("multiplier", multiplier);
        }

        private static void Finish(
            Core.BidResult result,
            long started,
            bool trace,
            Intent intent,
            List<string> idiomsFound,
            List<PhoneticsPass.RepairRecord> repairs)
        {
            result.LatencyMs = ElapsedMs(started);
            if (!trace)
            {
                return;
            }

            result.AddTrace("intent", intent.Name);
            result.AddTrace("matched", intent.Matched ?? string.Empty);
            result.AddTrace("idioms_found", string.Join(",", idiomsFound.ToArray()));
            var repaired = new List<string>(repairs.Count);
            for (int i = 0; i < repairs.Count; i++)
            {
                repaired.Add(repairs[i].Original + "->" + repairs[i].Replacement);
            }

            result.AddTrace("repairs", string.Join(",", repaired.ToArray()));
        }

        private static float ElapsedMs(long started)
        {
            return (float)((Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency);
        }

        private static string[] SplitTokens(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Array.Empty<string>();
            }

            return text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static double Sum(List<double> values)
        {
            double total = 0d;
            for (int i = 0; i < values.Count; i++)
            {
                total += values[i];
            }

            return total;
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle))
            {
                return 0;
            }

            int count = 0;
            int index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }

        private static string JoinFloats(List<double> values)
        {
            var parts = new string[values.Count];
            for (int i = 0; i < values.Count; i++)
            {
                parts[i] = values[i].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            }

            return string.Join(",", parts);
        }
    }
}
