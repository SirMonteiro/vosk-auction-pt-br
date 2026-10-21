using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using AuctionVoice.ASR.NativeVosk;

namespace AuctionVoice.ASR
{
    /// <summary>
    /// Vosk-backed speech recognition engine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One engine owns one <c>VoskModel</c>; models are read-only and safe to share. Every
    /// utterance gets a fresh <see cref="VoskStreamingSession"/>, because
    /// <c>KaldiRecognizer</c> is <b>not</b> thread-safe and must never be touched concurrently.
    /// </para>
    /// <para>
    /// <b>Threading:</b> <see cref="Initialize"/> is expensive (0.2 s) and blocking; call it from
    /// a background task at startup. Sessions are cheap to create.
    /// </para>
    /// </remarks>
    public sealed class VoskSpeechEngine : ISpeechRecognitionEngine
    {
        private readonly object _gate = new object();
        private IntPtr _model = IntPtr.Zero;
        private string _modelPath;
        private string _grammarJson;
        private int _maxAlternatives = 5;
        private bool _disposed;

        /// <inheritdoc />
        public bool IsInitialized => _model != IntPtr.Zero;

        /// <inheritdoc />
        public string ModelName { get; private set; } = string.Empty;

        /// <inheritdoc />
        public int SampleRate { get; }

        /// <summary>Number of alternative hypotheses to request from the decoder.</summary>
        public int MaxAlternatives
        {
            get => _maxAlternatives;
            set => _maxAlternatives = Math.Max(1, value);
        }

        /// <summary>True when a runtime phrase grammar is active.</summary>
        public bool GrammarEnabled => !string.IsNullOrEmpty(_grammarJson);

        /// <summary>Creates an engine that will recognise at <paramref name="sampleRate"/> Hz.</summary>
        public VoskSpeechEngine(int sampleRate = Audio.PcmAudio.SampleRate)
        {
            SampleRate = sampleRate;
        }

        /// <inheritdoc />
        public void Initialize(string modelPath)
        {
            if (string.IsNullOrEmpty(modelPath))
            {
                throw new ArgumentException("A model path is required.", nameof(modelPath));
            }

            if (!Directory.Exists(modelPath))
            {
                throw new DirectoryNotFoundException(
                    "Vosk model not found at '" + modelPath + "'. Copy the small-lm-v1 model "
                    + "directory into StreamingAssets/vosk/ and point the engine at it.");
            }

            lock (_gate)
            {
                if (_model != IntPtr.Zero && string.Equals(_modelPath, modelPath, StringComparison.Ordinal))
                {
                    return;
                }

                FreeModel();

                VoskPInvoke.vosk_set_log_level(-1);
                IntPtr handle = VoskPInvoke.vosk_model_new(modelPath);
                if (handle == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        "vosk_model_new returned null for '" + modelPath + "'. The directory must "
                        + "contain final.mdl, HCLr.fst and Gr.fst (or HCLG.fst).");
                }

                _model = handle;
                _modelPath = modelPath;
                ModelName = Path.GetFileName(
                    modelPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
        }

        /// <summary>
        /// Sets the runtime phrase grammar.
        /// </summary>
        /// <param name="grammarJson">
        /// A flat JSON array of accented phrase strings, or <c>null</c>/empty to use the model's
        /// own language model.
        /// </param>
        /// <remarks>
        /// <para>
        /// Only the <c>small</c> model family supports runtime grammars. The pruned model uses a
        /// static graph and silently ignores them, so verify support before enabling this.
        /// </para>
        /// <para>
        /// The grammar <b>replaces</b> the language model rather than biasing it: it must always
        /// contain the number phrases, or bids stop decoding (measured: 18/40 with an
        /// intent-only grammar).
        /// </para>
        /// </remarks>
        public void SetGrammar(string grammarJson)
        {
            lock (_gate)
            {
                _grammarJson = string.IsNullOrEmpty(grammarJson) ? null : grammarJson;
            }
        }

        /// <summary>
        /// True when the model directory can accept a runtime grammar (it has both graph files).
        /// </summary>
        public static bool SupportsRuntimeGrammar(string modelPath)
        {
            if (string.IsNullOrEmpty(modelPath))
            {
                return false;
            }

            return File.Exists(Path.Combine(modelPath, "HCLr.fst"))
                   && File.Exists(Path.Combine(modelPath, "Gr.fst"));
        }

        /// <summary>Returns true when the loaded model's vocabulary contains the word.</summary>
        public bool KnowsWord(string word)
        {
            lock (_gate)
            {
                return _model != IntPtr.Zero && VoskPInvoke.vosk_model_find_word(_model, word) >= 0;
            }
        }

        /// <inheritdoc />
        public IStreamingSession CreateSession()
        {
            lock (_gate)
            {
                if (_model == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        "The recognition engine is not initialised. Call Initialize(modelPath) first.");
                }

                IntPtr recognizer = string.IsNullOrEmpty(_grammarJson)
                    ? VoskPInvoke.vosk_recognizer_new(_model, SampleRate)
                    : VoskPInvoke.vosk_recognizer_new_grm(_model, SampleRate, _grammarJson);

                if (recognizer == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        "vosk_recognizer_new returned null. A grammar that is not a flat array of "
                        + "strings raises here, as does an unsupported sample rate.");
                }

                VoskPInvoke.vosk_recognizer_set_words(recognizer, 1);
                VoskPInvoke.vosk_recognizer_set_partial_words(recognizer, 1);
                VoskPInvoke.vosk_recognizer_set_max_alternatives(recognizer, _maxAlternatives);

                return new VoskStreamingSession(recognizer, ModelName, SampleRate);
            }
        }

