using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AuctionVoice.ASR;
using AuctionVoice.Audio;
using AuctionVoice.Core;
using AuctionVoice.Service;
using NUnit.Framework;

namespace AuctionVoice.Tests
{
    /// <summary>
    /// A recognition engine that returns canned text, so the service can be tested without a
    /// model, a microphone or the native library.
    /// </summary>
    public sealed class FakeRecognitionEngine : ISpeechRecognitionEngine
    {
        private readonly string[] _hypotheses;

        public FakeRecognitionEngine(params string[] hypotheses)
        {
            _hypotheses = hypotheses ?? Array.Empty<string>();
        }

        public bool IsInitialized => true;
        public string ModelName => "fake";
        public int SampleRate => 16000;
        public int SessionsCreated { get; private set; }
        public int Disposed { get; private set; }

        public void Initialize(string modelPath)
        {
        }

        public IStreamingSession CreateSession()
        {
            SessionsCreated++;
            return new FakeSession(_hypotheses);
        }

        public void Dispose()
        {
            Disposed++;
        }
    }

    internal sealed class FakeSession : IStreamingSession
    {
        private readonly string[] _hypotheses;
        private bool _disposed;

        public FakeSession(string[] hypotheses)
        {
            _hypotheses = hypotheses;
        }

        public int BytesFed { get; private set; }
        public void Start()
        {
        }

        public string Feed(byte[] pcm, int count)
        {
            BytesFed += count;
            return null;
        }

        public AsrResult Finish()
        {
            var result = new AsrResult { Text = _hypotheses.Length > 0 ? _hypotheses[0] : string.Empty };
            foreach (string hypothesis in _hypotheses)
            {
                result.Alternatives.Add(new Alternative { Text = hypothesis });
            }

            return result;
        }

        public void Reset()
        {
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }

    /// <summary>Tests for the capture-to-parse orchestration.</summary>
    [TestFixture]
    public sealed class BidProcessorServiceTests
    {
        private static byte[] Loud(int milliseconds)
        {
            byte[] pcm = new byte[16000 * milliseconds / 1000 * 2];
            for (int i = 0; i < pcm.Length; i += 2)
            {
                pcm[i] = 0xFF;
                pcm[i + 1] = 0x7F;
            }

            return pcm;
        }

        private static BidProcessorService BuildService(out FakeRecognitionEngine engine, params string[] hypotheses)
        {
            engine = new FakeRecognitionEngine(hypotheses);
            return new BidProcessorService(engine, capture: null, new NLU.AuctionBidParser());
        }

        [Test]
        public async Task ParsesTheFinalTranscript()
        {
            BidProcessorService service = BuildService(out _, "mais vinte mais vinte mais sete");
            service.BeginSpeechSession();
            byte[] audio = Loud(500);
            service.PushAudio(audio, audio.Length);

            BidResult result = await service.EndSpeechSessionAsync(0);

            Assert.That(result.Kind, Is.EqualTo(BidKind.BidDelta));
            Assert.That(result.Value, Is.EqualTo(47d).Within(0.01d));
            Assert.That(result.Currency, Is.EqualTo("R$ 47,00"));
            service.Dispose();
        }

        [Test]
        public async Task RejectsUtterancesShorterThanTheMinimum()
        {
            BidProcessorService service = BuildService(out _, "cinquenta");
            service.BeginSpeechSession();
            byte[] audio = Loud(100); // below the 250 ms gate
            service.PushAudio(audio, audio.Length);

            BidResult result = await service.EndSpeechSessionAsync(0);

            Assert.That(result.RejectReason, Is.EqualTo(BidRejectReason.TooShort));
            Assert.That(result.Reason, Is.EqualTo("too_short"));
            Assert.That(result.Kind, Is.EqualTo(BidKind.Unknown));
            service.Dispose();
        }

        [Test]
        public async Task RejectsSilentAudio()
        {
            BidProcessorService service = BuildService(out _, "cinquenta");
            service.BeginSpeechSession();
            byte[] silence = PcmAudio.Silence(1000);
            service.PushAudio(silence, silence.Length);

            BidResult result = await service.EndSpeechSessionAsync(0);

            Assert.That(result.RejectReason, Is.EqualTo(BidRejectReason.NoSpeech));
            Assert.That(result.Reason, Is.EqualTo("no_speech"));
            service.Dispose();
        }

        [Test]
        public async Task ReturnsSilenceWhenNoSessionWasOpened()
        {
            BidProcessorService service = BuildService(out _, "cinquenta");
            BidResult result = await service.EndSpeechSessionAsync(0);
            Assert.That(result.RejectReason, Is.EqualTo(BidRejectReason.Silence));
            service.Dispose();
        }

        [Test]
        public async Task RaisesTheFinalizedEvent()
        {
            BidProcessorService service = BuildService(out _, "cinquenta");
            BidResult captured = null;
            service.OnBidFinalized += result => captured = result;

            service.BeginSpeechSession();
            byte[] audio = Loud(500);
            service.PushAudio(audio, audio.Length);
            await service.EndSpeechSessionAsync(0);

            Assert.That(captured, Is.Not.Null);
            Assert.That(captured.Value, Is.EqualTo(50d).Within(0.01d));
            service.Dispose();
        }

