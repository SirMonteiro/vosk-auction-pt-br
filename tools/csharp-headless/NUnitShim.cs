// Minimal source-compatible subset of the NUnit API used by the package's tests.
//
// The real NUnit assemblies are provided by Unity's Test Framework (and by NuGet in CI).
// This shim exists only so tools/csharp-headless can compile and run the *same* test
// sources on a workstation with no Unity editor and no NuGet access. It is never
// compiled into the package: it lives under tools/ and is excluded from the .asmdef.

using System;
using System.Collections;
using System.Collections.Generic;

namespace NUnit.Framework
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class TestFixtureAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TestAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class SetUpAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class TestCaseAttribute : Attribute
    {
        public TestCaseAttribute(params object[] arguments)
        {
            Arguments = arguments ?? Array.Empty<object>();
        }

        public object[] Arguments { get; }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class TestCaseSourceAttribute : Attribute
    {
        public TestCaseSourceAttribute(string sourceName)
        {
            SourceName = sourceName;
        }

        public string SourceName { get; }
    }

    public sealed class AssertionException : Exception
    {
        public AssertionException(string message) : base(message)
        {
        }
    }

    public static class Assert
    {
        public static void That(object actual, IResolveConstraint constraint, string message = null)
        {
            if (!constraint.Matches(actual, out string detail))
            {
                throw new AssertionException(
                    (message == null ? string.Empty : message + "\n  ") + detail);
            }
        }

        public static void That(bool condition, string message = null)
        {
            if (!condition)
            {
                throw new AssertionException(message ?? "expected condition to be true");
            }
        }

        public static void AreEqual(object expected, object actual, string message = null)
        {
            if (!Equals(expected, actual))
            {
                throw new AssertionException(
                    (message == null ? string.Empty : message + "\n  ")
                    + $"expected {Format(expected)} but was {Format(actual)}");
            }
        }

        public static void IsTrue(bool condition, string message = null)
        {
            That(condition, message ?? "expected true");
        }

        public static void IsFalse(bool condition, string message = null)
        {
            That(!condition, message ?? "expected false");
        }

        public static void DoesNotThrow(Action action, string message = null)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                throw new AssertionException(
                    (message == null ? string.Empty : message + "\n  ") + "unexpected exception: " + ex);
            }
        }

        public static void Throws<T>(Action action, string message = null) where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                return;
            }
            catch (Exception ex)
            {
                throw new AssertionException(
                    "expected " + typeof(T).Name + " but got " + ex.GetType().Name);
            }

            throw new AssertionException("expected " + typeof(T).Name + " but nothing was thrown");
        }

        internal static string Format(object value)
        {
            if (value == null)
            {
                return "null";
            }

            if (value is IEnumerable enumerable && !(value is string))
            {
                var parts = new List<string>();
                foreach (object item in enumerable)
                {
                    parts.Add(Format(item));
                }

                return "[" + string.Join(", ", parts) + "]";
            }

            return value.ToString();
        }
    }

    public interface IResolveConstraint
    {
        bool Matches(object actual, out string detail);
    }

    /// <summary>Tolerance-bearing constraint (NUnit's <c>Within</c>).</summary>
    public interface IWithinConstraint : IResolveConstraint
    {
        IResolveConstraint Within(float tolerance);

        IResolveConstraint Within(double tolerance);
    }

    public static class Is
    {
        public static IWithinConstraint EqualTo(object expected)
        {
            return new EqualConstraint(expected, 0f, false);
        }

        public static IWithinConstraint EqualTo(float expected)
        {
            return new EqualConstraint(expected, 0f, false);
        }

        public static IWithinConstraint EqualTo(double expected)
        {
            return new EqualConstraint(expected, 0d, false);
        }

        public static IWithinConstraint EqualTo(int expected)
        {
            return new EqualConstraint(expected, 0f, false);
        }

        public static IWithinConstraint EqualTo(string expected)
        {
            return new EqualConstraint(expected, 0f, false);
        }

        public static IResolveConstraint Empty
        {
            get { return new EmptyConstraint(); }
        }

        public static IResolveConstraint GreaterThanOrEqualTo(int expected)
        {
            return new ComparisonConstraint(expected, ComparisonKind.GreaterOrEqual);
        }

        public static IResolveConstraint GreaterThan(int expected)
        {
            return new ComparisonConstraint(expected, ComparisonKind.Greater);
        }

        public static IResolveConstraint LessThan(int expected)
        {
            return new ComparisonConstraint(expected, ComparisonKind.Less);
        }

        public static IResolveConstraint LessThanOrEqualTo(int expected)
        {
            return new ComparisonConstraint(expected, ComparisonKind.LessOrEqual);
        }

        public static IResolveConstraint True
        {
            get { return new BoolConstraint(true); }
        }

        public static IResolveConstraint False
        {
            get { return new BoolConstraint(false); }
        }

        public static IResolveConstraint Null
        {
            get { return new NullConstraint(true); }
        }

        public static NotConstraint Not
        {
            get { return new NotConstraint(); }
        }

        public static IResolveConstraint SameAs(object expected)
        {
            return new SameAsConstraint(expected);
        }
    }

    public sealed class EqualConstraint : IWithinConstraint
    {
        private readonly object _expected;
        private readonly double _tolerance;
        private readonly bool _hasTolerance;

        public EqualConstraint(object expected, double tolerance, bool hasTolerance)
        {
            _expected = expected;
            _tolerance = tolerance;
            _hasTolerance = hasTolerance;
        }

        public EqualConstraint Within(float tolerance)
        {
            return new EqualConstraint(_expected, tolerance, true);
        }

        public EqualConstraint Within(double tolerance)
        {
            return new EqualConstraint(_expected, tolerance, true);
        }

        IResolveConstraint IWithinConstraint.Within(float tolerance)
        {
            return Within(tolerance);
        }

        IResolveConstraint IWithinConstraint.Within(double tolerance)
        {
            return Within(tolerance);
        }

        public bool Matches(object actual, out string detail)
        {
            detail = null;

            if (_expected is IEnumerable expectedEnumerable
                && !(_expected is string)
                && actual is IEnumerable actualEnumerable
                && !(actual is string))
            {
                var expectedItems = new List<object>();
                foreach (object item in expectedEnumerable)
                {
                    expectedItems.Add(item);
                }

                var actualItems = new List<object>();
                foreach (object item in actualEnumerable)
                {
                    actualItems.Add(item);
                }

                if (expectedItems.Count != actualItems.Count)
                {
                    detail = $"expected {Assert.Format(_expected)} but was {Assert.Format(actual)}";
                    return false;
                }

                for (int i = 0; i < expectedItems.Count; i++)
                {
                    if (!ItemMatches(expectedItems[i], actualItems[i]))
                    {
                        detail = $"expected {Assert.Format(_expected)} but was {Assert.Format(actual)}";
                        return false;
                    }
                }

                return true;
            }

            if (ItemMatches(_expected, actual))
            {
                return true;
            }

            detail = $"expected {Assert.Format(_expected)} but was {Assert.Format(actual)}";
            return false;
        }

        private bool ItemMatches(object expected, object actual)
        {
            if (_hasTolerance)
            {
                double e = Convert.ToDouble(expected);
                double a = Convert.ToDouble(actual);
                return Math.Abs(e - a) <= _tolerance;
            }

            // Numeric types must compare by value, not by CLR type: a float 1000 and a
            // double 1000 are equal for every assertion in this suite.
            if (NumericHelper.IsNumeric(expected) && NumericHelper.IsNumeric(actual))
            {
                return Convert.ToDouble(expected).Equals(Convert.ToDouble(actual));
            }

            return Equals(expected, actual);
        }
    }

    internal static class NumericHelper
    {
        public static bool IsNumeric(object value)
        {
            return value is byte || value is sbyte || value is short || value is ushort
                   || value is int || value is uint || value is long || value is ulong
                   || value is float || value is double || value is decimal;
        }
    }

    public sealed class EmptyConstraint : IResolveConstraint
    {
        public bool Matches(object actual, out string detail)
        {
            detail = null;
            if (actual is ICollection collection)
            {
                if (collection.Count == 0)
                {
                    return true;
                }

                detail = $"expected empty but had {collection.Count} item(s): {Assert.Format(actual)}";
                return false;
            }

            if (actual is IEnumerable enumerable)
            {
                foreach (object item in enumerable)
                {
                    detail = $"expected empty but had at least one item: {Assert.Format(actual)}";
                    return false;
                }

                return true;
            }

            detail = "expected an empty collection";
            return false;
        }
    }

    public sealed class BoolConstraint : IResolveConstraint
    {
        private readonly bool _expected;

        public BoolConstraint(bool expected)
        {
            _expected = expected;
        }

        public bool Matches(object actual, out string detail)
        {
            bool value = actual is bool b && b;
            if (value == _expected)
            {
                detail = null;
                return true;
            }

            detail = $"expected {_expected} but was {value}";
            return false;
        }
    }

    public sealed class NullConstraint : IResolveConstraint
    {
        private readonly bool _expectNull;

        public NullConstraint(bool expectNull)
        {
            _expectNull = expectNull;
        }

        public bool Matches(object actual, out string detail)
        {
            bool isNull = actual == null;
            if (isNull == _expectNull)
            {
                detail = null;
                return true;
            }

            detail = _expectNull ? "expected null" : "expected a value";
            return false;
        }
    }

    public sealed class SameAsConstraint : IResolveConstraint
    {
        private readonly object _expected;

        public SameAsConstraint(object expected)
        {
            _expected = expected;
        }

        public bool Matches(object actual, out string detail)
        {
            if (ReferenceEquals(actual, _expected))
            {
                detail = null;
                return true;
            }

            detail = "expected the same instance";
            return false;
        }
    }

    public sealed class NotConstraint : IResolveConstraint
    {
        private IResolveConstraint _inner;

        public NotConstraint Not(IResolveConstraint inner)
        {
            _inner = inner;
            return this;
        }

        public NotConstraint Null => Not(new NullConstraint(true));

        public NotConstraint Empty => Not(new EmptyConstraint());

        public bool Matches(object actual, out string detail)
        {
            if (_inner == null)
            {
                detail = "Not() was not composed with a constraint";
                return false;
            }

            if (_inner.Matches(actual, out _))
            {
                detail = "expected the negation to match";
                return false;
            }

            detail = null;
            return true;
        }
    }

    public enum ComparisonKind
    {
        Greater,
        GreaterOrEqual,
        Less,
        LessOrEqual
    }

    public sealed class ComparisonConstraint : IResolveConstraint
    {
        private readonly int _expected;
        private readonly ComparisonKind _kind;

        public ComparisonConstraint(int expected, ComparisonKind kind)
        {
            _expected = expected;
            _kind = kind;
        }

        public bool Matches(object actual, out string detail)
        {
            detail = null;
            int value = Convert.ToInt32(actual);
            bool ok;
            switch (_kind)
            {
                case ComparisonKind.Greater:
                    ok = value > _expected;
                    break;
                case ComparisonKind.Less:
                    ok = value < _expected;
                    break;
                case ComparisonKind.LessOrEqual:
                    ok = value <= _expected;
                    break;
                default:
                    ok = value >= _expected;
                    break;
            }

            if (!ok)
            {
                detail = $"expected {_kind} {_expected} but was {value}";
            }

            return ok;
        }
    }
}
