using System.Text;
using AuctionVoice.Core;
using UnityEngine;
using UnityEngine.UI;

namespace AuctionVoice.Samples
{
    /// <summary>
    /// Minimal on-screen readout for the sample scene: partial transcript, final bid, and a
    /// running log of recognised utterances.
    /// </summary>
    /// <remarks>
    /// Deliberately plain: the demo exists to prove the pipeline end to end, not to be a game
    /// UI. Text is pt-BR because the players are; code and comments stay English (<c>Q-6</c>).
    /// </remarks>
    [AddComponentMenu("Auction Voice/Auction Voice Debug HUD")]
    public sealed class AuctionVoiceDebugHud : MonoBehaviour
    {
        [Tooltip("Shows the live partial transcript.")]
        [SerializeField] private Text _partialLabel;

        [Tooltip("Shows the last finalised bid.")]
        [SerializeField] private Text _resultLabel;

        [Tooltip("Shows one line per finalised utterance.")]
        [SerializeField] private Text _logLabel;

        [Tooltip("Maximum number of log lines kept on screen.")]
        [SerializeField] private int _maxLogLines = 12;

        private readonly StringBuilder _log = new StringBuilder(1024);
        private int _logLines;

        private void OnEnable()
        {
            if (_partialLabel != null)
            {
                _partialLabel.text = "segure para falar";
            }
        }

        /// <summary>Called by <see cref="PushToTalkController.OnPartialReceived"/>.</summary>
        public void ShowPartial(string text)
        {
            if (_partialLabel != null)
            {
                _partialLabel.text = string.IsNullOrEmpty(text) ? "..." : text;
            }
        }

        /// <summary>Called by <see cref="PushToTalkController.OnBidRecognized"/>.</summary>
        public void ShowResult(BidResult result)
        {
            if (result == null)
            {
                return;
            }

            if (_partialLabel != null)
            {
                _partialLabel.text = "segure para falar";
            }

            if (_resultLabel != null)
            {
                _resultLabel.text = Describe(result);
            }

            AppendLog(Describe(result));
        }

        private static string Describe(BidResult result)
        {
            var builder = new StringBuilder(96);

            switch (result.Kind)
            {
                case BidKind.BidAbsolute:
                    builder.Append("LANCE ").Append(result.Currency);
                    break;
                case BidKind.BidDelta:
                    builder.Append("MAIS ").Append(result.Currency).Append(" (incremental)");
                    break;
                case BidKind.BidSum:
                    builder.Append("SOMA ").Append(result.Currency);
                    break;
                case BidKind.BidMultiply:
                    builder.Append("MULTIPLICADO ").Append(result.Currency);
                    break;
                case BidKind.MultiplyOnly:
                    builder.Append("MULTIPLICAR x").Append(result.Value.ToString("0.##"));
                    break;
                case BidKind.Pass:
                    builder.Append("PASSOU");
                    break;
                case BidKind.Correct:
                    builder.Append("CORREÇÃO");
                    if (result.TargetValue.HasValue)
                    {
                        builder.Append(" para ").Append(AuctionFormatting.FormatCurrency(result.TargetValue.Value));
                    }

                    break;
                case BidKind.Chant:
                    builder.Append("MARTELO");
                    break;
                case BidKind.Query:
                    builder.Append("PERGUNTA");
                    break;
                case BidKind.Lot:
                    builder.Append("LOTE ").Append(result.Lot ?? 0);
                    break;
                default:
                    builder.Append("—");
                    if (result.RejectReason != BidRejectReason.None)
                    {
                        builder.Append(" (").Append(result.Reason).Append(")");
                    }

                    break;
            }

            if (result.Closing)
            {
                builder.Append("  [FECHANDO]");
            }

            builder.Append("   \"").Append(result.RawTranscript).Append('"');
            return builder.ToString();
        }

        private void AppendLog(string line)
        {
            _log.AppendLine(line);
            _logLines++;

            while (_logLines > _maxLogLines && _log.Length > 0)
            {
                int newline = _log.ToString().IndexOf('\n');
                if (newline < 0)
                {
                    break;
                }

                _log.Remove(0, newline + 1);
                _logLines--;
            }

            if (_logLabel != null)
            {
                _logLabel.text = _log.ToString();
            }
        }
    }
}
