using System;
using System.Runtime.InteropServices;

namespace AuctionVoice.ASR.NativeVosk
{
    /// <summary>
    /// Raw P/Invoke surface of the Vosk C API.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Signatures are taken from <c>vosk_api.h</c> and verified against the exported symbols of
    /// <c>libvosk.so</c>. All strings returned by Vosk are owned by the recogniser and must be
    /// copied before the next call; this layer converts them to managed strings immediately.
    /// </para>
    /// <para>
    /// <b>Never call these directly from game code.</b> Use
    /// <see cref="VoskSpeechEngine"/>, which owns the handles, pins the model lifetime and moves
    /// decoding off the main thread.
    /// </para>
    /// <para>
    /// Handles are opaque pointers. A null return from a constructor means failure and must be
    /// surfaced, never dereferenced.
    /// </para>
    /// </remarks>
    public static class VoskPInvoke
    {
        /// <summary>Library name resolved by the platform's loader.</summary>
        /// <remarks>
        /// Unity copies everything under <c>Plugins/x86_64</c> next to the player, so the bare
        /// names resolve on Windows (<c>vosk.dll</c>), Linux (<c>libvosk.so</c>) and macOS
        /// (<c>libvosk.dylib</c>).
        /// </remarks>
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        public const string LibraryName = "vosk";
#elif UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
        public const string LibraryName = "libvosk";
#else
        public const string LibraryName = "libvosk";
#endif

        /// <summary>Creates a model from a directory containing the acoustic model and graph.</summary>
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern IntPtr vosk_model_new(string modelPath);

        /// <summary>Releases a model. Safe to call with <see cref="IntPtr.Zero"/>.</summary>
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vosk_model_free(IntPtr model);

        /// <summary>
        /// Returns the model's word index for a UTF-8 word, or -1 when the word is out of
        /// vocabulary.
        /// </summary>
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int vosk_model_find_word(IntPtr model, string word);

        /// <summary>Creates a recogniser with the model's own language model.</summary>
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr vosk_recognizer_new(IntPtr model, float sampleRate);

        /// <summary>
        /// Creates a recogniser restricted to a phrase grammar (a flat JSON array of strings).
        /// </summary>
        /// <remarks>
        /// The grammar <b>replaces</b> the language model rather than biasing it, which is why it
        /// must always contain the number phrases. Nested arrays raise at creation time.
        /// </remarks>
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern IntPtr vosk_recognizer_new_grm(IntPtr model, float sampleRate, string grammarJson);

        /// <summary>Releases a recogniser. Safe to call with <see cref="IntPtr.Zero"/>.</summary>
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vosk_recognizer_free(IntPtr recognizer);

        /// <summary>
        /// Feeds PCM and returns 1 when an endpoint was detected, 0 when more audio is needed, and
        /// a negative value on failure.
        /// </summary>
        /// <remarks>
        /// <paramref name="data"/> must stay pinned for the duration of the call; the marshaller
        /// handles that for a byte array parameter.
        /// </remarks>
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vosk_recognizer_accept_waveform(
            IntPtr recognizer, byte[] data, int length);

        /// <summary>Feeds 32-bit float PCM in [-1, 1].</summary>
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vosk_recognizer_accept_waveform_f(
            IntPtr recognizer, float[] data, int length);

        /// <summary>Returns the JSON result of the segment that just ended.</summary>
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr vosk_recognizer_result(IntPtr recognizer);

        /// <summary>Returns the JSON partial result. Display-only; never apply game state from it.</summary>
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr vosk_recognizer_partial_result(IntPtr recognizer);

        /// <summary>Flushes the decoder and returns the final JSON result.</summary>
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr vosk_recognizer_final_result(IntPtr recognizer);

        /// <summary>Resets the recogniser for a new utterance, keeping the loaded model.</summary>
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vosk_recognizer_reset(IntPtr recognizer);

        /// <summary>Enables per-word timings in results.</summary>
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vosk_recognizer_set_words(IntPtr recognizer, int enable);

        /// <summary>Enables per-word timings in partial results.</summary>
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vosk_recognizer_set_partial_words(IntPtr recognizer, int enable);

        /// <summary>Sets how many alternative hypotheses a result carries.</summary>
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vosk_recognizer_set_max_alternatives(IntPtr recognizer, int maxAlternatives);

        /// <summary>Toggles NLSML output (unused here).</summary>
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vosk_recognizer_set_nlsml(IntPtr recognizer, int enable);

        /// <summary>Replaces the recogniser's grammar at runtime.</summary>
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern void vosk_recognizer_set_grm(IntPtr recognizer, string grammarJson);

        /// <summary>Sets the native log verbosity; -1 silences it.</summary>
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vosk_set_log_level(int level);

        /// <summary>Copies a UTF-8 string returned by Vosk into a managed string.</summary>
        public static string ReadUtf8(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero)
            {
                return string.Empty;
            }

            return Marshal.PtrToStringUTF8(pointer) ?? string.Empty;
        }
    }
}
