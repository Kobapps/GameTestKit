using System;
using System.Collections.Generic;
using System.IO;
using Kobapps.GameTestKit.Scripting;
using UnityEditor;
using UnityEngine;

namespace Kobapps.GameTestKit.Editor
{
    /// <summary>
    /// A live view of the run in progress: which test, which step, and whether it is still moving.
    /// </summary>
    /// <remarks>
    /// A run lives inside play mode, so while it is going the Editor's own UI is the only place anyone can
    /// watch it — and the thing people actually need to know is not the final tally but <em>is it stuck</em>.
    /// This reads the same <see cref="LiveStatus"/> heartbeat the agent API polls, so the window and an
    /// automated caller always agree about what is happening, and shows the two derived facts that matter:
    /// how long the current step has been running, and how long since anything moved at all.
    /// <para>
    /// It repaints off <see cref="EditorApplication.update"/> rather than only on inspector events, because
    /// a wedged run produces no events at all — the absence of change is the signal.
    /// </para>
    /// </remarks>
    public sealed class GameTestLiveWindow : EditorWindow
    {
        private const float REPAINT_INTERVAL_SECONDS = 0.25f;
        private const float STALE_AFTER_SECONDS = 30f;

        private static readonly Color Running = new Color(0.35f, 0.65f, 1f);
        private static readonly Color Passed = new Color(0.35f, 0.8f, 0.45f);
        private static readonly Color Failed = new Color(0.95f, 0.4f, 0.35f);
        private static readonly Color Stale = new Color(0.95f, 0.75f, 0.25f);
        private static readonly Color Idle = new Color(0.6f, 0.6f, 0.6f);

        private double _nextRepaint;
        private Vector2 _scroll;
        private string _rawJson;
        private DateTime _rawStamp;
        private JsonValue _status;

        /// <summary>The last step seen, so a finished run still shows what it was doing when it ended.</summary>
        private readonly List<string> _timeline = new List<string>();

        private string _timelineRun;
        private int _timelineLastStep = -1;

        [MenuItem("Tools/GameTestKit/Live Run", priority = 10)]
        public static void Open()
        {
            var window = GetWindow<GameTestLiveWindow>("Live Run");
            window.minSize = new Vector2(360f, 260f);
            window.Show();
        }

        private void OnEnable() => EditorApplication.update += Tick;

        private void OnDisable() => EditorApplication.update -= Tick;

        private void Tick()
        {
            if (EditorApplication.timeSinceStartup < _nextRepaint) return;
            _nextRepaint = EditorApplication.timeSinceStartup + REPAINT_INTERVAL_SECONDS;
            Reload();
            Repaint();
        }

        private void Reload()
        {
            try
            {
                var path = LiveStatus.Path;
                if (!File.Exists(path)) { _status = null; return; }

                var text = File.ReadAllText(path);
                if (text == _rawJson) return;

                _rawJson = text;
                _rawStamp = DateTime.UtcNow;
                _status = JsonValue.Parse(text);
                RecordTimeline();
            }
            catch (Exception)
            {
                // A half-written heartbeat is normal — the next tick reads a whole one.
            }
        }

        /// <summary>Appends each step as it starts, so the window shows the path taken, not just the tip.</summary>
        private void RecordTimeline()
        {
            var run = _status["run"].AsString("") + "|" + _status["startedUtc"].AsString("");
            if (run != _timelineRun)
            {
                _timelineRun = run;
                _timeline.Clear();
                _timelineLastStep = -1;
            }

            var index = _status["stepIndex"].AsInt(-1);
            var step = _status["step"].AsString(null);

            if (!string.IsNullOrEmpty(step) && index != _timelineLastStep)
            {
                _timelineLastStep = index;
                _timeline.Add($"{index,3}  {step}");
                if (_timeline.Count > 200) _timeline.RemoveAt(0);
            }

            // Annotate the entry that just concluded, so failures read in place.
            var last = _status["lastStepStatus"].AsString(null);
            if (_timeline.Count > 0 && !string.IsNullOrEmpty(last) &&
                !last.Equals("Passed", StringComparison.OrdinalIgnoreCase) &&
                !_timeline[_timeline.Count - 1].EndsWith("]", StringComparison.Ordinal))
                _timeline[_timeline.Count - 1] += $"   [{last}]";
        }

        private void OnGUI()
        {
            if (_status == null)
            {
                EditorGUILayout.HelpBox(
                    "No run has been started in this project yet.\n\n" +
                    "Start one from Tools ▸ GameTestKit ▸ GameTester, or from an agent with " +
                    "GameTestKitAgent.Start(...).", MessageType.Info);
                DrawToolbar(false);
                return;
            }

            var state = _status["state"].AsString("idle");
            var secondsSinceBeat = SecondsSince(_status["heartbeatUtc"].AsString(null));
            bool stale = state == "running" && secondsSinceBeat > STALE_AFTER_SECONDS;

            DrawBanner(state, stale, secondsSinceBeat);
            DrawProgress();
            DrawCurrentStep(state, stale, secondsSinceBeat);
            DrawMessage();
            DrawTimeline();
            DrawToolbar(state == "running");
        }

