using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AuctionVoice.ASR;
using AuctionVoice.Audio;
using AuctionVoice.Core;
using AuctionVoice.Diagnostics;
using AuctionVoice.NLU;

namespace AuctionVoice.Service
{
    /// <summary>
    /// Orchestrates one push-to-talk utterance: capture, decode, parse, report.
    /// </summary>
    public interface IBidProcessorService : IDisposable
    {
        /// <summary>True while an utterance is open.</summary>
        bool IsListening { get; }

        /// <summary>Raised on the main thread whenever the partial transcript changes.</summary>
        event Action<string> OnPartialTranscript;

        /// <summary>Raised on the main thread once a bid is finalised.</summary>
        event Action<BidResult> OnBidFinalized;

        /// <summary>Opens the microphone and starts a session.</summary>
        void BeginSpeechSession();

        /// <summary>
        /// Closes the session: flushes trailing silence, decodes, parses and raises
        /// <see cref="OnBidFinalized"/>.
        /// </summary>
        Task<BidResult> EndSpeechSessionAsync(
            int trailingSilenceMs = 200,
            CancellationToken cancellationToken = default);

        /// <summary>Drops the current utterance without producing a result.</summary>
        void CancelSpeechSession();

        /// <summary>Feeds PCM directly, bypassing the microphone (tests, replay, sidecar).</summary>
        void PushAudio(byte[] pcm, int count);
    }

    /// <summary>
    /// Default <see cref="IBidProcessorService"/> implementation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Decoding and parsing run on a worker thread; only the events are marshalled back. The
    /// caller is responsible for invoking the raised callbacks on the main thread when it
    /// supplies its own dispatcher, which is what <see cref="PushToTalkController"/> does.
    /// </para>
    /// <para>
    /// Audio gates mirror the reference implementation: utterances shorter than
    /// <see cref="MinUtteranceMs"/> are rejected as <c>too_short</c>, and audio below
    /// <see cref="MinRms"/> is rejected as <c>no_speech</c>. Both are evaluated on the
    /// <b>unpadded</b> buffer, because padding would clear the duration gate first.
    /// </para>
    /// </remarks>
    public sealed class BidProcessorService : IBidProcessorService
    {
        private readonly ISpeechRecognitionEngine _engine;
        private readonly IAudioCaptureService _capture;
        private readonly INluParser _parser;
        private readonly IAuctionLogger _logger;

        private readonly object _gate = new object();
        private readonly List<byte> _utterance = new List<byte>(16000 * 4);
        private IStreamingSession _session;
        private bool _disposed;

        /// <summary>Minimum utterance duration; shorter audio is rejected as <c>too_short</c>.</summary>
        public int MinUtteranceMs = 250;

        /// <summary>RMS gate; quieter audio is rejected as <c>no_speech</c>.</summary>
        public float MinRms = 0.004f;

        /// <summary>Silence prepended before decoding, to protect the first syllable.</summary>
        public int LeadSilenceMs = 300;

        /// <summary>Silence appended before finalising, to flush the acoustic graph.</summary>
        public int TailSilenceMs = 400;

        /// <summary>Recent idle audio replayed when a session starts.</summary>
        public int PreRollMs = 250;

        /// <summary>When true, parsing populates the trace dictionary.</summary>
        public bool DebugParsing;

        /// <summary>True when a session is open.</summary>
        public bool IsListening { get; private set; }

        /// <summary>
        /// The capture service this processor drains, or <c>null</c> when audio is pushed
        /// externally (replay, tests, a platform-specific capture plugin).
        /// </summary>
        public IAudioCaptureService Capture => _capture;

        /// <inheritdoc />
        public event Action<string> OnPartialTranscript;

        /// <inheritdoc />
        public event Action<BidResult> OnBidFinalized;

        /// <summary>Creates a service.</summary>
        public BidProcessorService(
            ISpeechRecognitionEngine engine,
            IAudioCaptureService capture,
            INluParser parser = null,
            IAuctionLogger logger = null)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _capture = capture;
            _parser = parser ?? AuctionBidParser.Default;
            _logger = logger ?? NullAuctionLogger.Instance;

            if (_capture != null)
            {
                _capture.OnChunkReady += HandleChunkReady;
            }
        }

        /// <inheritdoc />
        public void BeginSpeechSession()
        {
            ThrowIfDisposed();

            lock (_gate)
            {
                if (IsListening)
                {
                    return;
                }

                _utterance.Clear();
                _session = _engine.CreateSession();
                _session.Start();
                IsListening = true;
            }

            // Replay the tail of the idle buffer so the first syllable is not clipped.
            if (_capture != null)
            {
                byte[] preRoll = _capture.GetPreRoll(PreRollMs);
                if (preRoll.Length > 0)
                {
                    PushAudio(preRoll, preRoll.Length);
                }
            }

            if (_capture != null)
            {
                _capture.StartCapture();
            }

            _logger.LogInfo("speech session started");
        }

