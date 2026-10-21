using System;
using System.Diagnostics;
using AuctionVoice.Core;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AuctionVoice.Diagnostics
{
    /// <summary>
    /// Unity-backed logger with zero allocation when debug is off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every public method is guarded by <see cref="IsDebugEnabled"/> before any string is
    /// built, and the informative overloads are additionally marked
    /// <see cref="ConditionalAttribute"/> so the call site itself is removed in release builds
    /// unless <c>ENABLE_AUCTION_DEBUG</c> is defined.
    /// </para>
    /// <para>
    /// Warnings are always emitted: they signal misconfiguration (missing model, unsupported
    /// grammar) that must not be silent.
    /// </para>
    /// </remarks>
    public sealed class AuctionLogger : IAuctionLogger
    {
        private bool _enableVerboseDebug;

        /// <summary>Creates a logger. Debug logging defaults to on in the editor, off in players.</summary>
        public AuctionLogger(bool enableVerboseDebug = false)
        {
            _enableVerboseDebug = enableVerboseDebug;
        }

        /// <inheritdoc />
        public bool IsDebugEnabled
        {
            get => _enableVerboseDebug;
            set => _enableVerboseDebug = value;
        }

        /// <summary>
        /// Informational message. Guarded at the call site by
        /// <see cref="LogInfoIfDebug"/>, because an interface implementation cannot carry
        /// <see cref="ConditionalAttribute"/>.
        /// </summary>
        public void LogInfo(string message)
        {
            if (!_enableVerboseDebug)
            {
                return;
            }

            Debug.Log("[AuctionVoice] " + message);
        }

        /// <summary>
        /// Emits an informational message only when <c>ENABLE_AUCTION_DEBUG</c> is defined, so
        /// the call site and its string concatenation are removed entirely in release builds.
        /// </summary>
        [Conditional("ENABLE_AUCTION_DEBUG")]
        public void LogInfoIfDebug(string message)
        {
            LogInfo(message);
        }

        /// <inheritdoc />
        public void LogWarning(string message)
        {
            Debug.LogWarning("[AuctionVoice] " + message);
        }

        /// <inheritdoc />
        public void LogPartial(string text)
        {
            if (!_enableVerboseDebug)
            {
                return;
            }

            Debug.Log("[AuctionVoice] partial \"" + text + "\"");
        }

        /// <inheritdoc />
        public void LogBidResult(BidResult result, long latencyMs)
        {
            if (!_enableVerboseDebug || result == null)
            {
                return;
            }

            Debug.Log(Format(result, latencyMs));
        }

        /// <summary>
        /// Renders the one-block report the Python demo prints, for side-by-side comparison.
        /// </summary>
        public static string Format(BidResult result, long latencyMs)
        {
            if (result == null)
            {
                return string.Empty;
            }

            var builder = new System.Text.StringBuilder(320);
            builder.AppendLine("----------------------------------------------------");
            builder.Append("[ASR]  \"").Append(result.RawTranscript).AppendLine("\"");
            builder.Append("[TXT]  \"").Append(result.NormalizedText).AppendLine("\"");
            builder.Append("[BID]  ").Append(result.Currency)
                .Append("  value=").Append(result.Value.ToString("F2"))
                .Append("  mode=").Append(result.Mode.ToWire())
                .Append("  kind=").Append(result.Kind.ToWire())
                .AppendLine();
            builder.Append("[FLG]  add=").Append(result.HasAddition ? "true" : "false")
                .Append("  sum=").Append(result.HasSum ? "true" : "false")
                .Append("  mult=").Append(result.HasMultiplication ? "true" : "false")
                .Append("  action=").Append(result.Action.ToWire())
                .AppendLine();
            builder.Append("[PRF]  conf=").Append(result.Confidence.ToString("F2"))
                .Append("  ").Append(latencyMs).Append("ms")
                .AppendLine();

            if (result.RejectReason != BidRejectReason.None)
            {
                builder.Append("[WHY]  ").AppendLine(result.Reason);
            }

            builder.Append("----------------------------------------------------");
            return builder.ToString();
        }
    }
}