        // ---------------------------------------------------------------- sections

        private void DrawBanner(string state, bool stale, double secondsSinceBeat)
        {
            var (label, color) = state switch
            {
                "running" when stale => ($"STUCK? no progress for {secondsSinceBeat:0}s", Stale),
                "running" => ("RUNNING", Running),
                "finished" => (_status["failed"].AsInt(0) > 0 ? "FAILED" : "PASSED",
                    _status["failed"].AsInt(0) > 0 ? Failed : Passed),
                "aborted" => ("ABORTED", Failed),
                _ => ("IDLE", Idle),
            };

            var rect = GUILayoutUtility.GetRect(1f, 26f);
            EditorGUI.DrawRect(rect, color * new Color(1f, 1f, 1f, 0.25f));

            var style = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleCenter };
            style.normal.textColor = color;
            GUI.Label(rect, label, style);
        }

        private void DrawProgress()
        {
            int index = _status["testIndex"].AsInt(0);
            int count = Math.Max(1, _status["testCount"].AsInt(1));
            int passed = _status["passed"].AsInt(0);
            int failed = _status["failed"].AsInt(0);

            var rect = GUILayoutUtility.GetRect(1f, 18f);
            EditorGUI.ProgressBar(rect, Mathf.Clamp01((float)(passed + failed) / count),
                $"{passed + failed}/{count} tests   ✔ {passed}   ✘ {failed}");

            EditorGUILayout.LabelField("Test", $"{index}/{count}  {_status["test"].AsString("—")}");
            EditorGUILayout.LabelField("Scene", _status["scene"].AsString("—"));
            EditorGUILayout.LabelField("Elapsed", $"{_status["elapsedSeconds"].AsFloat(0f):0.0}s");
        }

        private void DrawCurrentStep(string state, bool stale, double secondsSinceBeat)
        {
            EditorGUILayout.Space(4f);

            var step = _status["step"].AsString(null);
            var label = string.IsNullOrEmpty(step)
                ? (state == "running" ? "(between steps)" : "—")
                : step;

            var style = new GUIStyle(EditorStyles.boldLabel) { wordWrap = true };
            if (stale) style.normal.textColor = Stale;

            EditorGUILayout.LabelField($"Step {_status["stepIndex"].AsInt(0)}", style);
            EditorGUILayout.LabelField(label, new GUIStyle(EditorStyles.label) { wordWrap = true });

            if (state == "running")
                EditorGUILayout.LabelField("Last heartbeat", $"{secondsSinceBeat:0.0}s ago");
        }

        private void DrawMessage()
        {
            var message = _status["lastMessage"].AsString(null);
            if (string.IsNullOrEmpty(message)) return;

            var status = _status["lastStepStatus"].AsString("");
            var type = status.Equals("Passed", StringComparison.OrdinalIgnoreCase)
                ? MessageType.Info
                : MessageType.Error;

            EditorGUILayout.Space(4f);
            EditorGUILayout.HelpBox(message, type);
        }

        private void DrawTimeline()
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Steps so far", EditorStyles.boldLabel);

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(90f));
            for (int i = _timeline.Count - 1; i >= 0; i--)
                EditorGUILayout.LabelField(_timeline[i], EditorStyles.miniLabel);
            EditorGUILayout.EndScrollView();
        }

        private void DrawToolbar(bool running)
        {
            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!running))
                    if (GUILayout.Button("Stop Run"))
                        EditorTestRunner.Cancel();

                var folder = _status?["runFolder"].AsString(null);
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(folder)))
                    if (GUILayout.Button("Open Artifacts"))
                        EditorUtility.RevealInFinder(folder);

                if (GUILayout.Button("Report"))
                {
                    var report = string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, "report.html");
                    if (!string.IsNullOrEmpty(report) && File.Exists(report)) UnityEngine.Application.OpenURL(report);
                    else ShowNotification(new GUIContent("No report yet"));
                }
            }

            if (EditorUtility.scriptCompilationFailed)
                EditorGUILayout.HelpBox(
                    "The project has compile errors — Unity will not enter play mode, so a queued run will " +
                    "never start.", MessageType.Warning);
        }

        // ---------------------------------------------------------------- helpers

        private static double SecondsSince(string isoUtc)
        {
            if (string.IsNullOrEmpty(isoUtc)) return 0;
            return DateTime.TryParse(isoUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var when)
                ? Math.Max(0, (DateTime.UtcNow - when.ToUniversalTime()).TotalSeconds)
                : 0;
        }
    }
}
