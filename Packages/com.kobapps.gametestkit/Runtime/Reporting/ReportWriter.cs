using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Kobapps.GameTestKit.Scripting;
using UnityEngine;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// Turns a <see cref="RunReport"/> into the artifacts a team actually consumes: JUnit XML for CI,
    /// JSON for tooling and agents, and a self-contained HTML page for humans.
    /// </summary>
    public static class ReportWriter
    {
        /// <summary>Writes every format listed in <paramref name="options"/>. Returns the files written.</summary>
        public static List<string> WriteAll(RunReport report, RunOptions options, string folder)
        {
            var written = new List<string>();
            if (report == null) return written;

            try { Directory.CreateDirectory(folder); }
            catch (Exception e)
            {
                Debug.LogError($"[GameTestKit] Cannot create report folder '{folder}': {e.Message}");
                return written;
            }

            foreach (var format in options.ReportFormats)
            {
                try
                {
                    switch ((format ?? "").Trim().ToLowerInvariant())
                    {
                        case "junit":
                        case "xml":
                            written.Add(Write(Path.Combine(folder, "results.junit.xml"), ToJUnitXml(report)));
                            break;
                        case "json":
                            written.Add(Write(Path.Combine(folder, "results.json"), ToJson(report).ToJson()));
                            break;
                        case "html":
                            written.Add(Write(Path.Combine(folder, "report.html"), ToHtml(report)));
                            break;
                        default:
                            Debug.LogWarning($"[GameTestKit] Unknown report format '{format}'. Use junit, json or html.");
                            break;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[GameTestKit] Failed to write '{format}' report: {e.Message}");
                }
            }

            return written;
        }

        private static string Write(string path, string content)
        {
            File.WriteAllText(path, content, new UTF8Encoding(false));
            Debug.Log($"[GameTestKit] Wrote {path}");
            return path;
        }

        // ================================================================ JUnit

        /// <summary>JUnit XML — understood by GitHub Actions, GitLab, Jenkins, TeamCity and the rest.</summary>
        public static string ToJUnitXml(RunReport report)
        {
            var xml = new StringBuilder();
            xml.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            xml.AppendLine($"<testsuites name=\"{Escape(report.Suite)}\" tests=\"{report.Total}\" " +
                           $"failures=\"{report.Failed}\" errors=\"{report.Errored + report.TimedOut}\" " +
                           $"skipped=\"{report.Skipped}\" time=\"{Number(report.DurationSeconds)}\">");

            xml.AppendLine($"  <testsuite name=\"{Escape(report.Suite)}\" tests=\"{report.Total}\" " +
                           $"failures=\"{report.Failed}\" errors=\"{report.Errored + report.TimedOut}\" " +
                           $"skipped=\"{report.Skipped}\" time=\"{Number(report.DurationSeconds)}\" " +
                           $"timestamp=\"{Escape(report.StartedAtUtc)}\" hostname=\"{Escape(report.Device)}\">");

            xml.AppendLine("    <properties>");
            xml.AppendLine($"      <property name=\"unityVersion\" value=\"{Escape(report.UnityVersion)}\" />");
            xml.AppendLine($"      <property name=\"platform\" value=\"{Escape(report.Platform)}\" />");
            xml.AppendLine($"      <property name=\"seed\" value=\"{report.Seed}\" />");
            xml.AppendLine("    </properties>");

            foreach (var test in report.Tests)
            {
                xml.AppendLine($"    <testcase classname=\"{Escape(ClassName(report, test))}\" name=\"{Escape(test.Name)}\" " +
                               $"time=\"{Number(test.DurationSeconds)}\">");

                switch (test.Status)
                {
                    case TestStatus.Skipped:
                        xml.AppendLine($"      <skipped message=\"{Escape(test.Message)}\" />");
                        break;

                    case TestStatus.Failed:
                        xml.AppendLine($"      <failure message=\"{Escape(test.Message)}\" type=\"AssertionFailure\">" +
                                       $"{Escape(Detail(test))}</failure>");
                        break;

                    case TestStatus.Timeout:
                        xml.AppendLine($"      <error message=\"{Escape(test.Message)}\" type=\"Timeout\">" +
                                       $"{Escape(Detail(test))}</error>");
                        break;

                    case TestStatus.Error:
                        xml.AppendLine($"      <error message=\"{Escape(test.Message)}\" type=\"Error\">" +
                                       $"{Escape(Detail(test))}</error>");
                        break;
                }

                var output = StepLog(test);
                if (!string.IsNullOrEmpty(output))
                    xml.AppendLine($"      <system-out>{Escape(output)}</system-out>");

                xml.AppendLine("    </testcase>");
            }

            xml.AppendLine("  </testsuite>");
            xml.AppendLine("</testsuites>");
            return xml.ToString();
        }

        /// <summary>
        /// The JUnit <c>classname</c>, which is what every CI dashboard groups and trends by — so the
        /// category belongs in it: <c>GameTestKit.Shop.Checkout</c>.
        /// </summary>
        private static string ClassName(RunReport report, TestRecord test)
        {
            var category = TestCategory.Normalize(test.Category);
            return category.Length == 0
                ? report.Suite
                : $"{report.Suite}.{category.Replace(TestCategory.Separator, '.')}";
        }

        private static string Detail(TestRecord test)
        {
            var builder = new StringBuilder();
            builder.AppendLine(test.Message);
            if (!string.IsNullOrEmpty(test.StackTrace)) builder.AppendLine(test.StackTrace);
            foreach (var artifact in test.Artifacts) builder.AppendLine($"artifact: {artifact}");

            foreach (var step in Flatten(test.Steps))
            {
                if (!step.IsFailure) continue;
                builder.AppendLine($"failed step: {step.Description} — {step.Message}");
                foreach (var artifact in step.Artifacts) builder.AppendLine($"  artifact: {artifact}");
            }

            return builder.ToString();
        }

        private static string StepLog(TestRecord test)
        {
            var builder = new StringBuilder();
            foreach (var step in Flatten(test.Steps))
                builder.AppendLine($"[{step.Status}] {step.Description} ({step.DurationSeconds:0.00}s)");
            return builder.ToString();
        }

        private static IEnumerable<StepRecord> Flatten(IEnumerable<StepRecord> steps)
        {
            foreach (var step in steps)
            {
                yield return step;
                foreach (var child in Flatten(step.Children)) yield return child;
            }
        }

        // ================================================================ JSON

        /// <summary>Structured results — what tooling and agents read back after a run.</summary>
        public static JsonValue ToJson(RunReport report)
        {
            var root = JsonValue.NewObject()
                .Set("suite", report.Suite)
                .Set("success", report.Success)
                .Set("startedAt", report.StartedAtUtc ?? "")
                .Set("finishedAt", report.FinishedAtUtc ?? "")
                .Set("durationSeconds", report.DurationSeconds)
                .Set("unityVersion", report.UnityVersion ?? "")
                .Set("platform", report.Platform ?? "")
                .Set("device", report.Device ?? "")
                .Set("seed", report.Seed);

            root.Set("totals", JsonValue.NewObject()
                .Set("total", report.Total)
                .Set("passed", report.Passed)
                .Set("failed", report.Failed)
                .Set("errored", report.Errored)
                .Set("timedOut", report.TimedOut)
                .Set("skipped", report.Skipped));

            // Per-category totals, so a dashboard can say "Shop is red" without walking every test.
            var categories = JsonValue.NewArray();
            foreach (var group in GroupByCategory(report.Tests))
            {
                int passed = 0, failed = 0;
                foreach (var test in group.Value)
                {
                    if (test.Status == TestStatus.Passed) passed++;
                    else if (test.IsFailure) failed++;
                }

                categories.Add(JsonValue.NewObject()
                    .Set("category", group.Key)
                    .Set("total", group.Value.Count)
                    .Set("passed", passed)
                    .Set("failed", failed));
            }
            root.Set("categories", categories);

            var tests = JsonValue.NewArray();
            foreach (var test in report.Tests)
            {
                var item = JsonValue.NewObject()
                    .Set("name", test.Name)
                    .Set("status", test.Status.ToString().ToLowerInvariant())
                    .Set("durationSeconds", test.DurationSeconds)
                    .Set("attempt", test.Attempt);

                if (!string.IsNullOrEmpty(test.Category)) item.Set("category", test.Category);
                if (!string.IsNullOrEmpty(test.Description)) item.Set("description", test.Description);
                if (!string.IsNullOrEmpty(test.SourcePath)) item.Set("source", test.SourcePath);
                if (!string.IsNullOrEmpty(test.Message)) item.Set("message", test.Message);
                if (!string.IsNullOrEmpty(test.StackTrace)) item.Set("stackTrace", test.StackTrace);

                if (test.Tags.Count > 0)
                {
                    var tags = JsonValue.NewArray();
                    foreach (var tag in test.Tags) tags.Add(JsonValue.New(tag));
                    item.Set("tags", tags);
                }

                item.Set("steps", StepsToJson(test.Steps));

                if (test.Artifacts.Count > 0)
                {
                    var artifacts = JsonValue.NewArray();
                    foreach (var artifact in test.Artifacts) artifacts.Add(JsonValue.New(artifact));
                    item.Set("artifacts", artifacts);
                }

                var errors = JsonValue.NewArray();
                foreach (var log in test.Logs)
                {
                    if (log.Type != "Error" && log.Type != "Exception" && log.Type != "Assert") continue;
                    errors.Add(JsonValue.NewObject()
                        .Set("type", log.Type)
                        .Set("message", log.Message ?? "")
                        .Set("atSeconds", log.TimeStamp)
                        .Set("step", log.StepIndex));
                }
                if (errors.Count > 0) item.Set("logErrors", errors);

                if (test.EventProofs.Count > 0)
                {
                    var proofs = JsonValue.NewArray();
                    foreach (var proof in test.EventProofs) proofs.Add(proof.ToJson());
                    item.Set("eventProofs", proofs);
                }

                if (test.Performance != null)
                {
                    item.Set("performance", JsonValue.NewObject()
                        .Set("frames", test.Performance.Frames)
                        .Set("averageFps", Math.Round(test.Performance.AverageFps, 1))
                        .Set("minFps", Math.Round(test.Performance.MinFps, 1))
                        .Set("worstFrameMs", Math.Round(test.Performance.WorstFrameMs, 2))
                        .Set("p95FrameMs", Math.Round(test.Performance.Percentile95FrameMs, 2))
                        .Set("peakManagedMemoryMb", Math.Round(test.Performance.PeakManagedMemoryBytes / 1048576.0, 1)));
                }

                tests.Add(item);
            }

            root.Set("tests", tests);
            return root;
        }

        /// <summary>
        /// The tests grouped by category, in tree order, keeping each group's original order.
        /// </summary>
        private static List<KeyValuePair<string, List<TestRecord>>> GroupByCategory(List<TestRecord> tests)
        {
            var groups = new Dictionary<string, List<TestRecord>>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();

            foreach (var test in tests)
            {
                var category = TestCategory.Normalize(test.Category);

                if (!groups.TryGetValue(category, out var bucket))
                {
                    bucket = new List<TestRecord>();
                    groups[category] = bucket;
                    order.Add(category);
                }

                bucket.Add(test);
            }

            order.Sort(TestCategory.Compare);

            var result = new List<KeyValuePair<string, List<TestRecord>>>(order.Count);
            foreach (var category in order)
                result.Add(new KeyValuePair<string, List<TestRecord>>(category, groups[category]));

            return result;
        }

        private static JsonValue StepsToJson(List<StepRecord> steps)
        {
            var array = JsonValue.NewArray();
            foreach (var step in steps)
            {
                var item = JsonValue.NewObject()
                    .Set("index", step.Index)
                    .Set("phase", step.Phase)
                    .Set("description", step.Description)
                    .Set("status", step.Status.ToString().ToLowerInvariant())
                    .Set("durationSeconds", Math.Round(step.DurationSeconds, 3));

                if (!string.IsNullOrEmpty(step.Message)) item.Set("message", step.Message);

                if (step.Artifacts.Count > 0)
                {
                    var artifacts = JsonValue.NewArray();
                    foreach (var artifact in step.Artifacts) artifacts.Add(JsonValue.New(artifact));
                    item.Set("artifacts", artifacts);
                }

                if (step.Children.Count > 0) item.Set("steps", StepsToJson(step.Children));

                array.Add(item);
            }
            return array;
        }

        // ================================================================ HTML

        /// <summary>A single self-contained page: summary, per-test steps, failure screenshots inline.</summary>
        public static string ToHtml(RunReport report)
        {
            var html = new StringBuilder();
            html.AppendLine("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
            html.AppendLine($"<title>GameTestKit — {Html(report.Suite)}</title>");
            html.AppendLine(@"<style>
:root{color-scheme:light dark}
body{font:14px/1.5 system-ui,-apple-system,Segoe UI,sans-serif;margin:0;padding:24px;background:#fbfbfd;color:#16181d}
@media(prefers-color-scheme:dark){body{background:#14161a;color:#e7e9ee}}
h1{font-size:20px;margin:0 0 4px}
.meta{opacity:.65;font-size:12px;margin-bottom:20px}
.totals{display:flex;gap:8px;flex-wrap:wrap;margin-bottom:20px}
.pill{padding:6px 12px;border-radius:999px;font-weight:600;font-size:12px;background:#e9ecf2;color:#3c4250}
.pill.ok{background:#12a150;color:#fff}.pill.bad{background:#e5484d;color:#fff}.pill.warn{background:#f5a524;color:#111}
.test{border:1px solid rgba(128,128,128,.25);border-radius:10px;margin-bottom:10px;overflow:hidden}
.test>summary{cursor:pointer;padding:12px 14px;font-weight:600;display:flex;gap:10px;align-items:center}
.test[data-status=passed]>summary{border-left:4px solid #12a150}
.test[data-status=failed]>summary,.test[data-status=error]>summary,.test[data-status=timeout]>summary{border-left:4px solid #e5484d}
.test[data-status=skipped]>summary{border-left:4px solid #9aa0aa}
.dur{margin-left:auto;font-weight:400;opacity:.6;font-size:12px}
.body{padding:0 14px 14px}
.msg{background:rgba(229,72,77,.12);border-radius:8px;padding:10px 12px;margin:8px 0;white-space:pre-wrap;font-family:ui-monospace,SFMono-Regular,Menlo,monospace;font-size:12px}
ol.steps{margin:8px 0;padding-left:22px}
ol.steps li{margin:2px 0}
li.failed{color:#e5484d;font-weight:600}
li.skipped{opacity:.5}
img.shot{max-width:520px;border-radius:8px;border:1px solid rgba(128,128,128,.3);display:block;margin:8px 0}
code{font-family:ui-monospace,SFMono-Regular,Menlo,monospace;font-size:12px}
h2.cat{font-size:13px;text-transform:uppercase;letter-spacing:.06em;opacity:.6;margin:22px 0 8px;
  padding-bottom:6px;border-bottom:1px solid rgba(128,128,128,.25);display:flex;gap:8px;align-items:baseline}
h2.cat .n{font-weight:400;letter-spacing:0;text-transform:none;margin-left:auto;font-size:12px}
h2.cat.bad{color:#e5484d;opacity:1}
.proof{border:1px solid rgba(128,128,128,.3);border-radius:8px;margin:10px 0;overflow:hidden}
.proof>.h{padding:8px 12px;font-weight:600;display:flex;gap:8px;align-items:center;background:rgba(128,128,128,.08)}
.proof .verdict{margin-left:auto;font-weight:400;font-size:12px;opacity:.75}
.proof.ok>.h{border-left:4px solid #12a150}.proof.bad>.h{border-left:4px solid #e5484d}
table.props{border-collapse:collapse;width:100%;font-size:12px}
table.props td,table.props th{padding:4px 12px;text-align:left;border-top:1px solid rgba(128,128,128,.18)}
table.props th{opacity:.6;font-weight:500}
table.props td.k{font-family:ui-monospace,SFMono-Regular,Menlo,monospace}
tr.miss td{background:rgba(229,72,77,.1)}
tr.miss td.k{color:#e5484d;font-weight:600}
.sinks{padding:6px 12px;font-size:12px;display:flex;gap:6px;flex-wrap:wrap}
.sink{padding:2px 8px;border-radius:999px;background:#12a150;color:#fff}
.sink.no{background:#8b8f98}
</style></head><body>");

            html.AppendLine($"<h1>{Html(report.Suite)}</h1>");
            html.AppendLine($"<div class=\"meta\">{Html(report.StartedAtUtc)} · Unity {Html(report.UnityVersion)} · " +
                            $"{Html(report.Platform)} · seed {report.Seed} · {report.DurationSeconds:0.0}s</div>");

            html.AppendLine("<div class=\"totals\">");
            html.AppendLine($"<span class=\"pill {(report.Success ? "ok" : "bad")}\">{(report.Success ? "PASSED" : "FAILED")}</span>");
            html.AppendLine($"<span class=\"pill\">{report.Passed} passed</span>");
            if (report.Failed > 0) html.AppendLine($"<span class=\"pill bad\">{report.Failed} failed</span>");
            if (report.Errored > 0) html.AppendLine($"<span class=\"pill bad\">{report.Errored} errored</span>");
            if (report.TimedOut > 0) html.AppendLine($"<span class=\"pill bad\">{report.TimedOut} timed out</span>");
            if (report.Skipped > 0) html.AppendLine($"<span class=\"pill warn\">{report.Skipped} skipped</span>");
            html.AppendLine("</div>");

            var groups = GroupByCategory(report.Tests);

            foreach (var group in groups)
            {
                // One flat list stays flat: a heading per category only earns its space once the suite
                // actually has more than one.
                if (groups.Count > 1)
                {
                    int failed = 0;
                    foreach (var test in group.Value) if (test.IsFailure) failed++;

                    html.AppendLine($"<h2 class=\"cat{(failed > 0 ? " bad" : "")}\">{Html(TestCategory.Display(group.Key))}" +
                                    $"<span class=\"n\">{group.Value.Count} test{(group.Value.Count == 1 ? "" : "s")}" +
                                    $"{(failed > 0 ? $" · {failed} failing" : "")}</span></h2>");
                }

                foreach (var test in group.Value) AppendTest(html, test);
            }

            html.AppendLine("</body></html>");
            return html.ToString();
        }

        private static void AppendTest(StringBuilder html, TestRecord test)
        {
            var status = test.Status.ToString().ToLowerInvariant();
            html.AppendLine($"<details class=\"test\" data-status=\"{status}\"{(test.IsFailure ? " open" : "")}>");
            html.AppendLine($"<summary>{Html(test.Name)}<span class=\"dur\">{status} · {test.DurationSeconds:0.00}s" +
                            $"{(test.Attempt > 1 ? $" · attempt {test.Attempt}" : "")}</span></summary>");
            html.AppendLine("<div class=\"body\">");

            if (!string.IsNullOrEmpty(test.Description))
                html.AppendLine($"<p>{Html(test.Description)}</p>");

            if (!string.IsNullOrEmpty(test.Message))
                html.AppendLine($"<div class=\"msg\">{Html(test.Message)}</div>");

            foreach (var proof in test.EventProofs) AppendProof(html, proof);

            html.AppendLine("<ol class=\"steps\">");
            AppendSteps(html, test.Steps);
            html.AppendLine("</ol>");

            foreach (var artifact in test.Artifacts)
                html.AppendLine($"<img class=\"shot\" src=\"{Html(RelativeArtifact(artifact))}\" alt=\"failure screenshot\">");

            if (test.Performance != null)
                html.AppendLine($"<p><code>avg {test.Performance.AverageFps:0} fps · worst frame " +
                                $"{test.Performance.WorstFrameMs:0.0} ms · p95 {test.Performance.Percentile95FrameMs:0.0} ms</code></p>");

            html.AppendLine("</div></details>");
        }

        /// <summary>
        /// One event proof: the payload that actually went, per-property verdicts, and which sinks took
        /// it.
        /// </summary>
        /// <remarks>
        /// The whole point of a proof case is that a reviewer can read what the wire carried without
        /// running anything, so the payload is rendered in full — not only the properties that failed.
        /// A property nobody asserted is exactly where an unnoticed regression lives.
        /// </remarks>
        private static void AppendProof(StringBuilder html, EventProofCase proof)
        {
            var title = string.IsNullOrEmpty(proof.Variant) ? proof.Subject : $"{proof.Subject} · {proof.Variant}";

            html.AppendLine($"<div class=\"proof {(proof.Passed ? "ok" : "bad")}\">");
            html.AppendLine($"<div class=\"h\">{Html(title)}" +
                            $"<span class=\"verdict\">{proof.Verdict}</span></div>");

            if (proof.Proof == null)
            {
                html.AppendLine("<div class=\"sinks\">the event never fired, so there is no payload to show</div>");
                AppendProofFailures(html, proof);
                html.AppendLine("</div>");
                return;
            }

            // Verdicts are keyed by property so the payload can be shown whole, with the checked ones
            // marked in place rather than listed separately.
            var verdicts = new Dictionary<string, EventPropertyMatch>(StringComparer.OrdinalIgnoreCase);
            foreach (var expectation in proof.Expectations)
                foreach (var match in expectation.Properties)
                    verdicts[match.Key] = match;

            html.AppendLine("<table class=\"props\"><tr><th>property</th><th>value</th><th>expected</th></tr>");

            foreach (var pair in proof.Proof.Properties)
            {
                verdicts.TryGetValue(pair.Key, out var verdict);
                bool bad = verdict != null && !verdict.Passed;

                html.AppendLine($"<tr class=\"{(bad ? "miss" : "")}\">" +
                                $"<td class=\"k\">{Html(pair.Key)}</td>" +
                                $"<td>{Html(EventPropertyMatcher.Describe(pair.Value))}</td>" +
                                $"<td>{Html(verdict?.Expected ?? "")}{(bad ? $" — {Html(verdict.Reason)}" : "")}</td></tr>");
            }

            // A property that was expected and never arrived has no row of its own above.
            foreach (var pair in verdicts)
            {
                if (proof.Proof.Properties.ContainsKey(pair.Key)) continue;

                html.AppendLine($"<tr class=\"{(pair.Value.Passed ? "" : "miss")}\">" +
                                $"<td class=\"k\">{Html(pair.Key)}</td><td>(absent)</td>" +
                                $"<td>{Html(pair.Value.Expected)}{(pair.Value.Passed ? "" : $" — {Html(pair.Value.Reason)}")}</td></tr>");
            }

            html.AppendLine("</table>");

            if (proof.Proof.Deliveries.Count > 0)
            {
                html.AppendLine("<div class=\"sinks\">");
                foreach (var delivery in proof.Proof.Deliveries)
                    html.AppendLine($"<span class=\"sink{(delivery.Delivered ? "" : " no")}\" " +
                                    $"title=\"{Html(delivery.Reason)}\">{Html(delivery.Sink)}</span>");
                html.AppendLine("</div>");
            }

            AppendProofFailures(html, proof);

            if (!string.IsNullOrEmpty(proof.ProofScreenshot))
                html.AppendLine($"<img class=\"shot\" src=\"{Html(proof.ProofScreenshot)}\" alt=\"the moment the event fired\">");

            html.AppendLine("</div>");
        }

        private static void AppendProofFailures(StringBuilder html, EventProofCase proof)
        {
            foreach (var expectation in proof.Expectations)
            {
                if (expectation.Passed || string.IsNullOrEmpty(expectation.Reason)) continue;

                html.AppendLine($"<div class=\"msg\">{Html(expectation.Kind)}: {Html(expectation.Reason)}</div>");
            }
        }

        private static void AppendSteps(StringBuilder html, List<StepRecord> steps)
        {
            foreach (var step in steps)
            {
                var css = step.IsFailure ? "failed" : step.Status == StepStatus.Skipped ? "skipped" : "";
                html.Append($"<li class=\"{css}\">{Html(step.Description)} " +
                            $"<span class=\"dur\">{step.DurationSeconds:0.00}s</span>");

                if (!string.IsNullOrEmpty(step.Message))
                    html.Append($"<div class=\"msg\">{Html(step.Message)}</div>");

                foreach (var artifact in step.Artifacts)
                    html.Append($"<img class=\"shot\" src=\"{Html(RelativeArtifact(artifact))}\" alt=\"step screenshot\">");

                if (step.Children.Count > 0)
                {
                    html.Append("<ol class=\"steps\">");
                    AppendSteps(html, step.Children);
                    html.Append("</ol>");
                }

                html.AppendLine("</li>");
            }
        }

        /// <summary>Screenshots live one folder below the report, so link them relatively.</summary>
        private static string RelativeArtifact(string absolute)
        {
            if (string.IsNullOrEmpty(absolute)) return "";
            var normalized = absolute.Replace('\\', '/');
            int index = normalized.LastIndexOf('/');
            if (index < 0) return normalized;
            int parent = normalized.LastIndexOf('/', index - 1);
            return parent < 0 ? normalized.Substring(index + 1) : normalized.Substring(parent + 1);
        }

        private static string Number(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                        .Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        private static string Html(string value) => Escape(value);
    }
}
