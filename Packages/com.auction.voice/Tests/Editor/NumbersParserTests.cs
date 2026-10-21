using AuctionVoice.Core;
using NUnit.Framework;

namespace AuctionVoice.Tests
{
    /// <summary>
    /// Number parsing tests: every case from <c>docs/03-nlu-spec.md</c> section 3, ported
    /// verbatim from <c>tests/test_numbers.py</c>.
    /// </summary>
    [TestFixture]
    public sealed class NumbersParserTests
    {
        private static readonly object[] ParseCases =
        {
            // --- 3.4 accepted forms ------------------------------------------------
            new object[] { "quarenta e sete", new[] { 47d } },
            new object[] { "cento e vinte e cinco", new[] { 125d } },
            new object[] { "mil e quinhentos", new[] { 1500d } },
            new object[] { "mil quinhentos", new[] { 1500d } },
            new object[] { "dois mil e quinhentos", new[] { 2500d } },
            new object[] { "quarenta e sete mil e quinhentos", new[] { 47500d } },
            new object[] { "nove mil novecentos e noventa e nove", new[] { 9999d } },
            new object[] { "um milhão e duzentos mil", new[] { 1200000d } },
            new object[] { "meio milhão", new[] { 500000d } },
            new object[] { "um milhão e meio", new[] { 1500000d } },
            new object[] { "duas onças", new[] { 100d } },
            new object[] { "onça e meia", new[] { 75d } },
            new object[] { "meia dúzia", new[] { 6d } },
            new object[] { "uma dúzia", new[] { 12d } },
            new object[] { "dúzia e meia", new[] { 18d } },
            new object[] { "um barão", new[] { 1000d } },
            new object[] { "três placas", new[] { 300000d } },
            new object[] { "meio pau", new[] { 500d } },
            new object[] { "cinquenta pila", new[] { 50d } },
            new object[] { "cem conto", new[] { 100d } },
            new object[] { "cinquenta reais e vinte centavos", new[] { 50.2d } },
            new object[] { "sete seis", new[] { 76d } },
            new object[] { "cinco zero zero", new[] { 500d } },

            // --- 3.5 rejected / downgraded forms -----------------------------------
            new object[] { "trinta e dez", new[] { 30d, 10d } },
            new object[] { "doze e três", new[] { 12d, 3d } },
            new object[] { "cinquenta, cinquenta, cinquenta", new[] { 50d, 50d, 50d } },

            // --- extra regression cases --------------------------------------------
            new object[] { "vinte e um", new[] { 21d } },
            new object[] { "cento e um", new[] { 101d } },
            new object[] { "novecentos e noventa e nove", new[] { 999d } },
            new object[] { "mil novecentos e noventa e nove", new[] { 1999d } },
            new object[] { "cinquenta e meia", new[] { 50.5d } },
            new object[] { "duzentos e cinquenta mil", new[] { 250000d } },
            new object[] { "vinte e cinco mil reais", new[] { 25000d } },
            new object[] { "duzentos", new[] { 200d } },
            new object[] { "um", new[] { 1d } },
            new object[] { "cem", new[] { 100d } },
            new object[] { "zero", new double[0] }
        };

        [TestCaseSource(nameof(ParseCases))]
        public void ParsesExpectedValues(string text, double[] expected)
        {
            NLU.ParseOutcome outcome = NLU.NumbersParser.ParseNumbers(text.Split(' '));
            Assert.That(outcome.Values, Is.EqualTo(expected).Within(0.001f), $"tokens: {text}");
        }

        [TestCase("um", NLU.NumberTokenKind.Base, NLU.NumberScale.Unit)]
        [TestCase("dez", NLU.NumberTokenKind.Base, NLU.NumberScale.Teen)]
        [TestCase("dezesseis", NLU.NumberTokenKind.Base, NLU.NumberScale.Teen)]
        [TestCase("vinte", NLU.NumberTokenKind.Base, NLU.NumberScale.Ten)]
        [TestCase("cento", NLU.NumberTokenKind.Base, NLU.NumberScale.Hundred)]
        [TestCase("novecentos", NLU.NumberTokenKind.Base, NLU.NumberScale.Hundred)]
        [TestCase("mil", NLU.NumberTokenKind.Multiplier, NLU.NumberScale.Multiplier)]
        [TestCase("onça", NLU.NumberTokenKind.Denomination, NLU.NumberScale.Denomination)]
        [TestCase("reais", NLU.NumberTokenKind.Currency, NLU.NumberScale.Auxiliary)]
        [TestCase("meia", NLU.NumberTokenKind.Half, NLU.NumberScale.Unit)]
        [TestCase("barão", NLU.NumberTokenKind.Magnitude, NLU.NumberScale.Denomination)]
        [TestCase("placa", NLU.NumberTokenKind.Magnitude, NLU.NumberScale.Denomination)]
        [TestCase("47", NLU.NumberTokenKind.Digit, NLU.NumberScale.Ten)]
        [TestCase("250000", NLU.NumberTokenKind.Digit, NLU.NumberScale.Multiplier)]
        public void ClassifiesTokens(string token, NLU.NumberTokenKind kind, int scale)
        {
            NLU.NumberToken classified = NLU.NumbersParser.Classify(token);
            Assert.That(classified.Kind, Is.EqualTo(kind));
            Assert.That(classified.Scale, Is.EqualTo(scale));
        }

        [Test]
        public void MagnitudeFactorsFollowTheUserGlossary()
        {
            Assert.That(NLU.NumbersParser.Classify("barão").Value, Is.EqualTo(1000f));
            Assert.That(NLU.NumbersParser.Classify("placa").Value, Is.EqualTo(100000f));
            Assert.That(NLU.NumbersParser.Classify("pau").Value, Is.EqualTo(1000f));

            // Money synonyms are currency units (factor 1), not magnitudes.
            Assert.That(NLU.NumbersParser.Classify("conto").Value, Is.EqualTo(1f));
            Assert.That(NLU.NumbersParser.Classify("pila").Value, Is.EqualTo(1f));
            Assert.That(NLU.NumbersParser.Classify("conto").Kind, Is.EqualTo(NLU.NumberTokenKind.Currency));
        }

        [Test]
        public void ReportsUnknownTokens()
        {
            NLU.ParseOutcome outcome = NLU.NumbersParser.ParseNumbers(new[] { "cinquenta", "bananas" });
            Assert.That(outcome.Values, Is.EqualTo(new[] { 50d }));
            Assert.That(outcome.Unknown, Is.EqualTo(new[] { "bananas" }));
        }

        [Test]
        public void HandlesEmptyInput()
        {
            Assert.That(NLU.NumbersParser.ParseNumbers(new string[0]).Values, Is.Empty);
            Assert.That(NLU.NumbersParser.ParseNumbers(new[] { string.Empty }).Values, Is.Empty);
        }

        [Test]
        public void ConnectorDoesNotResetScaleMemory()
        {
            // "trinta e dez" is 30 + 10, not 40: the "e" must not bridge it.
            Assert.That(NLU.NumbersParser.ParseNumbers(new[] { "trinta", "e", "dez" }).Values,
                Is.EqualTo(new[] { 30d, 10d }));
        }

        [Test]
        public void NoteQuantifierMultipliesTheFollowingNumber()
        {
            Assert.That(NLU.NumbersParser.ParseNumbers(new[] { "duas", "notas", "de", "cem" }).Values,
                Is.EqualTo(new[] { 200d }));
        }
    }
}
