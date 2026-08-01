using System.Collections;
using System.Collections.Generic;
using System.IO;
using Kobapps.GameTestKit;
using Kobapps.GameTestKit.Scripting;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace GameTestKit.Bridge
{
    /// <summary>
    /// Exposes every <c>.gametest.json</c> script as a PlayMode NUnit test, so GameTestKit flows show
    /// up in the Test Runner window and in <c>-runTests</c> alongside your unit tests.
    /// </summary>
    /// <remarks>
    /// Copy this file (and its asmdef) into your project — for example <c>Assets/Tests/PlayMode/</c>.
    /// It is a sample rather than part of the package because Unity only compiles a package's test
    /// assemblies when the package is listed in the project manifest's <c>testables</c>, and because
    /// most teams want to control where their test assembly lives.
    /// <para>
    /// You do not need this to run GameTestKit — the GameTester window and
    /// <c>GameTesterCLI.Run</c> work on their own. Use it when you already have a Unity Test Framework
    /// pipeline and want one command, one report and one set of CI plumbing for everything.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class GameTestSuiteTests
    {
        /// <summary>Discovered at test-discovery time so each script becomes its own named test case.</summary>
        private static IEnumerable<TestCaseData> Scripts()
        {
            var found = false;

            foreach (var source in DiscoverSources())
            {
                if (source.IsSuite) continue;

                string name = null;
                string problem = null;
                try { name = TestScriptParser.ParseTest(source.Json, source.Path).Name; }
                catch (System.Exception e) { problem = e.Message; }

                // .Returns(null) is required: the test method returns IEnumerator (it is a [UnityTest]),
                // and without it NUnit rejects every case with "method has non-void return value".
                // A broken script becomes a visible failing test, not a silently missing one.
                yield return problem != null
                    ? new TestCaseData(source.Path, (string)null)
                        .SetName($"[broken] {Path.GetFileName(source.Path)}: {problem}")
                        .Returns(null)
                    : new TestCaseData(source.Path, source.Json).SetName(name).Returns(null);

                found = true;
            }

            if (!found)
                yield return new TestCaseData((string)null, (string)null)
                    .SetName("(no .gametest.json scripts found)")
                    .Returns(null);
        }

        private static IEnumerable<GameTestSource> DiscoverSources()
        {
#if UNITY_EDITOR
            foreach (var guid in UnityEditor.AssetDatabase.FindAssets("t:TextAsset"))
            {
                var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(TestScriptParser.TestExtension, System.StringComparison.OrdinalIgnoreCase))
                    continue;

                yield return new GameTestSource { Path = path, Json = File.ReadAllText(path) };
            }
#else
            foreach (var source in GameTestCatalog.DiscoverSources())
                yield return source;
#endif
        }

        [UnityTest]
        [TestCaseSource(nameof(Scripts))]
        public IEnumerator GameTest(string path, string json)
        {
            if (path == null)
            {
                Assert.Ignore("No GameTestKit scripts were found in this project.");
                yield break;
            }

            if (json == null)
            {
                Assert.Fail($"'{path}' could not be parsed. Run AICommands.Validate for the details.");
                yield break;
            }

            var test = TestScriptParser.ParseTest(json, path);
            var options = GameTesterSettings.Instance.CreateRunOptions();

            RunReport report = null;
            yield return GameTester.RunSingleAsync(test, options, result => report = result);

            Assert.That(report, Is.Not.Null, "The run produced no report.");

            var record = report.Tests.Count > 0 ? report.Tests[0] : null;
            Assert.That(record, Is.Not.Null, "The run produced no result for this test.");

            if (record.Status == TestStatus.Skipped)
                Assert.Ignore(record.Message);

            if (record.IsFailure)
            {
                var details = new System.Text.StringBuilder();
                details.AppendLine(record.Message);

                foreach (var step in record.Steps)
                    if (step.IsFailure)
                        details.AppendLine($"  at step: {step.Description}");

                foreach (var artifact in record.Artifacts)
                    details.AppendLine($"  screenshot: {artifact}");

                Assert.Fail(details.ToString());
            }

            Debug.Log($"[GameTestKit] {record.Name} passed in {record.DurationSeconds:0.00}s");
        }
    }
}
