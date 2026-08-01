using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// Watches the Unity log while a test runs. An unhandled exception or an error the game logged is
    /// a defect even when every assertion passed, so by default it fails the running test.
    /// </summary>
    public sealed class LogCapture : IDisposable
    {
        private readonly List<LogEntry> _entries = new List<LogEntry>();
        private readonly List<Regex> _ignored = new List<Regex>();
        private readonly float _startTime;
        private bool _attached;

        public IReadOnlyList<LogEntry> Entries => _entries;

        /// <summary>Index of the step currently running, stamped onto captured lines.</summary>
        public int CurrentStepIndex { get; set; } = -1;

        /// <summary>The first non-ignored error/exception since the last <see cref="Reset"/>, if any.</summary>
        public LogEntry FirstError { get; private set; }

        public LogCapture(IEnumerable<string> ignoredPatterns)
        {
            _startTime = Time.realtimeSinceStartup;
            if (ignoredPatterns == null) return;

            foreach (var pattern in ignoredPatterns)
            {
                if (string.IsNullOrWhiteSpace(pattern)) continue;
                try { _ignored.Add(new Regex(pattern, RegexOptions.CultureInvariant)); }
                catch (ArgumentException e)
                {
                    Debug.LogWarning($"[GameTestKit] Ignored log pattern '{pattern}' is not a valid regex: {e.Message}");
                }
            }
        }

        public void Attach()
        {
            if (_attached) return;
            Application.logMessageReceived += OnLog;
            _attached = true;
        }

        public void Detach()
        {
            if (!_attached) return;
            Application.logMessageReceived -= OnLog;
            _attached = false;
        }

        public void Dispose() => Detach();

        public void Reset()
        {
            _entries.Clear();
            FirstError = null;
        }

        /// <summary>Removes captured entries, returning what was collected (for the test record).</summary>
        public List<LogEntry> Drain()
        {
            var copy = new List<LogEntry>(_entries);
            _entries.Clear();
            FirstError = null;
            return copy;
        }

        /// <summary>
        /// Drops the pending error without dropping the captured log. Called once an error has been
        /// turned into a step failure, so it does not also fail every step after it.
        /// </summary>
        public void ClearPendingError() => FirstError = null;

        /// <summary>
        /// Marks errors matching <paramref name="pattern"/> as expected: clears the pending failure and
        /// suppresses future matches for the rest of the test. Used by the <c>expectLog</c> step so a
        /// test can deliberately exercise an error path.
        /// </summary>
        public void ForgiveError(Regex pattern)
        {
            if (pattern == null) return;
            _ignored.Add(pattern);
            if (FirstError != null && pattern.IsMatch(FirstError.Message ?? string.Empty))
                FirstError = null;
        }

        private void OnLog(string message, string stackTrace, LogType type)
        {
            var entry = new LogEntry
            {
                Message = message,
                StackTrace = stackTrace,
                Type = type.ToString(),
                TimeStamp = Time.realtimeSinceStartup - _startTime,
                StepIndex = CurrentStepIndex,
            };
            _entries.Add(entry);

            if (FirstError != null) return;
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            if (IsIgnored(message)) return;

            FirstError = entry;
        }

        private bool IsIgnored(string message)
        {
            for (int i = 0; i < _ignored.Count; i++)
                if (_ignored[i].IsMatch(message ?? string.Empty))
                    return true;
            return false;
        }
    }

    /// <summary>Frame-time and memory statistics for one test. Cheap: one sample per frame.</summary>
    public sealed class PerformanceSampler
    {
        private readonly List<float> _frameMs = new List<float>();
        private long _peakMemory;

        public void Reset()
        {
            _frameMs.Clear();
            _peakMemory = 0;
        }

        public void Tick()
        {
            _frameMs.Add(Time.unscaledDeltaTime * 1000f);
            var memory = GC.GetTotalMemory(false);
            if (memory > _peakMemory) _peakMemory = memory;
        }

        public PerformanceSummary Summarize()
        {
            if (_frameMs.Count == 0) return null;

            var sorted = new List<float>(_frameMs);
            sorted.Sort();

            double total = 0;
            double worst = 0;
            for (int i = 0; i < _frameMs.Count; i++)
            {
                total += _frameMs[i];
                if (_frameMs[i] > worst) worst = _frameMs[i];
            }

            double average = total / _frameMs.Count;
            int p95Index = Mathf.Clamp(Mathf.CeilToInt(sorted.Count * 0.95f) - 1, 0, sorted.Count - 1);

            return new PerformanceSummary
            {
                Frames = _frameMs.Count,
                AverageFps = average > 0 ? 1000.0 / average : 0,
                MinFps = worst > 0 ? 1000.0 / worst : 0,
                WorstFrameMs = worst,
                Percentile95FrameMs = sorted[p95Index],
                PeakManagedMemoryBytes = _peakMemory,
            };
        }
    }

    /// <summary>
    /// Writes screenshots and reports under one run folder, so a failure comes with the picture of
    /// what the screen looked like when it happened.
    /// </summary>
    public sealed class ArtifactStore
    {
        public string Root { get; }
        public string RunFolder { get; }

        private string _testFolder;
        private int _counter;

        public ArtifactStore(string root, string runName)
        {
            Root = string.IsNullOrEmpty(root) ? DefaultRoot() : Absolute(root);
            RunFolder = Path.Combine(Root, Sanitize(runName));
            Directory.CreateDirectory(RunFolder);
            _testFolder = RunFolder;
        }

        public static string DefaultRoot()
        {
            // <project>/GameTestKit in the Editor; the persistent data path in a player.
            if (Application.isEditor)
            {
                var projectRoot = Directory.GetParent(Application.dataPath);
                if (projectRoot != null) return Path.Combine(projectRoot.FullName, "GameTestKit");
            }
            return Path.Combine(Application.persistentDataPath, "GameTestKit");
        }

        private static string Absolute(string path) =>
            Path.IsPathRooted(path)
                ? path
                : Path.GetFullPath(Path.Combine(Directory.GetParent(Application.dataPath)?.FullName ?? ".", path));

        public void BeginTest(string testName)
        {
            _testFolder = Path.Combine(RunFolder, Sanitize(testName));
            Directory.CreateDirectory(_testFolder);
            _counter = 0;
        }

        public string PathFor(string fileName) => Path.Combine(_testFolder, Sanitize(fileName));

        private static bool _warnedAboutCapture;

        /// <summary>
        /// False when there is no presented frame to capture.
        /// </summary>
        /// <remarks>
        /// Batch mode is the important case: it never reaches end-of-frame, so a
        /// <c>WaitForEndOfFrame</c> there does not return late — it never returns at all, and would
        /// hang the whole run on the first failure screenshot. Skipping is the only correct behaviour;
        /// a screenshot is worth less than a run that finishes.
        /// </remarks>
        public static bool CanCapture
        {
            get
            {
                bool possible = !Application.isBatchMode &&
                                SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;

                if (!possible && !_warnedAboutCapture)
                {
                    _warnedAboutCapture = true;
                    Debug.Log("[GameTestKit] Screenshots are disabled: batch mode and -nographics have no " +
                              "presented frame to capture. Reports and step timings are unaffected.");
                }

                return possible;
            }
        }

        /// <summary>
        /// Captures the game view. Yields until the end of frame so the shot contains what the player
        /// would have seen, and returns the file path through <paramref name="onSaved"/>.
        /// </summary>
        public IEnumerator Screenshot(string name, Action<string> onSaved)
        {
            if (!CanCapture)
            {
                onSaved?.Invoke(null);
                yield break;
            }

            yield return new WaitForEndOfFrame();

            Texture2D texture = null;
            string path = null;
            try
            {
                texture = ScreenCapture.CaptureScreenshotAsTexture();
                var bytes = texture.EncodeToPNG();
                path = PathFor($"{++_counter:00}-{name}.png");
                File.WriteAllBytes(path, bytes);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GameTestKit] Screenshot '{name}' failed: {e.Message}");
                path = null;
            }
            finally
            {
                if (texture != null) UnityEngine.Object.Destroy(texture);
            }

            onSaved?.Invoke(path);
        }

        public static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed";
            var invalid = Path.GetInvalidFileNameChars();
            var builder = new System.Text.StringBuilder(name.Length);
            foreach (var c in name)
                builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return builder.ToString().Trim();
        }
    }
}
