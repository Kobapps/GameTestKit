using System;
using System.Collections.Generic;

namespace Kobapps.GameTestKit
{
    public enum TestStatus
    {
        NotRun = 0,
        Passed = 1,
        Failed = 2,
        /// <summary>Unexpected exception, broken script, or setup failure — distinct from an assertion failure.</summary>
        Error = 3,
        Skipped = 4,
        Timeout = 5,
    }

    public enum StepStatus
    {
        Passed = 0,
        Failed = 1,
        Error = 2,
        Timeout = 3,
        Skipped = 4,
    }

    /// <summary>One captured Unity log line, kept alongside the step that was running when it appeared.</summary>
    public sealed class LogEntry
    {
        public string Message;
        public string StackTrace;
        public string Type;      // Log, Warning, Error, Exception, Assert
        public float TimeStamp;  // seconds since the run started
        public int StepIndex = -1;
    }

    public sealed class StepRecord
    {
        public int Index;
        public string Description;
        public string Phase = "steps";   // setup | steps | teardown
        public StepStatus Status = StepStatus.Passed;
        public string Message;
        public string StackTrace;
        public double DurationSeconds;
        public readonly List<string> Artifacts = new List<string>();
        public readonly List<StepRecord> Children = new List<StepRecord>();

        public bool IsFailure => Status == StepStatus.Failed || Status == StepStatus.Error || Status == StepStatus.Timeout;
    }

    public sealed class TestRecord
    {
        public string Name;
        public string Description;
        public string SourcePath;
        public readonly List<string> Tags = new List<string>();
        public TestStatus Status = TestStatus.NotRun;
        public string Message;
        public string StackTrace;
        public double DurationSeconds;
        public int Attempt = 1;              // 1 = first try; >1 means it was retried
        public string StartedAtUtc;
        public readonly List<StepRecord> Steps = new List<StepRecord>();
        public readonly List<string> Artifacts = new List<string>();
        public readonly List<LogEntry> Logs = new List<LogEntry>();
        public PerformanceSummary Performance;

        public bool IsFailure => Status == TestStatus.Failed || Status == TestStatus.Error || Status == TestStatus.Timeout;
    }

    /// <summary>Frame-time / memory statistics sampled while a test ran. Cheap enough to always collect.</summary>
    public sealed class PerformanceSummary
    {
        public int Frames;
        public double AverageFps;
        public double MinFps;
        public double WorstFrameMs;
        public double Percentile95FrameMs;
        public long PeakManagedMemoryBytes;
    }

    /// <summary>The result of one batch run: everything a report needs.</summary>
    public sealed class RunReport
    {
        public string Suite = "GameTestKit";
        public string StartedAtUtc;
        public string FinishedAtUtc;
        public double DurationSeconds;
        public string UnityVersion;
        public string Platform;
        public string Device;
        public int Seed;
        public readonly List<TestRecord> Tests = new List<TestRecord>();

        public int Total => Tests.Count;
        public int Passed => Count(TestStatus.Passed);
        public int Failed => Count(TestStatus.Failed);
        public int Errored => Count(TestStatus.Error);
        public int Skipped => Count(TestStatus.Skipped);
        public int TimedOut => Count(TestStatus.Timeout);
        public bool Success => Failed == 0 && Errored == 0 && TimedOut == 0;

        private int Count(TestStatus status)
        {
            int n = 0;
            for (int i = 0; i < Tests.Count; i++)
                if (Tests[i].Status == status) n++;
            return n;
        }

        public string Summary() =>
            $"{Passed}/{Total} passed, {Failed} failed, {Errored} errored, {TimedOut} timed out, {Skipped} skipped ({DurationSeconds:0.0}s)";
    }
}
