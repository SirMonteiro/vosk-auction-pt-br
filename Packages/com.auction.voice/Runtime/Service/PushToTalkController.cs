using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using AuctionVoice.Audio;
using AuctionVoice.Core;
using AuctionVoice.Diagnostics;
using AuctionVoice.NLU;
using UnityEngine;
using UnityEngine.Events;
#if AUCTION_VOICE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace AuctionVoice.Service
{
    /// <summary>
    /// Push-to-talk driver with a configurable post-release grace period.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Human speech continues for 100-200 ms after the player releases the key (terminal
    /// consonants, reverberation, hesitation). Finalising immediately clips the last syllable,
    /// turning <c>"cinquenta"</c> into <c>"cinquen"</c>. This controller therefore waits
    /// <see cref="ReleaseGracePeriodMs"/> after release before finalising, and cancels that wait
    /// if the player presses again, so a hesitation mid-phrase is seamless.
    /// </para>
    /// <para>
    /// The whole lifecycle is: <c>started</c> opens a session, chunks stream into Vosk,
    /// <c>canceled</c> arms the grace timer, and the timer flushes trailing silence and asks the
    /// service to finalise. Only the final result changes game state; partials are display-only.
    /// </para>
    /// </remarks>
    [AddComponentMenu("Auction Voice/Push To Talk Controller")]
    public class PushToTalkController : MonoBehaviour
    {
        /// <summary>How the talk button is read.</summary>
        public enum InputMode
        {
            /// <summary>Read <see cref="TalkAction"/> from the Input System.</summary>
            InputSystemAction = 0,

            /// <summary>Read a legacy <see cref="KeyCode"/> in <c>Update()</c>. Useful before Input System wiring.</summary>
            LegacyKeyCode = 1,

            /// <summary>Never start a session automatically; the game calls the public methods.</summary>
            External = 2
        }

        [Header("Input")]
        [Tooltip("Input System action that opens the microphone while held.")]
#if AUCTION_VOICE_INPUT_SYSTEM
        [SerializeField] private InputActionReference _talkAction;
#endif

        [Tooltip("How the talk button is read.")]
        [SerializeField] private InputMode _inputMode = InputMode.InputSystemAction;

        [Tooltip("Legacy key used when InputMode is LegacyKeyCode.")]
        [SerializeField] private KeyCode _fallbackKey = KeyCode.Space;

        [Header("Timing")]
        [Tooltip("Grace period after release before finalising. Prevents clipped final syllables.")]
        [SerializeField, Range(0, 1000)] private int _releaseGracePeriodMs = 250;

        [Tooltip("Silence fed to the recogniser before finalising, to flush the acoustic graph.")]
        [SerializeField, Range(0, 500)] private int _trailingSilencePaddingMs = 200;

        [Tooltip("Reject utterances shorter than this. Guards against accidental taps.")]
        [SerializeField, Range(0, 1000)] private int _minimumUtteranceMs = 250;

        [Header("Service")]
        [Tooltip("Service that owns capture, decoding and parsing. Created automatically when empty.")]
        [SerializeField] private BidProcessorService _bidService;

        [Tooltip("Create the microphone capture, engine and service in Awake when no service is assigned.")]
        [SerializeField] private bool _autoCreateService = true;

        [Header("Diagnostics")]
        [Tooltip("Verbose logging. Ignored in release builds unless ENABLE_AUCTION_DEBUG is defined.")]
        [SerializeField] private bool _enableVerboseDebug;

        [Header("Events")]
        [Tooltip("Raised on the main thread whenever the partial transcript changes.")]
        public UnityEvent<string> OnPartialReceived = new UnityEvent<string>();

        [Tooltip("Raised on the main thread once a bid is finalised.")]
        public BidResultEvent OnBidRecognized = new BidResultEvent();

        [Tooltip("Raised when a session opens (key down).")]
        public UnityEvent OnListeningStarted = new UnityEvent();

        [Tooltip("Raised when a session closes and finalisation begins (after the grace period).")]
        public UnityEvent OnListeningStopped = new UnityEvent();

        private Coroutine _graceRoutine;
        private bool _isHolding;
        private bool _ownsService;
        private bool _finalizing;
        private AuctionLogger _logger;
        private readonly Queue<Action> _mainThreadQueue = new Queue<Action>(8);

        /// <summary>Serializable event carrying a <see cref="BidResult"/>.</summary>
        [Serializable]
        public sealed class BidResultEvent : UnityEvent<BidResult>
        {
        }

        /// <summary>True while the talk button is held.</summary>
        public bool IsHolding => _isHolding;

        /// <summary>True while a session is open, including the grace period.</summary>
        public bool IsListening => _bidService != null && _bidService.IsListening;

        /// <summary>The service this controller drives.</summary>
        public BidProcessorService Service => _bidService;

        /// <summary>Grace period in milliseconds.</summary>
        public int ReleaseGracePeriodMs
        {
            get => _releaseGracePeriodMs;
            set => _releaseGracePeriodMs = Mathf.Clamp(value, 0, 1000);
        }

        private void Awake()
        {
            _mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            _logger = new AuctionLogger(_enableVerboseDebug);

            if (_bidService == null && _autoCreateService)
            {
                _bidService = BuildDefaultService();
                _ownsService = _bidService != null;
            }

            if (_bidService != null)
            {
                _bidService.MinUtteranceMs = _minimumUtteranceMs;
                _bidService.DebugParsing = _enableVerboseDebug;
                _bidService.OnPartialTranscript += HandlePartial;
                _bidService.OnBidFinalized += HandleFinalized;
            }
            else if (_inputMode != InputMode.External)
            {
                _logger.LogWarning(
                    "No BidProcessorService is assigned and auto-create is off; push-to-talk is inert.");
            }
        }

        /// <summary>
        /// Builds a service from the default components.
        /// </summary>
        /// <remarks>
        /// Override this in a derived controller to inject a pre-loaded engine, a custom parser
        /// or a replay source instead of the microphone.
        /// </remarks>
        protected virtual BidProcessorService BuildDefaultService()
        {
            return null;
        }

        /// <summary>
        /// Assigns the service at runtime. Takes ownership only when
        /// <paramref name="takeOwnership"/> is true.
        /// </summary>
        public void SetService(BidProcessorService service, bool takeOwnership = false)
        {
            if (_bidService != null)
            {
                _bidService.OnPartialTranscript -= HandlePartial;
                _bidService.OnBidFinalized -= HandleFinalized;
            }

            _bidService = service;
            _ownsService = takeOwnership;

            if (_bidService != null)
            {
                _bidService.OnPartialTranscript += HandlePartial;
                _bidService.OnBidFinalized += HandleFinalized;
            }
        }

        private void OnEnable()
        {
#if AUCTION_VOICE_INPUT_SYSTEM
            if (_inputMode == InputMode.InputSystemAction && _talkAction != null && _talkAction.action != null)
            {
                _talkAction.action.Enable();
                _talkAction.action.started += HandleActionStarted;
                _talkAction.action.canceled += HandleActionCanceled;
            }
#endif
        }

        private void OnDisable()
        {
#if AUCTION_VOICE_INPUT_SYSTEM
            if (_talkAction != null && _talkAction.action != null)
            {
                _talkAction.action.started -= HandleActionStarted;
                _talkAction.action.canceled -= HandleActionCanceled;
            }
#endif
            if (_graceRoutine != null)
            {
                StopCoroutine(_graceRoutine);
                _graceRoutine = null;
            }

            if (_bidService != null && _bidService.IsListening)
            {
                _bidService.CancelSpeechSession();
            }

            _isHolding = false;
        }

        private void Update()
        {
            DrainMainThreadQueue();

            if (_inputMode == InputMode.LegacyKeyCode)
            {
                if (Input.GetKeyDown(_fallbackKey))
                {
                    BeginPushToTalk();
                }
                else if (Input.GetKeyUp(_fallbackKey))
                {
                    EndPushToTalk();
                }
            }

            // Drain captured audio on the main thread; the service feeds Vosk from its own call.
            if (_bidService != null && _bidService.IsListening)
            {
                if (_bidService.Capture is UnityMicrophoneCapture microphone)
                {
                    microphone.Tick();
                }
            }
        }

        private void DrainMainThreadQueue()
        {
            if (_mainThreadQueue.Count == 0)
            {
                return;
            }

            while (_mainThreadQueue.Count > 0)
            {
                Action action = _mainThreadQueue.Dequeue();
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

#if AUCTION_VOICE_INPUT_SYSTEM
        private void HandleActionStarted(InputAction.CallbackContext context)
        {
            BeginPushToTalk();
        }

        private void HandleActionCanceled(InputAction.CallbackContext context)
        {
            EndPushToTalk();
        }
#endif

        /// <summary>Opens a speech session. Safe to call when already listening.</summary>
        public void BeginPushToTalk()
        {
            if (_bidService == null)
            {
                return;
            }

            _isHolding = true;

            // Re-pressed during the grace period: this is a hesitation, not a new utterance.
            if (_graceRoutine != null)
            {
                StopCoroutine(_graceRoutine);
                _graceRoutine = null;
                return;
            }

            if (_bidService.IsListening || _finalizing)
            {
                return;
            }

            _bidService.BeginSpeechSession();
            OnListeningStarted.Invoke();
        }

        /// <summary>Arms the grace period. Finalisation happens when it expires.</summary>
        public void EndPushToTalk()
        {
            _isHolding = false;

            if (_bidService == null || !_bidService.IsListening)
            {
                return;
            }

            if (_graceRoutine != null)
            {
                StopCoroutine(_graceRoutine);
            }

            _graceRoutine = StartCoroutine(GracePeriodRoutine());
        }

        private IEnumerator GracePeriodRoutine()
        {
            if (_releaseGracePeriodMs > 0)
            {
                yield return new WaitForSecondsRealtime(_releaseGracePeriodMs / 1000f);
            }

            _graceRoutine = null;

            if (_isHolding)
            {
                yield break;
            }

            OnListeningStopped.Invoke();
            _finalizing = true;

            Task<BidResult> pending = _bidService.EndSpeechSessionAsync(_trailingSilencePaddingMs);
            while (!pending.IsCompleted)
            {
                yield return null;
            }

            _finalizing = false;

            if (pending.IsFaulted && pending.Exception != null)
            {
                Debug.LogException(pending.Exception.GetBaseException());
            }
        }

        private void HandlePartial(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            Enqueue(() => OnPartialReceived.Invoke(text));
        }

        private void HandleFinalized(BidResult result)
        {
            if (result == null)
            {
                return;
            }

            Enqueue(() => OnBidRecognized.Invoke(result));
        }

        private void Enqueue(Action action)
        {
            if (action == null)
            {
                return;
            }

            // Events already arrive on the main thread in the shipped flow (the service raises
            // them from the awaited continuation), but queueing keeps this correct if a custom
            // service raises from a worker thread.
            if (System.Threading.Thread.CurrentThread.ManagedThreadId == _mainThreadId)
            {
                action();
                return;
            }

            lock (_mainThreadQueue)
            {
                _mainThreadQueue.Enqueue(action);
            }
        }

        private int _mainThreadId;

        private void OnDestroy()
        {
            if (_bidService != null)
            {
                _bidService.OnPartialTranscript -= HandlePartial;
                _bidService.OnBidFinalized -= HandleFinalized;
                if (_ownsService)
                {
                    _bidService.Dispose();
                }
            }
        }
    }
}