        private void FreeModel()
        {
            if (_model != IntPtr.Zero)
            {
                VoskPInvoke.vosk_model_free(_model);
                _model = IntPtr.Zero;
            }

            _modelPath = null;
            ModelName = string.Empty;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            lock (_gate)
            {
                FreeModel();
            }

            _disposed = true;
        }
    }

    /// <summary>
    /// One recogniser bound to one utterance.
    /// </summary>
    /// <remarks>
    /// Accumulates text from every segment that ends mid-stream. Without this, an endpoint that
    /// fires while the player is still holding the key would discard everything recognised so
    /// far, and the trailing silence would produce an empty final result.
    /// </remarks>
    public sealed class VoskStreamingSession : IStreamingSession
    {
        private readonly IntPtr _recognizer;
        private readonly string _modelName;
        private readonly int _sampleRate;
        private readonly List<Segment> _segments = new List<Segment>(2);
        private readonly object _gate = new object();

        private long _startedTimestamp;
        private string _lastPartial = string.Empty;
        private bool _disposed;

        private struct Segment
        {
            public string Text;
            public List<Alternative> Alternatives;
        }

        /// <summary>Creates a session around an existing recogniser handle.</summary>
        public VoskStreamingSession(IntPtr recognizer, string modelName, int sampleRate)
        {
            _recognizer = recognizer;
            _modelName = modelName ?? string.Empty;
            _sampleRate = sampleRate;
        }

        /// <inheritdoc />
        public void Start()
        {
            lock (_gate)
            {
                VoskPInvoke.vosk_recognizer_reset(_recognizer);
                _startedTimestamp = Stopwatch.GetTimestamp();
                _lastPartial = string.Empty;
                _segments.Clear();
            }
        }

        /// <inheritdoc />
        public string Feed(byte[] pcm, int count)
        {
            if (pcm == null || count <= 0)
            {
                return null;
            }

            int length = Math.Min(count, pcm.Length);
            byte[] buffer = pcm;
            if (length != pcm.Length)
            {
                buffer = new byte[length];
                Array.Copy(pcm, buffer, length);
            }

            lock (_gate)
            {
                int finished = VoskPInvoke.vosk_recognizer_accept_waveform(_recognizer, buffer, length);
                if (finished < 0)
                {
                    throw new InvalidOperationException("vosk_recognizer_accept_waveform failed.");
                }

                if (finished == 1)
                {
                    // An endpoint fired mid-stream: keep the segment so later silence cannot
                    // discard already-recognised text.
                    JsonValue payload = JsonValue.Parse(
                        VoskPInvoke.ReadUtf8(VoskPInvoke.vosk_recognizer_result(_recognizer)));
                    List<Alternative> alternatives = ParseAlternatives(payload);
                    string text = payload["text"].AsStringOr(string.Empty).Trim();
                    if (text.Length == 0 && alternatives.Count > 0)
                    {
                        text = (alternatives[0].Text ?? string.Empty).Trim();
                    }

                    if (text.Length > 0)
                    {
                        _segments.Add(new Segment { Text = text, Alternatives = alternatives });
                    }

                    string prefix = JoinSegments();
                    if (prefix.Length > 0 && prefix != _lastPartial)
                    {
                        _lastPartial = prefix;
                        return prefix;
                    }

                    return null;
                }

                JsonValue partialPayload = JsonValue.Parse(
                    VoskPInvoke.ReadUtf8(VoskPInvoke.vosk_recognizer_partial_result(_recognizer)));
                string partial = partialPayload["partial"].AsStringOr(string.Empty).Trim();
                string segmentPrefix = JoinSegments();
                string current = segmentPrefix.Length > 0
                    ? (segmentPrefix + " " + partial).Trim()
                    : partial;

                if (current.Length > 0 && current != _lastPartial)
                {
                    _lastPartial = current;
                    return current;
                }

                return null;
            }
        }

        /// <inheritdoc />
        public AsrResult Finish()
        {
            lock (_gate)
            {
                JsonValue payload = JsonValue.Parse(
                    VoskPInvoke.ReadUtf8(VoskPInvoke.vosk_recognizer_final_result(_recognizer)));

                List<Alternative> finalAlternatives = ParseAlternatives(payload);
                string finalText = payload["text"].AsStringOr(string.Empty).Trim();
                if (finalText.Length == 0 && finalAlternatives.Count > 0)
                {
                    finalText = (finalAlternatives[0].Text ?? string.Empty).Trim();
                }

                if (finalText.Length > 0)
                {
                    _segments.Add(new Segment { Text = finalText, Alternatives = finalAlternatives });
                }

                var texts = new List<string>(_segments.Count);
                for (int i = 0; i < _segments.Count; i++)
                {
                    if (!string.IsNullOrEmpty(_segments[i].Text))
                    {
                        texts.Add(_segments[i].Text);
                    }
                }

                string combined = string.Join(" ", texts).Trim();

                List<Alternative> alternatives;
                if (_segments.Count == 0)
                {
                    alternatives = new List<Alternative>();
                }
                else if (_segments.Count == 1)
                {
                    alternatives = _segments[0].Alternatives ?? new List<Alternative>();
                }
                else
                {
                    // Multiple segments: the n-best lists no longer describe the combined text.
                    alternatives = new List<Alternative> { new Alternative { Text = combined } };
                }

                double elapsedMs =
                    (Stopwatch.GetTimestamp() - _startedTimestamp) * 1000.0 / Stopwatch.Frequency;

                return new AsrResult
                {
                    Text = combined,
                    Alternatives = alternatives,
                    DecodeMs = (float)elapsedMs,
                    Model = _modelName
                };
            }
        }

        /// <inheritdoc />
        public void Reset()
        {
            Start();
        }

        private string JoinSegments()
        {
            if (_segments.Count == 0)
            {
                return string.Empty;
            }

            var parts = new string[_segments.Count];
            for (int i = 0; i < _segments.Count; i++)
            {
                parts[i] = _segments[i].Text;
            }

            return string.Join(" ", parts).Trim();
        }

        /// <summary>
        /// Extracts n-best hypotheses from a Vosk result.
        /// </summary>
        /// <remarks>
        /// Vosk returns <c>{"text": ...}</c> normally, or <c>{"alternatives": [...]}</c> when
        /// <c>set_max_alternatives</c> is active. Confidence is reported but never used for
        /// ranking: the parser re-scores every alternative.
        /// </remarks>
        private static List<Alternative> ParseAlternatives(JsonValue payload)
        {
            var result = new List<Alternative>();
            JsonValue raw = payload["alternatives"];
            if (raw.Kind != JsonKind.Array || raw.Count == 0)
            {
                string text = payload["text"].AsStringOr(string.Empty).Trim();
                if (text.Length > 0)
                {
                    result.Add(new Alternative { Text = text });
                }

                return result;
            }

            for (int i = 0; i < raw.Count; i++)
            {
                JsonValue item = raw[i];
                string text = item["text"].AsStringOr(string.Empty).Trim();
                if (text.Length == 0)
                {
                    continue;
                }

                JsonValue words = item["result"];
                result.Add(new Alternative
                {
                    Text = text,
                    Confidence = (float)item["confidence"].AsNumberOr(0d),
                    Words = words.Kind == JsonKind.Array ? words.Items : null
                });
            }

            return result;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            lock (_gate)
            {
                if (_recognizer != IntPtr.Zero)
                {
                    VoskPInvoke.vosk_recognizer_free(_recognizer);
                }

                _segments.Clear();
            }

            _disposed = true;
        }
    }
}
