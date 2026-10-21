using System;

namespace AuctionVoice.Core
{
    /// <summary>
    /// Specifies how the parsed bid value relates to the current auction state.
    /// </summary>
    /// <remarks>
    /// This is the authoritative interpretation hint. The game must switch on
    /// <see cref="BidKind"/> for the action and on <see cref="ValueMode"/> for how to apply
    /// <see cref="BidResult.Value"/>.
    /// </remarks>
    public enum ValueMode
    {
        /// <summary>Explicit absolute bid value (e.g. "cinquenta" -> R$ 50).</summary>
        Absolute = 0,

        /// <summary>Incremental bid added to the current lot price (e.g. "mais cinquenta").</summary>
        Delta = 1,

        /// <summary>Arithmetic sum spoken by the bidder (e.g. "vinte mais vinte" -> R$ 40).</summary>
        Sum = 2,

        /// <summary>Multiplied bid value (e.g. "o dobro de cem" -> R$ 200).</summary>
        Multiply = 3
    }

    /// <summary>
    /// Authoritative, fine-grained categorisation of a player utterance.
    /// </summary>
    /// <remarks>
    /// Prefer this over <see cref="ActionType"/>. The latter collapses the four bid flavours
    /// onto a single <c>BID</c> value and exists only for wire compatibility with the Python
    /// reference implementation.
    /// </remarks>
    public enum BidKind
    {
        /// <summary>No usable meaning (silence, filler only, unknown words).</summary>
        Unknown = 0,

        /// <summary>A single number with no leading marker: the bid is exactly the value.</summary>
        BidAbsolute = 1,

        /// <summary>A leading marker ("mais", "cobre", "subo", ...): add the value to the lot.</summary>
        BidDelta = 2,

        /// <summary>Numbers joined by "mais": the spoken sum is the absolute bid.</summary>
        BidSum = 3,

        /// <summary>A multiplier was applied to a parsed number.</summary>
        BidMultiply = 4,

        /// <summary>A multiplier with no number ("dobro"): multiply the current lot price.</summary>
        MultiplyOnly = 5,

        /// <summary>The bidder is out ("passo", "sem lance").</summary>
        Pass = 6,

        /// <summary>The bidder retracts or amends a previous bid.</summary>
        Correct = 7,

        /// <summary>The auctioneer's chant ("dou-lhe uma", "vendido").</summary>
        Chant = 8,

        /// <summary>A question about the current state ("quanto ta").</summary>
        Query = 9,

        /// <summary>The bidder names a lot ("lote tres").</summary>
        Lot = 10
    }

    /// <summary>
    /// Legacy four-value vocabulary, extended with the new intents. Kept because the Python
    /// reference implementation and the JSON contract both expose it.
    /// </summary>
    public enum ActionType
    {
        /// <summary>No action.</summary>
        None = 0,

        /// <summary>Any of the four bid flavours.</summary>
        Bid = 1,

        /// <summary>A bare multiplier such as "dobro".</summary>
        MultiplyOnly = 2,

        /// <summary>The bidder is out.</summary>
        Pass = 3,

        /// <summary>The bidder corrects a previous bid.</summary>
        Correct = 4,

        /// <summary>A question.</summary>
        Query = 5,

        /// <summary>The auctioneer's chant.</summary>
        Chant = 6,

        /// <summary>A lot selection.</summary>
        Lot = 7
    }

    /// <summary>
    /// Machine-readable reasons attached to a rejected (non-actionable) result.
    /// </summary>
    public enum BidRejectReason
    {
        /// <summary>No reason: the result is actionable.</summary>
        None = 0,

        /// <summary>The transcript was empty.</summary>
        Silence = 1,

        /// <summary>The captured audio was shorter than the configured minimum.</summary>
        TooShort = 2,

        /// <summary>The audio was below the RMS gate.</summary>
        NoSpeech = 3,

