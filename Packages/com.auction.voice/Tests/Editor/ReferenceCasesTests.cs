using AuctionVoice.Core;
using NUnit.Framework;

namespace AuctionVoice.Tests
{
    /// <summary>
    /// The authoritative parity suite: every row of <c>tests/fixtures/text_cases.jsonl</c>,
    /// generated into <see cref="ReferenceCases"/> by <c>tools/gen_csharp_lexicon.py</c>.
    /// </summary>
    /// <remarks>
    /// This is the acceptance gate for the whole NLU port. If a single case drifts, the C#
    /// parser and the Python reference no longer agree, and the game would behave differently
    /// depending on which implementation is wired in.
    /// </remarks>
    [TestFixture]
    public sealed class ReferenceCasesTests
    {
        private NLU.AuctionBidParser _parser;

        [SetUp]
        public void SetUp()
        {
            _parser = new NLU.AuctionBidParser();
        }

        [Test]
        public void FixtureIsNotEmpty()
        {
            Assert.That(ReferenceCases.All.Length, Is.GreaterThanOrEqualTo(40),
                "the reference suite must contain at least the 40 legacy cases");
        }

        [Test]
        public void EveryReferenceCaseMatchesThePythonContract()
        {
            var failures = new System.Collections.Generic.List<string>();

            foreach (ReferenceCase expected in ReferenceCases.All)
            {
                Core.BidResult actual = _parser.Parse(expected.Text);

                if (System.Math.Abs(actual.Value - expected.Value) >= 0.01d)
                {
                    failures.Add($"{expected.Text}: value expected {expected.Value} but was {actual.Value}");
                }

                if (actual.HasAddition != expected.HasAddition)
                {
                    failures.Add($"{expected.Text}: has_addition expected {expected.HasAddition} but was {actual.HasAddition}");
                }

                if (actual.HasMultiplication != expected.HasMultiplication)
                {
                    failures.Add($"{expected.Text}: has_multiplication expected {expected.HasMultiplication} but was {actual.HasMultiplication}");
                }

                if (actual.Action.ToWire() != expected.ActionType)
                {
                    failures.Add($"{expected.Text}: action_type expected {expected.ActionType} but was {actual.Action.ToWire()}");
                }

                if (actual.Kind.ToWire() != expected.BidKind)
                {
                    failures.Add($"{expected.Text}: bid_kind expected {expected.BidKind} but was {actual.Kind.ToWire()}");
                }
            }

            Assert.That(failures, Is.Empty, string.Join("\n", failures));
        }

        [Test]
        public void ReferenceSuiteScoresFullMarks()
        {
            int passed = 0;
            foreach (ReferenceCase expected in ReferenceCases.All)
            {
                Core.BidResult actual = _parser.Parse(expected.Text);
                bool ok = System.Math.Abs(actual.Value - expected.Value) < 0.01d
                          && actual.HasAddition == expected.HasAddition
                          && actual.HasMultiplication == expected.HasMultiplication
                          && actual.Action.ToWire() == expected.ActionType
                          && actual.Kind.ToWire() == expected.BidKind;
                if (ok)
                {
                    passed++;
                }
            }

            Assert.That(passed, Is.EqualTo(ReferenceCases.All.Length),
                $"reference suite scored {passed}/{ReferenceCases.All.Length}");
        }
    }
}
