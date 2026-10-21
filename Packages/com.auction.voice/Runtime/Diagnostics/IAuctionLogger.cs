using System;

namespace AuctionVoice.Diagnostics
{
    /// <summary>
    /// Guarded logging surface for the voice pipeline.
    /// </summary>
    /// <remarks>
    /// Every implementation must be allocation-free when debug logging is disabled: the audio
    /// path calls into this interface on the hot path.
    /// </remarks>
    public interface IAuctionLogger
    {
        /// <summary>When false, no string is built and nothing is emitted.</summary>
        bool IsDebugEnabled { get; set; }

        /// <summary>Informational message. Skipped in release builds unless debug is enabled.</summary>
        void LogInfo(string message);

        /// <summary>Warning message. Always emitted, but never on the per-frame path.</summary>
        void LogWarning(string message);

        /// <summary>Reports one finalised bid together with its latency.</summary>
        void LogBidResult(AuctionVoice.Core.BidResult result, long latencyMs);

        /// <summary>Reports a partial transcript. Called at most once per changed partial.</summary>
        void LogPartial(string text);
    }

    /// <summary>
    /// A logger that discards everything. Useful for tests and headless servers.
    /// </summary>
    public sealed class NullAuctionLogger : IAuctionLogger
    {
        /// <summary>Shared instance.</summary>
        public static readonly NullAuctionLogger Instance = new NullAuctionLogger();

        /// <inheritdoc />
        public bool IsDebugEnabled { get; set; }

        /// <inheritdoc />
        public void LogInfo(string message)
        {
        }

        /// <inheritdoc />
        public void LogWarning(string message)
        {
        }

        /// <inheritdoc />
        public void LogBidResult(AuctionVoice.Core.BidResult result, long latencyMs)
        {
        }

        /// <inheritdoc />
        public void LogPartial(string text)
        {
        }
    }
}