        /// <summary>Words were heard but no number or intent could be recognised.</summary>
        Unknown = 4,

        /// <summary>The utterance implies a delta but no current bid was supplied.</summary>
        NeedsCurrentBid = 5
    }

    /// <summary>
    /// Wire-name helpers. The string spellings are a contract shared with the Python
    /// implementation and with <c>docs/unity-handoff.md</c>; never rename them silently.
    /// </summary>
    public static class AuctionEnumNames
    {
        /// <summary>Lower-case wire name of a <see cref="Core.ValueMode"/>.</summary>
        public static string ToWire(this ValueMode mode)
        {
            switch (mode)
            {
                case ValueMode.Delta: return "delta";
                case ValueMode.Sum: return "sum";
                case ValueMode.Multiply: return "multiply";
                default: return "absolute";
            }
        }

        /// <summary>Upper-case wire name of a <see cref="Core.BidKind"/>.</summary>
        public static string ToWire(this BidKind kind)
        {
            switch (kind)
            {
                case BidKind.BidAbsolute: return "BID_ABSOLUTE";
                case BidKind.BidDelta: return "BID_DELTA";
                case BidKind.BidSum: return "BID_SUM";
                case BidKind.BidMultiply: return "BID_MULTIPLY";
                case BidKind.MultiplyOnly: return "MULTIPLY_ONLY";
                case BidKind.Pass: return "PASS";
                case BidKind.Correct: return "CORRECT";
                case BidKind.Chant: return "CHANT";
                case BidKind.Query: return "QUERY";
                case BidKind.Lot: return "LOT";
                default: return "UNKNOWN";
            }
        }

        /// <summary>Upper-case wire name of an <see cref="Core.ActionType"/>.</summary>
        public static string ToWire(this ActionType action)
        {
            switch (action)
            {
                case ActionType.Bid: return "BID";
                case ActionType.MultiplyOnly: return "MULTIPLY_ONLY";
                case ActionType.Pass: return "PASS";
                case ActionType.Correct: return "CORRECT";
                case ActionType.Query: return "QUERY";
                case ActionType.Chant: return "CHANT";
                case ActionType.Lot: return "LOT";
                default: return "NONE";
            }
        }

        /// <summary>Lower-case wire name of a <see cref="BidRejectReason"/>.</summary>
        public static string ToWire(this BidRejectReason reason)
        {
            switch (reason)
            {
                case BidRejectReason.Silence: return "silence";
                case BidRejectReason.TooShort: return "too_short";
                case BidRejectReason.NoSpeech: return "no_speech";
                case BidRejectReason.Unknown: return "unknown";
                case BidRejectReason.NeedsCurrentBid: return "needs_current_bid";
                default: return null;
            }
        }

        /// <summary>
        /// Legacy <see cref="ActionType"/> for a <see cref="BidKind"/>: every bid flavour
        /// collapses onto <see cref="ActionType.Bid"/>.
        /// </summary>
        public static ActionType ToActionType(this BidKind kind)
        {
            switch (kind)
            {
                case BidKind.BidAbsolute:
                case BidKind.BidDelta:
                case BidKind.BidSum:
                case BidKind.BidMultiply:
                    return ActionType.Bid;
                case BidKind.MultiplyOnly: return ActionType.MultiplyOnly;
                case BidKind.Pass: return ActionType.Pass;
                case BidKind.Correct: return ActionType.Correct;
                case BidKind.Chant: return ActionType.Chant;
                case BidKind.Query: return ActionType.Query;
                case BidKind.Lot: return ActionType.Lot;
                default: return ActionType.None;
            }
        }

        /// <summary>True for the four bid flavours.</summary>
        public static bool IsBid(this BidKind kind)
        {
            return kind == BidKind.BidAbsolute
                   || kind == BidKind.BidDelta
                   || kind == BidKind.BidSum
                   || kind == BidKind.BidMultiply;
        }
    }
}
