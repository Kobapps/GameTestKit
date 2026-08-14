using System;
using System.IO;
using Kobapps.GameTestKit.Scripting;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// Reads a <c>results.json</c> back into a <see cref="RunReport"/>. The Editor uses this to show the
    /// results of a run that happened in play mode — the objects themselves do not survive the domain
    /// reload, but the file does.
    /// </summary>
    public static class ReportReader
    {
        public static RunReport FromFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try { return FromJson(JsonValue.Parse(File.ReadAllText(path))); }
            catch (Exception) { return null; }
        }

        public static RunReport FromJson(JsonValue root)
        {
            if (root == null || !root.IsObject) return null;

            var report = new RunReport
            {
                Suite = root["suite"].AsString("GameTestKit"),
                StartedAtUtc = root["startedAt"].AsString(""),
                FinishedAtUtc = root["finishedAt"].AsString(""),
                DurationSeconds = root["durationSeconds"].AsNumber(),
                UnityVersion = root["unityVersion"].AsString(""),
                Platform = root["platform"].AsString(""),
                Device = root["device"].AsString(""),
                Seed = root["seed"].AsInt(),
            };

            foreach (var item in root["tests"])
            {
                var test = new TestRecord
                {
                    Name = item["name"].AsString(""),
                    Description = item["description"].AsString(null),
                    SourcePath = item["source"].AsString(null),
                    Category = item["category"].AsString("") ?? "",
                    Status = ParseTestStatus(item["status"].AsString("")),
                    Message = item["message"].AsString(null),
                    StackTrace = item["stackTrace"].AsString(null),
                    DurationSeconds = item["durationSeconds"].AsNumber(),
                    Attempt = item["attempt"].AsInt(1),
                };

                foreach (var tag in item["tags"].AsStringList()) test.Tags.Add(tag);
                foreach (var artifact in item["artifacts"].AsStringList()) test.Artifacts.Add(artifact);

                ReadSteps(item["steps"], test.Steps);
                report.Tests.Add(test);
            }

            return report;
        }

        private static void ReadSteps(JsonValue array, System.Collections.Generic.List<StepRecord> target)
        {
            if (!array.IsArray) return;

            foreach (var item in array)
            {
                var step = new StepRecord
                {
                    Index = item["index"].AsInt(),
                    Phase = item["phase"].AsString("steps"),
                    Description = item["description"].AsString(""),
                    Status = ParseStepStatus(item["status"].AsString("")),
                    Message = item["message"].AsString(null),
                    DurationSeconds = item["durationSeconds"].AsNumber(),
                };

                foreach (var artifact in item["artifacts"].AsStringList()) step.Artifacts.Add(artifact);
                ReadSteps(item["steps"], step.Children);

                target.Add(step);
            }
        }

        private static TestStatus ParseTestStatus(string value) =>
            Enum.TryParse(value, true, out TestStatus status) ? status : TestStatus.NotRun;

        private static StepStatus ParseStepStatus(string value) =>
            Enum.TryParse(value, true, out StepStatus status) ? status : StepStatus.Passed;
    }
}