        [Test]
        public async Task UsesParserScoredNBestRatherThanDecoderOrder()
        {
            // Vosk ranked "mas vindo de" above "mais vinte"; the parser must pick the bid.
            BidProcessorService service = BuildService(out _, "mas vindo de", "mais vinte");
            service.BeginSpeechSession();
            byte[] audio = Loud(500);
            service.PushAudio(audio, audio.Length);

            BidResult result = await service.EndSpeechSessionAsync(0);

            Assert.That(result.RawTranscript, Is.EqualTo("mais vinte"));
            Assert.That(result.Value, Is.EqualTo(20d).Within(0.01d));
            Assert.That(result.NBest.Count, Is.EqualTo(2));
            Assert.That(result.NBest[1].Chosen, Is.True);
            service.Dispose();
        }

        [Test]
        public void CancellingClosesTheSessionWithoutAResult()
        {
            BidProcessorService service = BuildService(out _, "cinquenta");
            bool raised = false;
            service.OnBidFinalized += _ => raised = true;

            service.BeginSpeechSession();
            Assert.That(service.IsListening, Is.True);
            service.CancelSpeechSession();

            Assert.That(service.IsListening, Is.False);
            Assert.That(raised, Is.False);
            service.Dispose();
        }

        [Test]
        public void PushAudioIsIgnoredWhenNotListening()
        {
            BidProcessorService service = BuildService(out _, "cinquenta");
            byte[] audio = Loud(500);
            Assert.DoesNotThrow(() => service.PushAudio(audio, audio.Length));
            service.Dispose();
        }

        [Test]
        public void DisposeReleasesTheEngine()
        {
            BidProcessorService service = BuildService(out FakeRecognitionEngine engine, "cinquenta");
            service.Dispose();
            Assert.That(engine.Disposed, Is.EqualTo(1));
        }
    }

    /// <summary>Tests for the n-best extraction from raw Vosk JSON.</summary>
    [TestFixture]
    public sealed class VoskJsonTests
    {
        [Test]
        public void ParsesPlainTextResult()
        {
            JsonValue payload = JsonValue.Parse("{\"text\": \"mais vinte\"}");
            Assert.That(payload["text"].AsString, Is.EqualTo("mais vinte"));
        }

        [Test]
        public void ParsesAlternatives()
        {
            const string json =
                "{\"alternatives\":[{\"text\":\"mais vinte\",\"confidence\":0.82},"
                + "{\"text\":\"mas vindo de\",\"confidence\":0.91}]}";
            JsonValue payload = JsonValue.Parse(json);
            Assert.That(payload["alternatives"].Count, Is.EqualTo(2));
            Assert.That(payload["alternatives"][1]["text"].AsString, Is.EqualTo("mas vindo de"));
            Assert.That(payload["alternatives"][0]["confidence"].AsNumber, Is.EqualTo(0.82d).Within(0.001d));
        }

        [Test]
        public void ParsesPartialResult()
        {
            Assert.That(JsonValue.Parse("{\"partial\":\"mais vin\"}")["partial"].AsString,
                Is.EqualTo("mais vin"));
        }

        [Test]
        public void ParsesEmptyResult()
        {
            JsonValue payload = JsonValue.Parse("{\"text\":\"\"}");
            Assert.That(payload["text"].AsString, Is.EqualTo(string.Empty));
            Assert.That(payload["missing"].Kind, Is.EqualTo(JsonKind.Null));
        }

        [Test]
        public void DecodesUnicodeEscapes()
        {
            JsonValue payload = JsonValue.Parse("{\"text\":\"gar\\u00e7a\"}");
            Assert.That(payload["text"].AsString, Is.EqualTo("garça"));
        }

        [Test]
        public void MalformedInputReturnsNullInsteadOfThrowing()
        {
            Assert.That(JsonValue.Parse("{\"text\": ").Kind, Is.EqualTo(JsonKind.Null));
            Assert.That(JsonValue.Parse("not json").Kind, Is.EqualTo(JsonKind.Null));
            Assert.That(JsonValue.Parse(null).Kind, Is.EqualTo(JsonKind.Null));
        }

        [Test]
        public void TraceIsPopulatedOnlyWhenDebugIsRequested()
        {
            var parser = new NLU.AuctionBidParser();
            Assert.That(parser.Parse("cinquenta").Trace, Is.Empty);
            Assert.That(parser.Parse("cinquenta", debug: true).Trace, Is.Not.Empty);
        }

        [Test]
        public void NBestTextsDeduplicatesAndKeepsDecoderOrder()
        {
            var result = new AsrResult { Text = "mais vinte" };
            result.Alternatives.Add(new Alternative { Text = "mais vinte" });
            result.Alternatives.Add(new Alternative { Text = "mais  vinte " });
            result.Alternatives.Add(new Alternative { Text = "mas vindo de" });

            List<string> texts = result.NBestTexts();
            Assert.That(texts, Is.EqualTo(new[] { "mais vinte", "mas vindo de" }));
        }
    }
}
