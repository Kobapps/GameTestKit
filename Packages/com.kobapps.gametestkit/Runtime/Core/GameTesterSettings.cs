using System.Collections.Generic;
using UnityEngine;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// Project-wide defaults. Lives at <c>Assets/Resources/GameTesterSettings.asset</c> so it is
    /// available in built players too. Created on demand from the Editor
    /// (<c>Tools ▸ GameTestKit ▸ Settings</c>); the defaults below are used when it is absent.
    /// </summary>
    public sealed class GameTesterSettings : ScriptableObject
    {
        public const string ResourcePath = "GameTesterSettings";

        [Header("Discovery")]
        [Tooltip("Folders (project-relative) scanned in the Editor for *.gametest.json and *.gamesuite.json.")]
        public List<string> TestFolders = new List<string> { "Assets/GameTests" };

        [Tooltip("Resources sub-folder scanned at runtime, so tests also exist in built players.")]
        public string RuntimeResourcesFolder = "GameTests";

        [Tooltip("Also scan StreamingAssets/<folder> at runtime. Lets QA drop scripts next to a build.")]
        public string StreamingAssetsFolder = "GameTests";

        [Header("Run defaults")]
        public float DefaultStepTimeout = 15f;
        public float LocatorTimeout = 5f;
        public float DefaultTestTimeout = 120f;
        public InputBackendKind Backend = InputBackendKind.Auto;
        public PointerMode Pointer = PointerMode.Mouse;

        [Tooltip("Multiplies gesture durations. 1 = human-ish speed; 0.2 = fast; 3 = slow enough to watch.")]
        public float InputSpeedScale = 1f;

        [Tooltip("Disable real hardware input devices during a run so stray human input can't corrupt it.")]
        public bool IsolateRealDevices;

        [Tooltip("Draw yellow markers where the virtual user interacts, plus a caption strip with the "
                 + "current step and the text being typed. Toggle it during a run with F9.")]
        public bool ShowInputOverlay = true;

        [Header("Failure policy")]
        public bool FailOnLogError = true;
        public List<string> IgnoredLogPatterns = new List<string>();
        public bool ScreenshotOnFailure = true;
        public bool CollectPerformance = true;

        [Header("Artifacts")]
        [Tooltip("Absolute or project-relative output folder. Empty = <project>/GameTestKit.")]
        public string ArtifactRoot = "";

        public List<string> ReportFormats = new List<string> { "junit", "json", "html" };

        private static GameTesterSettings _instance;

        /// <summary>Loaded settings, or a transient default instance when none exists.</summary>
        public static GameTesterSettings Instance
        {
            get
            {
                if (_instance != null) return _instance;
                _instance = Resources.Load<GameTesterSettings>(ResourcePath);
                if (_instance == null)
                {
                    _instance = CreateInstance<GameTesterSettings>();
                    _instance.name = "GameTesterSettings (defaults)";
                    _instance.hideFlags = HideFlags.HideAndDontSave;
                }
                return _instance;
            }
            set => _instance = value;
        }

        /// <summary>A fresh <see cref="RunOptions"/> pre-filled from these settings.</summary>
        public RunOptions CreateRunOptions()
        {
            return new RunOptions
            {
                DefaultStepTimeout = DefaultStepTimeout,
                LocatorTimeout = LocatorTimeout,
                Backend = Backend,
                Pointer = Pointer,
                InputSpeedScale = InputSpeedScale,
                IsolateRealDevices = IsolateRealDevices,
                ShowInputOverlay = ShowInputOverlay,
                FailOnLogError = FailOnLogError,
                IgnoredLogPatterns = new List<string>(IgnoredLogPatterns),
                ScreenshotOnFailure = ScreenshotOnFailure,
                CollectPerformance = CollectPerformance,
                ArtifactRoot = ArtifactRoot,
                ReportFormats = new List<string>(ReportFormats),
            };
        }
    }
}
