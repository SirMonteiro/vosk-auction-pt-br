using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AuctionVoice.Core
{
    /// <summary>
    /// One decoder hypothesis together with the parser's own score for it.
    /// </summary>
    /// <remarks>
    /// Vosk's confidence ordering is unreliable (it once ranked "mas vindo de" above
    /// "mais vinte"), so the parser re-scores every alternative and reports the winner.
    /// </remarks>
    public sealed class NBestItem
    {
        /// <summary>Raw transcript of this hypothesis.</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>Parser confidence in [0, 1] for this hypothesis.</summary>
        public double Score { get; set; }

        /// <summary>True for the hypothesis the parser selected.</summary>
        public bool Chosen { get; set; }
    }

    /// <summary>
    /// The outcome of parsing one utterance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Value"/> must be interpreted according to <see cref="Mode"/>:
    /// <see cref="ValueMode.Absolute"/> and <see cref="ValueMode.Sum"/> are absolute bids,
    /// <see cref="ValueMode.Delta"/> is added to the current lot price, and
    /// <see cref="ValueMode.Multiply"/> is already the multiplied result.
    /// </para>
    /// <para>
    /// The legacy flags (<see cref="HasAddition"/>, <see cref="HasSum"/>,
    /// <see cref="HasMultiplication"/>) are preserved for wire compatibility with the Python
    /// reference implementation. <see cref="Kind"/> is authoritative for game logic.
    /// </para>
    /// </remarks>
    public sealed class BidResult
    {
        /// <summary>Exactly what the recogniser produced.</summary>
        public string RawTranscript { get; set; } = string.Empty;

        /// <summary>Lower-cased, whitespace-collapsed text before the idiom pass.</summary>
        public string NormalizedText { get; set; } = string.Empty;

        /// <summary>Every hypothesis, with the parser's score and the selected one flagged.</summary>
        public List<NBestItem> NBest { get; } = new List<NBestItem>();

        /// <summary>
        /// Final numeric value, in reais. See <see cref="Mode"/>.
        /// </summary>
        /// <remarks>
        /// Stored as <see cref="double"/>, not <see cref="float"/>: a single-precision value
        /// cannot represent a bid above ~16.7 million reais to the nearest real, so
        /// <c>R$ 16.000.019,00</c> would print as <c>R$ 16.000.020,00</c>. The Python reference
        /// uses a double, and the two implementations must agree exactly.
        /// </remarks>
        public double Value { get; set; }

        /// <summary><see cref="Value"/> rounded to the nearest integer.</summary>
        public int IntValue => (int)Math.Round(Value, MidpointRounding.AwayFromZero);

        /// <summary>Formatted Brazilian currency, e.g. <c>R$ 1.234,56</c>.</summary>
        public string Currency => AuctionFormatting.FormatCurrency(Value);

        /// <summary>How <see cref="Value"/> must be applied.</summary>
        public ValueMode Mode { get; set; } = ValueMode.Absolute;

        /// <summary>Authoritative action category.</summary>
        public BidKind Kind { get; set; } = BidKind.Unknown;

        /// <summary>Legacy action category; always consistent with <see cref="Kind"/>.</summary>
        public ActionType Action => Kind.ToActionType();

        /// <summary>
        /// Legacy compatibility flag: true <b>only</b> for <see cref="ValueMode.Delta"/>, i.e. an
        /// incremental bid on the lot. Deliberately false for a spoken sum.
        /// </summary>
        public bool HasAddition => Mode == ValueMode.Delta;

        /// <summary>Legacy alias of <see cref="HasAddition"/>, kept identical for compatibility.</summary>
        public bool HasSum => HasAddition;

        /// <summary>True when a multiplier verb or noun was applied.</summary>
        public bool HasMultiplication { get; set; }

        /// <summary>True when the bidder explicitly spoke an arithmetic sum ("vinte mais vinte").</summary>
        public bool IsArithmeticSum => Mode == ValueMode.Sum;

        /// <summary>True when the value is an increment to be added to the current lot price.</summary>
        public bool IsIncremental => Mode == ValueMode.Delta;

        /// <summary>Numbers the parser found, in spoken order.</summary>
        public List<double> NumbersFound { get; } = new List<double>();

        /// <summary>Idiom phrases that were expanded, in match order.</summary>
        public List<string> IdiomsFound { get; } = new List<string>();

        /// <summary>Deterministic parse confidence in [0, 1].</summary>
        public double Confidence { get; set; }

        /// <summary>End-to-end parse time in milliseconds.</summary>
        public float LatencyMs { get; set; }

        /// <summary>Why a non-actionable result was rejected, or <see cref="BidRejectReason.None"/>.</summary>
        public BidRejectReason RejectReason { get; set; }

        /// <summary>Wire spelling of <see cref="RejectReason"/>, or <c>null</c> when actionable.</summary>
        public string Reason => RejectReason.ToWire();

        /// <summary>True when the speaker signalled "e fechando".</summary>
        public bool Closing { get; set; }

        /// <summary>Corrected value carried by a CORRECT intent, when one was spoken.</summary>
        public double? TargetValue { get; set; }

        /// <summary>Lot number carried by a LOT intent.</summary>
        public int? Lot { get; set; }

        /// <summary>Per-stage trace; empty unless debug parsing was requested.</summary>
        public IReadOnlyDictionary<string, string> Trace => _trace;

        private readonly Dictionary<string, string> _trace = new Dictionary<string, string>();

        /// <summary>True when the result should change game state.</summary>
        public bool IsActionable => Kind != BidKind.Unknown;

        /// <summary>Adds a trace entry. Ignored (and allocation-free) when debug is off.</summary>
        internal void AddTrace(string key, string value)
        {
            if (_trace != null && key != null)
            {
                _trace[key] = value;
            }
        }

        /// <summary>Adds a trace entry only when <paramref name="enabled"/> is true.</summary>
        internal void AddTrace(bool enabled, string key, string value)
        {
            if (enabled)
            {
                AddTrace(key, value);
            }
        }

        /// <summary>Compact single-line description used by the demo UI and logs.</summary>
        public override string ToString()
        {
            var builder = new StringBuilder(96);
            builder.Append(Kind.ToWire());
            builder.Append(' ').Append(Currency);
            builder.Append(" mode=").Append(Mode.ToWire());
            if (RejectReason != BidRejectReason.None)
            {
                builder.Append(" reason=").Append(Reason);
            }

            return builder.ToString();
        }
    }

    /// <summary>Brazilian currency formatting, matching the Python reference byte for byte.</summary>
    public static class AuctionFormatting
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        /// <summary>
        /// Formats a value as <c>R$ 1.234,56</c>: dot thousands, comma decimals, always two
        /// decimals. Zero prints <c>R$ 0,00</c>.
        /// </summary>
        public static string FormatCurrency(double value)
        {
            // "N2" under an invariant culture gives "1,234.56"; swap the separators.
            string formatted = value.ToString("N2", Invariant);
            var builder = new StringBuilder(formatted.Length + 4);
            builder.Append("R$ ");
            for (int i = 0; i < formatted.Length; i++)
            {
                char c = formatted[i];
                if (c == ',')
                {
                    builder.Append('\u0000');
                }
                else if (c == '.')
                {
                    builder.Append(',');
                }
                else
                {
                    builder.Append(c);
                }
            }

            return builder.Replace('\u0000', '.').ToString();
        }
    }
}
