using System;
using System.Collections.Generic;

namespace AuctionVoice.ASR
{
    /// <summary>One decoder hypothesis.</summary>
    public sealed class Alternative
    {
        /// <summary>Recognised text.</summary>
        public string Text = string.Empty;

        /// <summary>Vosk's own confidence. Unreliable for ranking; the parser re-scores.</summary>
        public float Confidence;

        /// <summary>Word-level timings, when the recogniser was configured for them.</summary>
        public IReadOnlyList<JsonValue> Words;
    }

    /// <summary>Everything the decoder produced for one utterance.</summary>
    public sealed class AsrResult
    {
        /// <summary>Combined transcript of every segment.</summary>
        public string Text = string.Empty;

        /// <summary>Alternative hypotheses, best first as Vosk ordered them.</summary>
        public List<Alternative> Alternatives = new List<Alternative>();

        /// <summary>Audio duration in seconds, when known.</summary>
        public float DurationSeconds;

        /// <summary>Time from session start to the final result, in milliseconds.</summary>
        public float DecodeMs;

        /// <summary>Name of the model directory that produced this result.</summary>
        public string Model = string.Empty;

        /// <summary>
        /// Distinct hypothesis texts, best first, always including <see cref="Text"/>.
        /// </summary>
        /// <remarks>
        /// This is what the parser scores. Vosk's own confidence ordering is not trustworthy
        /// (it ranked <c>"mas vindo de"</c> above <c>"mais vinte"</c>), so every candidate is
        /// re-scored by the NLU.
        /// </remarks>
        public List<string> NBestTexts()
        {
            var seen = new List<string>(Alternatives.Count + 1);
            AddDistinct(seen, Text);
            for (int i = 0; i < Alternatives.Count; i++)
            {
                AddDistinct(seen, Alternatives[i].Text);
            }

            return seen;
        }

        private static void AddDistinct(List<string> target, string candidate)
        {
            if (string.IsNullOrEmpty(candidate))
            {
                return;
            }

            string normalized = CollapseWhitespace(candidate);
            if (normalized.Length == 0 || target.Contains(normalized))
            {
                return;
            }

            target.Add(normalized);
        }

        private static string CollapseWhitespace(string text)
        {
            var builder = new System.Text.StringBuilder(text.Length);
            bool pending = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c))
                {
                    pending = builder.Length > 0;
                    continue;
                }

                if (pending)
                {
                    builder.Append(' ');
                    pending = false;
                }

                builder.Append(c);
            }

            return builder.ToString();
        }
    }

    /// <summary>A loaded model that can create streaming sessions.</summary>
    public interface ISpeechRecognitionEngine : IDisposable
    {
        /// <summary>True once a model is loaded and a recogniser can be created.</summary>
        bool IsInitialized { get; }

        /// <summary>Name of the loaded model directory.</summary>
        string ModelName { get; }

        /// <summary>Sample rate the recogniser expects.</summary>
        int SampleRate { get; }

        /// <summary>Loads a model from a directory. Idempotent for the same path.</summary>
        void Initialize(string modelPath);

        /// <summary>Creates a streaming session. Sessions are not thread-safe; use one at a time.</summary>
        IStreamingSession CreateSession();
    }

    /// <summary>A per-utterance streaming recogniser.</summary>
    public interface IStreamingSession : IDisposable
    {
        /// <summary>Resets the recogniser for a new utterance.</summary>
        void Start();

        /// <summary>
        /// Consumes PCM and returns the current partial transcript when it changed, otherwise
        /// <c>null</c>.
        /// </summary>
        string Feed(byte[] pcm, int count);

        /// <summary>Flushes the decoder and returns the final result.</summary>
        AsrResult Finish();

        /// <summary>Drops buffered audio and prepares for the next utterance.</summary>
        void Reset();
    }
}
