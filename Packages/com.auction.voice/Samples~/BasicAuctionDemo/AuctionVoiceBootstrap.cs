using System;
using System.IO;
using System.Threading.Tasks;
using AuctionVoice.ASR;
using AuctionVoice.Audio;
using AuctionVoice.Core;
using AuctionVoice.Diagnostics;
using AuctionVoice.NLU;
using AuctionVoice.Service;
using UnityEngine;

namespace AuctionVoice.Samples
{
    /// <summary>
    /// Drop-in bootstrap: loads the Vosk model and wires capture, recognition, NLU and
    /// push-to-talk together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Attach this to an empty GameObject, set the model folder under <c>StreamingAssets</c>,
    /// press play and hold the talk key. The model loads asynchronously so the editor does not
    /// stall; push-to-talk is inert until <see cref="IsReady"/> is true.
    /// </para>
    /// <para>
    /// Ship the model directory inside <c>StreamingAssets/vosk/</c>. On desktop platforms that
    /// folder is a real directory on disk, which is what <c>vosk_model_new</c> requires. On
    /// Android the contents must be extracted from the APK to persistent storage first.
    /// </para>
    /// </remarks>
    [AddComponentMenu("Auction Voice/Auction Voice Bootstrap")]
    public sealed class AuctionVoiceBootstrap : MonoBehaviour
    {
        [Header("Model")]
        [Tooltip("Model folder name under StreamingAssets/vosk, e.g. small-lm-v1.")]
        [SerializeField] private string _modelFolderName = "small-lm-v1";

        [Tooltip("Optional absolute path. Overrides the StreamingAssets lookup.")]
        [SerializeField] private string _modelAbsolutePath;

        [Tooltip("Enable the runtime phrase grammar. Only the 'small' model family supports it.")]
        [SerializeField] private bool _useGrammar = true;

        [Header("Behaviour")]
        [Tooltip("Number of decoder hypotheses to request. The parser scores them itself.")]
        [SerializeField, Range(1, 10)] private int _maxAlternatives = 5;

        [Tooltip("Verbose logging in the editor and development builds.")]
        [SerializeField] private bool _enableVerboseDebug = true;

        [Header("Optional")]
        [Tooltip("Push-to-talk controller to bind. Found in children when empty.")]
        [SerializeField] private PushToTalkController _controller;

        private VoskSpeechEngine _engine;
        private UnityMicrophoneCapture _capture;
        private BidProcessorService _service;
        private AuctionLogger _logger;

        /// <summary>True once the model is loaded and push-to-talk may be used.</summary>
        public bool IsReady { get; private set; }

        /// <summary>Why the last initialisation attempt failed, or null.</summary>
        public string LastError { get; private set; }

        /// <summary>The parser, usable without any audio for text-level tests.</summary>
        public INluParser Parser { get; private set; }

        /// <summary>The running service, or null until ready.</summary>
        public BidProcessorService Service => _service;

        private void Awake()
        {
            _logger = new AuctionLogger(_enableVerboseDebug);
            Parser = new AuctionBidParser(_enableVerboseDebug);

            if (_controller == null)
            {
                _controller = GetComponentInChildren<PushToTalkController>();
            }
        }

        private async void Start()
        {
            await InitializeAsync();
        }

        /// <summary>Loads the model on a worker thread and wires the service.</summary>
        public async Task InitializeAsync()
        {
            string modelPath = ResolveModelPath();
            if (string.IsNullOrEmpty(modelPath))
            {
                Fail("Model folder not found. Copy the Vosk model into StreamingAssets/vosk/.");
                return;
            }

            _engine = new VoskSpeechEngine(PcmAudio.SampleRate) { MaxAlternatives = _maxAlternatives };

            try
            {
                // vosk_model_new blocks for ~0.2 s; keep it off the main thread.
                await Task.Run(() => _engine.Initialize(modelPath));
            }
            catch (Exception exception)
            {
                Fail(exception.Message);
                _engine = null;
                return;
            }

            if (_useGrammar)
            {
                if (VoskSpeechEngine.SupportsRuntimeGrammar(modelPath))
                {
                    _engine.SetGrammar(BuildGrammarJson());
                }
                else
                {
                    _logger.LogWarning(
                        "This model uses a static graph and ignores runtime grammars; continuing without one.");
                }
            }

            _capture = new UnityMicrophoneCapture();
            _service = new BidProcessorService(_engine, _capture, Parser, _logger)
            {
                DebugParsing = _enableVerboseDebug
            };

            if (_controller != null)
            {
                _controller.SetService(_service);
            }

            IsReady = true;
            LastError = null;
            _logger.LogInfo("ready: model=" + _engine.ModelName + " grammar=" + _engine.GrammarEnabled);
        }

