using System.Collections.Generic;
using Kobapps.GameTestKit.Scripting;
using UnityEditor;
using UnityEngine;

namespace Kobapps.GameTestKit.Editor
{
    /// <summary>
    /// Turn a bot loose on the game and watch, without writing a test.
    /// </summary>
    /// <remarks>
    /// The scripted path (<c>{"runBot": …}</c>) answers "can this persona complete this flow". This one
    /// answers the other question — "what happens if I just let it play" — which is how soak bugs get
    /// found: the fifteenth popup, the level that never ends, the button that stops responding after a
    /// scene reload. It runs as a one-test throwaway script through the normal runner, so a bot launched
    /// here gets the same input backend, the same artefacts and the same report as one launched from CI.
    /// </remarks>
    public sealed class BotRunnerWindow : EditorWindow
    {
        private const string TEMP_SCRIPT = "Library/GameTestKit/bot-session.gametest.json";

        private int _botIndex;
        private string _goal = "";
        private string _failIf = "";
        private float _seconds = 120f;
        private int _actions;
        private int _stuckAfter = 12;
        private string[] _names = System.Array.Empty<string>();
        private List<GameBot> _bots = new List<GameBot>();
        private Vector2 _scroll;

        [MenuItem("Tools/GameTestKit/Bot Runner", priority = 11)]
        public static void Open()
        {
            var window = GetWindow<BotRunnerWindow>("Bot Runner");
            window.minSize = new Vector2(380f, 300f);
            window.Refresh();
            window.Show();
        }

        private void OnFocus() => Refresh();

        private void Refresh()
        {
            _bots = BotRegistry.All();
            _names = new string[_bots.Count];
            for (int i = 0; i < _bots.Count; i++)
                _names[i] = string.IsNullOrEmpty(_bots[i].Persona)
                    ? _bots[i].BotName
                    : $"{_bots[i].BotName} — {Summarise(_bots[i].Persona)}";
            _botIndex = Mathf.Clamp(_botIndex, 0, Mathf.Max(0, _bots.Count - 1));
        }

        private static string Summarise(string persona)
        {
            var line = persona.Replace('\n', ' ').Trim();
            return line.Length <= 60 ? line : line.Substring(0, 57) + "…";
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            if (_bots.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    $"No GameBot assets found.\n\nCreate one (a ScriptableObject deriving from GameBot) and " +
                    $"put it in Resources/{BotRegistry.ResourcesFolder}/ so it ships to players too.",
                    MessageType.Info);
                if (GUILayout.Button("Refresh")) Refresh();
                EditorGUILayout.EndScrollView();
                return;
            }

            _botIndex = EditorGUILayout.Popup("Bot", _botIndex, _names);
            var bot = _bots[_botIndex];

            if (!string.IsNullOrEmpty(bot.Persona))
                EditorGUILayout.HelpBox(bot.Persona, MessageType.None);

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Budget", EditorStyles.boldLabel);
            _seconds = EditorGUILayout.FloatField("Seconds", _seconds);
            _actions = EditorGUILayout.IntField(
                new GUIContent("Actions", "0 uses the bot's own limit."), _actions);
            _stuckAfter = EditorGUILayout.IntField(
                new GUIContent("Stuck after", "Barren actions before calling it a dead end."), _stuckAfter);

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Conditions (optional)", EditorStyles.boldLabel);
            _goal = EditorGUILayout.TextField(
                new GUIContent("Until", "Expression that ends the run successfully, e.g. ms.wins >= 3"), _goal);
            _failIf = EditorGUILayout.TextField(
                new GUIContent("Fail if", "Expression that ends the run as a finding."), _failIf);

            EditorGUILayout.Space(8f);

            using (new EditorGUI.DisabledScope(EditorApplication.isPlaying || EditorApplication.isCompiling))
            {
                if (GUILayout.Button($"Let {bot.BotName} play", GUILayout.Height(28f)))
                    Launch(bot);
            }

            if (EditorApplication.isPlaying)
                EditorGUILayout.HelpBox("Exit play mode first — the runner starts its own session.",
                    MessageType.Info);

            EditorGUILayout.Space(4f);
            if (GUILayout.Button("Open Live Run window")) GameTestLiveWindow.Open();
            if (GUILayout.Button("Refresh bot list")) Refresh();

            EditorGUILayout.HelpBox(
                "The bot plays with real pointer input and can only touch what a player could. " +
                "Findings — logged errors, dead ends, unreachable elements — land in the run folder as " +
                "bot-<name>.json with the full action trail.", MessageType.None);

            EditorGUILayout.EndScrollView();
        }

        /// <summary>Writes a one-step script for this bot and runs it through the normal runner.</summary>
        private void Launch(GameBot bot)
        {
            var step = JsonValue.NewObject()
                .Set("runBot", bot.BotName)
                .Set("seconds", _seconds)
                .Set("actions", _actions)
                .Set("stuckAfter", _stuckAfter)
                // Never fail a free-play session: the point is the report, and a red result for "the bot
                // wandered somewhere unexpected" is exactly the signal people learn to ignore.
                .Set("expect", "explore");

            if (!string.IsNullOrWhiteSpace(_goal)) step.Set("until", _goal);
            if (!string.IsNullOrWhiteSpace(_failIf)) step.Set("failIf", _failIf);

            var steps = JsonValue.NewArray();
            steps.Add(step);

            var script = JsonValue.NewObject()
                .Set("name", $"{bot.BotName} free play")
                .Set("description", "Launched from the Bot Runner window.")
                .Set("timeout", _seconds + 60f);
            script["tags"] = JsonValue.NewArray();
            script["steps"] = steps;

            try
            {
                var path = System.IO.Path.Combine(
                    System.IO.Directory.GetParent(Application.dataPath)?.FullName ?? ".", TEMP_SCRIPT);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.IO.File.WriteAllText(path, script.ToJson());

                var options = GameTesterSettings.Instance.CreateRunOptions();
                options.Paths.Add(path);
                options.FailOnLogError = false;   // the bot reports errors as findings; they are the output

                if (!EditorTestRunner.Run(options, true, out var problem))
                    EditorUtility.DisplayDialog("Bot Runner", problem, "OK");
                else
                    GameTestLiveWindow.Open();
            }
            catch (System.Exception e)
            {
                EditorUtility.DisplayDialog("Bot Runner", $"Could not start the bot: {e.Message}", "OK");
            }
        }
    }
}
