using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace AuctionVoice.HeadlessRunner
{
    /// <summary>
    /// Minimal NUnit-compatible runner.
    /// </summary>
    /// <remarks>
    /// The package's tests are real NUnit tests (so Unity's Test Runner executes them). This
    /// runner exists only because the authoring workstation has no Unity editor and no .NET
    /// SDK: it reflects over the compiled test assembly, honours <c>[Test]</c>,
    /// <c>[TestCase]</c> and <c>[TestCaseSource]</c>, and reports pass/fail counts.
    /// It is not shipped.
    /// </remarks>
    public static class Program
    {
        public static int Main(string[] args)
        {
            Assembly assembly = typeof(Program).Assembly;
            int passed = 0;
            int failed = 0;
            var failures = new List<string>();
            var executed = new List<string>();

            foreach (Type type in assembly.GetTypes().OrderBy(t => t.FullName))
            {
                if (type.GetCustomAttribute<NUnit.Framework.TestFixtureAttribute>() == null)
                {
                    continue;
                }

                MethodInfo[] methods = type.GetMethods(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                MethodInfo setUp = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.GetCustomAttribute<NUnit.Framework.SetUpAttribute>() != null);

                foreach (MethodInfo method in methods.OrderBy(m => m.Name))
                {
                    var testAttr = method.GetCustomAttribute<NUnit.Framework.TestAttribute>();
                    var caseAttrs = method.GetCustomAttributes<NUnit.Framework.TestCaseAttribute>().ToArray();
                    var sourceAttrs = method.GetCustomAttributes<NUnit.Framework.TestCaseSourceAttribute>().ToArray();

                    if (testAttr == null && caseAttrs.Length == 0 && sourceAttrs.Length == 0)
                    {
                        continue;
                    }

                    var invocations = new List<(string Name, object[] Args)>();

                    if (testAttr != null)
                    {
                        invocations.Add(($"{type.Name}.{method.Name}", Array.Empty<object>()));
                    }

                    foreach (var caseAttr in caseAttrs)
                    {
                        invocations.Add((
                            $"{type.Name}.{method.Name}({string.Join(", ", caseAttr.Arguments)})",
                            caseAttr.Arguments));
                    }

                    foreach (var sourceAttr in sourceAttrs)
                    {
                        object source = ResolveSource(type, sourceAttr.SourceName);
                        foreach (object[] row in (System.Collections.IEnumerable)source)
                        {
                            invocations.Add((
                                $"{type.Name}.{method.Name}({string.Join(", ", row)})",
                                row));
                        }
                    }

                    foreach (var (name, callArgs) in invocations)
                    {
                        object instance = Activator.CreateInstance(type);
                        try
                        {
                            setUp?.Invoke(instance, null);
                            object result = method.Invoke(instance, callArgs);
                            if (result is System.Collections.IEnumerable asyncEnumerable)
                            {
                                foreach (object step in asyncEnumerable)
                                {
                                    _ = step;
                                }
                            }

                            passed++;
                            executed.Add(name);
                        }
                        catch (TargetInvocationException ex)
                        {
                            failed++;
                            Exception inner = ex.InnerException ?? ex;
                            failures.Add($"FAIL {name}\n     {inner.Message.Replace("\n", "\n     ")}");
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            failures.Add($"ERROR {name}\n     {ex.Message}");
                        }
                    }
                }
            }

            foreach (string failure in failures)
            {
                Console.WriteLine(failure);
                Console.WriteLine();
            }

            if (args != null && Array.IndexOf(args, "-v") >= 0)
            {
                foreach (string name in executed)
                {
                    Console.WriteLine($"  ok  {name}");
                }
            }

            Console.WriteLine($"{passed} passed, {failed} failed");
            return failed == 0 ? 0 : 1;
        }

        private static object ResolveSource(Type type, string name)
        {
            FieldInfo field = type.GetField(
                name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (field != null)
            {
                return field.GetValue(null);
            }

            PropertyInfo property = type.GetProperty(
                name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (property != null)
            {
                return property.GetValue(null);
            }

            MethodInfo method = type.GetMethod(
                name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (method != null)
            {
                return method.Invoke(null, null);
            }

            throw new InvalidOperationException($"Test case source '{name}' not found on {type.Name}");
        }
    }
}