        private string ResolveModelPath()
        {
            if (!string.IsNullOrEmpty(_modelAbsolutePath) && Directory.Exists(_modelAbsolutePath))
            {
                return _modelAbsolutePath;
            }

            string streamingAssets = Path.Combine(Application.streamingAssetsPath, "vosk", _modelFolderName);
            return Directory.Exists(streamingAssets) ? streamingAssets : null;
        }

        /// <summary>
        /// Builds the runtime phrase grammar from the lexicon.
        /// </summary>
        /// <remarks>
        /// Numbers are mandatory: the grammar <b>replaces</b> the language model, so an
        /// intent-only grammar drops bids entirely (measured: 18/40). Phrases, not words, and
        /// accented, because Vosk silently discards unaccented entries.
        /// </remarks>
        private static string BuildGrammarJson()
        {
            var phrases = new System.Collections.Generic.List<string>(512);

            // Numbers 0-99, the most common auction bids.
            string[] units =
            {
                "um", "dois", "três", "quatro", "cinco", "seis", "sete", "oito", "nove"
            };
            string[] teens =
            {
                "dez", "onze", "doze", "treze", "quatorze", "quinze", "dezesseis", "dezessete",
                "dezoito", "dezenove"
            };
            string[] tens =
            {
                "vinte", "trinta", "quarenta", "cinquenta", "sessenta", "setenta", "oitenta", "noventa"
            };
            string[] hundreds =
            {
                "cem", "cento", "duzentos", "trezentos", "quatrocentos", "quinhentos", "seiscentos",
                "setecentos", "oitocentos", "novecentos"
            };

            phrases.AddRange(units);
            phrases.AddRange(teens);
            phrases.AddRange(tens);
            phrases.AddRange(hundreds);
            foreach (string ten in tens)
            {
                foreach (string unit in units)
                {
                    phrases.Add(ten + " e " + unit);
                }
            }

            foreach (string hundred in hundreds)
            {
                phrases.Add(hundred + " mil");
            }

            foreach (string number in new[] { "cinco", "dez", "vinte", "trinta", "cinquenta", "cem", "duzentos" })
            {
                phrases.Add(number + " mil");
            }

            // Currency frames and delta markers.
            foreach (string prefix in new[] { "mais", "cobre", "vou de", "subo", "aumento" })
            {
                foreach (string number in new[] { "vinte", "cinquenta", "cem", "duzentos", "mil" })
                {
                    phrases.Add(prefix + " " + number);
                }
            }

            // Intents, multipliers, slang and idioms.
            foreach (string phrase in Lexicon.PassPhrases)
            {
                phrases.Add(phrase);
            }

            foreach (string phrase in Lexicon.CorrectPhrases)
            {
                phrases.Add(phrase);
            }

            foreach (string phrase in Lexicon.QueryPhrases)
            {
                phrases.Add(phrase);
            }

            foreach (string phrase in Lexicon.ChantPhrases)
            {
                phrases.Add(phrase.Replace("-", " "));
            }

            foreach (string phrase in Lexicon.MultiplierWords.Keys)
            {
                phrases.Add(phrase);
            }

            foreach (string phrase in Lexicon.Magnitudes.Keys)
            {
                phrases.Add(phrase);
                phrases.Add("um " + phrase);
                phrases.Add("dois " + phrase);
                phrases.Add("três " + phrase);
            }

            foreach (System.Collections.Generic.KeyValuePair<string, int> idiom in Lexicon.IdiomsLongestFirst)
            {
                phrases.Add(idiom.Key);
            }

            phrases.Add("[unk]");

            var builder = new System.Text.StringBuilder(phrases.Count * 16);
            builder.Append('[');
            for (int i = 0; i < phrases.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append('"').Append(EscapeJson(phrases[i])).Append('"');
            }

            builder.Append(']');
            return builder.ToString();
        }

        private static string EscapeJson(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private void Fail(string message)
        {
            LastError = message;
            IsReady = false;
            Debug.LogError("[AuctionVoice] " + message);
        }

        private void OnDestroy()
        {
            if (_service != null)
            {
                _service.Dispose();
                _service = null;
            }
            else
            {
                _engine?.Dispose();
                _capture?.Dispose();
            }
        }
    }
}
