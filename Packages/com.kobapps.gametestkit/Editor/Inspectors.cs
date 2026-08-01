using System.Text;
using EditorCoreKit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kobapps.GameTestKit.Editor
{
    /// <summary>
    /// The inspector for a <see cref="TestId"/>: the id, and the selector a test should use to reach
    /// this object — ready to copy, so nobody has to guess the syntax or retype it.
    /// </summary>
    [CustomEditor(typeof(TestId))]
    [CanEditMultipleObjects]
    public sealed class TestIdInspector : UnityEditor.Editor
    {
        public override VisualElement CreateInspectorGUI()
        {
            var root = new VisualElement();

            // Without this every kit class resolves to nothing and the inspector falls back to
            // unstyled defaults, with no error saying why.
            KUITheme.Apply(root);

            var component = (TestId)target;

            var card = new KUICard("Test Id",
                "A stable handle for this object. Names, paths and labels all change; an id survives.");

            KUIProperty.DrawAll(card, serializedObject);

            if (targets.Length == 1)
            {
                var selector = "id:" + component.Id;

                var row = KUILayout.Row();
                row.Add(KUIText.Code(selector));
                row.Add(KUILayout.Spacer());
                row.Add(KUIButton.Secondary("Copy selector", () =>
                {
                    EditorGUIUtility.systemCopyBuffer = selector;
                    KUIToast.Success(root, "Copied " + selector);
                }));
                card.Add(row);

                if (string.IsNullOrEmpty(serializedObject.FindProperty("_id").stringValue))
                {
                    card.Add(new KUIBanner(
                        KUITone.Warning,
                        "Falling back to the GameObject name",
                        $"With no id set, tests must say id:{component.name} — which breaks the moment "
                        + "the object is renamed. Set an explicit id."));
                }
            }

            root.Add(card);

            root.Add(KUIText.Muted(
                "Runtime-spawned objects register themselves, so a test can find one the frame it "
                + "appears without a scene scan."));

            return root;
        }
    }

    /// <summary>
    /// The inspector for <see cref="GameTesterSettings"/>: what the defaults mean, and the way into
    /// the window that actually runs things.
    /// </summary>
    [CustomEditor(typeof(GameTesterSettings))]
    public sealed class GameTesterSettingsInspector : UnityEditor.Editor
    {
        public override VisualElement CreateInspectorGUI()
        {
            var root = new VisualElement();
            KUITheme.Apply(root);

            var settings = (GameTesterSettings)target;

            var card = new KUICard("GameTestKit settings",
                "Project-wide defaults. A run started from the GameTester window can override most of "
                + "them for that run only.");

            card.Add(KUIText.KeyValueTable(
                "Test folders", settings.TestFolders.Count > 0
                    ? string.Join(", ", settings.TestFolders)
                    : "whole project",
                "Backend", settings.Backend.ToString(),
                "Pointer", settings.Pointer.ToString(),
                "Step timeout", $"{settings.DefaultStepTimeout:0.#}s",
                "Locator timeout", $"{settings.LocatorTimeout:0.#}s",
                "Reports", settings.ReportFormats.Count > 0
                    ? string.Join(", ", settings.ReportFormats)
                    : "none"));

            card.Add(KUILayout.Row(
                KUIButton.Primary("Open GameTester", () => GameTesterWindow.Open()),
                KUIButton.Secondary("Copy tests to Resources", GameTesterMenu.CopyTestsToResources),
                KUIButton.Ghost("Install AI skill", GameTesterSkillGenerator.InstallFromMenu)));

            root.Add(card);

            if (string.IsNullOrEmpty(settings.RuntimeResourcesFolder))
            {
                root.Add(new KUIBanner(
                    KUITone.Warning,
                    "Scripts will not ship in player builds",
                    "With no Resources folder configured, a built player has no tests to discover. "
                    + "Set one if you intend to run tests on a device."));
            }

            var raw = new KUISection("All settings", true, "GameTestKit.Settings.Raw");
            KUIProperty.DrawAll(raw, serializedObject);
            root.Add(raw);

            root.Add(KUIText.Muted(Describe()));
            return root;
        }

        private static string Describe()
        {
            var builder = new StringBuilder();
            builder.Append("Discovery order at runtime: explicit paths → registered scripts → ");
            builder.Append("Assets (Editor only) → Resources → StreamingAssets. ");
            builder.Append("The first two let a bootstrap add tests that no folder scan would find.");
            return builder.ToString();
        }
    }
}