        private void HandleChunkReady(byte[] pcm)
        {
            PushAudio(pcm, pcm?.Length ?? 0);
        }

        /// <inheritdoc />
        public void PushAudio(byte[] pcm, int count)
        {
            if (pcm == null || count <= 0 || !IsListening)
            {
                return;
            }

            int length = Math.Min(count, pcm.Length);
            string partial = null;

            lock (_gate)
            {
                if (_session == null)
                {
                    return;
                }

                _utterance.AddRange(new ArraySegment<byte>(pcm, 0, length));
                partial = _session.Feed(pcm, length);
            }

            if (!string.IsNullOrEmpty(partial))
            {
                _logger.LogPartial(partial);
                OnPartialTranscript?.Invoke(partial);
            }
        }

        /// <inheritdoc />
        public Task<BidResult> EndSpeechSessionAsync(
            int trailingSilenceMs = 200,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            IStreamingSession session;
            byte[] utterance;

            lock (_gate)
            {
                if (!IsListening)
                {
                    return Task.FromResult(
                        Rejected(string.Empty, BidRejectReason.Silence, 0f));
                }

                IsListening = false;
                session = _session;
                _session = null;
                utterance = _utterance.ToArray();
                _utterance.Clear();
            }

            if (_capture != null)
            {
                _capture.StopCapture();
            }

            return Task.Run(
                () => Finalize(session, utterance, trailingSilenceMs), cancellationToken);
        }

        private BidResult Finalize(IStreamingSession session, byte[] utterance, int trailingSilenceMs)
        {
            long started = Stopwatch.GetTimestamp();
            if (session == null)
            {
                return Rejected(string.Empty, BidRejectReason.Silence, 0f);
            }

            try
            {
                // Gates run on the *unpadded* buffer: padding would clear the duration gate first
                // and report too_short instead of silence.
                float durationMs = PcmAudio.DurationMs(utterance.Length, _engine.SampleRate);
                if (durationMs < MinUtteranceMs)
                {
                    return Rejected(string.Empty, BidRejectReason.TooShort, durationMs);
                }

                if (PcmAudio.IsSilent(utterance, MinRms, _engine.SampleRate))
                {
                    return Rejected(string.Empty, BidRejectReason.NoSpeech, durationMs);
                }

                int tail = trailingSilenceMs > 0 ? trailingSilenceMs : TailSilenceMs;
                if (tail > 0)
                {
                    byte[] silence = PcmAudio.Silence(tail, _engine.SampleRate);
                    session.Feed(silence, silence.Length);
                }

                AsrResult asr = session.Finish();
                List<string> candidates = asr.NBestTexts();
                BidResult result = candidates.Count > 0
                    ? _parser.ParseNBest(candidates, DebugParsing)
                    : Rejected(string.Empty, BidRejectReason.Silence, durationMs);

                if (candidates.Count == 0 && !string.IsNullOrEmpty(asr.Text))
                {
                    result = _parser.Parse(asr.Text, DebugParsing);
                }

                result.LatencyMs = (float)((Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency);
                result.AddTrace(DebugParsing, "model", _engine.ModelName);
                result.AddTrace(DebugParsing, "decode_ms",
                    asr.DecodeMs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));

                long totalMs = (long)result.LatencyMs;
                _logger.LogBidResult(result, totalMs);
                OnBidFinalized?.Invoke(result);
                return result;
            }
            finally
            {
                session.Dispose();
            }
        }

        /// <inheritdoc />
        public void CancelSpeechSession()
        {
            lock (_gate)
            {
                IsListening = false;
                if (_session != null)
                {
                    _session.Dispose();
                    _session = null;
                }

                _utterance.Clear();
            }

            if (_capture != null)
            {
                _capture.StopCapture();
            }
        }

        private static BidResult Rejected(string raw, BidRejectReason reason, float durationMs)
        {
            return new BidResult
            {
                RawTranscript = raw,
                Kind = BidKind.Unknown,
                Mode = ValueMode.Absolute,
                RejectReason = reason,
                LatencyMs = 0f
            };
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(BidProcessorService));
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (_capture != null)
            {
                _capture.OnChunkReady -= HandleChunkReady;
                _capture.Dispose();
            }

            CancelSpeechSession();
            _engine?.Dispose();
            _disposed = true;
        }
    }
}
