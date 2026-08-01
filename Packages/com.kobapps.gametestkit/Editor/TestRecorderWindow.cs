using System.IO;
using EditorCoreKit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kobapps.GameTestKit.Editor
{
    /// <summary>
    /// Records a live play session into a test script. Enter play mode, press Record, play the flow,
    /// press Stop — the script lands in your tests folder ready to run and edit.
    /// </summary>
    /// <remarks>
    /// The script is written on Stop, while play mode is still running: exiting play mode triggers a
    /// domain reload that would take the recording with it.
    /// </remarks>
    public sealed class TestRecorderWindow : EditorWindow
    {
        private string _testName = "recorded flow";
        private string _lastSavedPath;

        private KUIWindowShell _shell;
        private KUILogConsole _console;
        private KUIBanner _playModeBanner;
        private Button _recordButton;
        private Button _stopButton;
        private Label _countLabel;
        private VisualElement _savedRow;

        [MenuItem("Tools/GameTestKit/Recorder", priority = 10)]
        public static void Open()
        {
            var window = GetWindow<TestRecorderWindow>();
            window.titleContent = new GUIContent("Recorder");
            window.minSize = new Vector2(520, 380);
            window.Show();
        }

        private void OnDisable() => _shell = null;

        private void CreateGUI()
        {
            _shell = new KUIWindowShell("GameTestKit", "Recorder", withSidebar: false)
                .MountInto(rootVisualElement);

            var page = KUILayout.Page();

            _playModeBanner = new KUIBanner(
                KUITone.Warning,
                "Not in play mode",
                "Recording watches the running game, so press Play first. Anything you do in the game "
                + "view then becomes steps.");
            page.Add(_playModeBanner);

            var setup = new KUICard("Record a flow",
                "Play the flow once and the recorder writes the script — choosing the most stable "
                + "selector available for everything you touch.");

            var nameField = new TextField("Test name") { value = _testName };
            nameField.RegisterValueChangedCallback(evt => _testName = evt.newValue);
            setup.Add(nameField);

            _recordButton = KUIButton.Danger("● Record", StartRecording);
            _stopButton = KUIButton.Primary("■ Stop and save", StopAndSave);

            setup.Add(KUILayout.Row(_recordButton, _stopButton, KUILayout.Spacer()));

            setup.Add(KUIText.Muted(
                "While recording: F8 asserts on whatever is under the pointer, F9 inserts a screenshot "
                + "step. Pauses longer than 0.4s become explicit waits."));

            page.Add(setup);

            _countLabel = KUIText.SectionTitle("Steps so far");
            page.Add(_countLabel);

            _console = new KUILogConsole();
            _console.style.flexGrow = 1;
            _console.style.minHeight = 160;
            page.Add(_console);

            _savedRow = KUILayout.Row();
            _savedRow.style.display = DisplayStyle.None;
            page.Add(_savedRow);

            page.Add(new KUIBanner(
                KUITone.Accent,
                "A recording is a first draft",
                "Replace the recorded waits with waitFor/waitForVisible, and add the assertions that say "
                + "what the flow is supposed to prove. A recording that only clicks proves only that "
                + "nothing threw."));

            _shell.SetContent(page);

            UpdateState();
            rootVisualElement.schedule.Execute(UpdateState).Every(250);
        }

        private void StartRecording()
        {
            _console?.ClearLog();

            var recorder = TestRecorder.Begin(_testName);
            recorder.StepRecorded += OnStepRecorded;

            _savedRow.style.display = DisplayStyle.None;
            UpdateState();
        }

        private void OnStepRecorded(string json)
        {
            _console?.Append(json, KUITone.Neutral, TestRecorder.Active?.StepCount.ToString());
        }

        private void StopAndSave()
        {
            var json = TestRecorder.Stop();

            if (string.IsNullOrEmpty(json))
            {
                KUIToast.Error(rootVisualElement, "Nothing was recorded.");
                UpdateState();
                return;
            }

            var settings = GameTesterSettings.Instance;
            var folder = settings.TestFolders.Count > 0 ? settings.TestFolders[0] : "Assets/GameTests";
            Directory.CreateDirectory(folder);

            var slug = ArtifactStore.Sanitize(_testName.Replace(' ', '-').ToLowerInvariant());
            var path = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(folder, $"{slug}.gametest.json"));

            File.WriteAllText(path, json);
            AssetDatabase.ImportAsset(path);
            _lastSavedPath = path;

            Debug.Log($"[GameTestKit] Recorded script saved to {path}");
            KUIToast.Success(rootVisualElement, "Saved");

            ShowSaved(path);
            UpdateState();
        }

        private void ShowSaved(string path)
        {
            _savedRow.Clear();
            _savedRow.style.display = DisplayStyle.Flex;

            _savedRow.Add(new KUIBadge("SAVED", KUITone.Success));
            _savedRow.Add(KUIText.Link(path, () =>
            {
                var asset = AssetDatabase.LoadAssetAtPath<Object>(path);
                if (asset == null) return;

                Selection.activeObject = asset;
                EditorGUIUtility.PingObject(asset);
                AssetDatabase.OpenAsset(asset);
            }));

            _savedRow.Add(KUILayout.Spacer());
            _savedRow.Add(KUIButton.Secondary("Open GameTester", () => GameTesterWindow.Open()));
        }

        /// <summary>
        /// Keeps the chrome honest about a state this window does not own: recording lives in play
        /// mode and can end without the window being told (the user pressing Stop, a script error).
        /// </summary>
        private void UpdateState()
        {
            if (_shell == null) return;

            var playing = EditorApplication.isPlaying;
            var recorder = TestRecorder.Active;
            var recording = recorder != null && recorder.IsRecording;

            _playModeBanner.style.display = playing ? DisplayStyle.None : DisplayStyle.Flex;

            _recordButton.SetEnabled(playing && !recording);
            _stopButton.SetEnabled(recording);

            if (recording)
            {
                _countLabel.text = $"Recording… {recorder.StepCount} step(s)";
                _shell.Status?.Set("Recording", "● REC", KUITone.Error);
            }
            else
            {
                _countLabel.text = "Steps so far";
                _shell.Status?.Set(
                    playing
                        ? "Ready — press Record and play the flow"
                        : "Enter play mode to record",
                    playing ? KUITone.Success : KUITone.Warning);
            }

            if (!recording && !string.IsNullOrEmpty(_lastSavedPath) &&
                _savedRow.style.display.value == DisplayStyle.None)
                ShowSaved(_lastSavedPath);
        }
    }
}
