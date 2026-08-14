using System;
using UnityEditor;
using UnityEngine;

namespace Kobapps.GameTestKit.Editor
{
    /// <summary>
    /// A one-line "type a name" modal, for naming a category.
    /// </summary>
    /// <remarks>
    /// <see cref="EditorUtility"/> has a dialog for every answer except a typed one, and UI Toolkit has
    /// no modal at all, so this is IMGUI in a <c>ShowModalUtility</c> window — the one place in the
    /// package that is not UI Toolkit, and small enough to stay that way.
    /// </remarks>
    public sealed class CategoryPrompt : EditorWindow
    {
        private const string FieldName = "gametestkit.category.prompt";

        private string _message = "";
        private string _value = "";
        private bool _accepted;
        private bool _focused;

        /// <summary>Shows the prompt and blocks until it closes. Returns null when cancelled.</summary>
        public static string Show(string title, string message, string initialValue = "")
        {
            var window = CreateInstance<CategoryPrompt>();
            window.titleContent = new GUIContent(title);
            window._message = message ?? "";
            window._value = initialValue ?? "";
            window.minSize = new Vector2(380, 116);
            window.maxSize = new Vector2(380, 116);

            // Centred on the editor rather than wherever the last utility window was.
            var main = EditorGUIUtility.GetMainWindowPosition();
            window.position = new Rect(
                main.x + (main.width - 380f) * 0.5f,
                main.y + (main.height - 116f) * 0.5f,
                380f, 116f);

            window.ShowModalUtility();

            return window._accepted && !string.IsNullOrWhiteSpace(window._value) ? window._value.Trim() : null;
        }

        private void OnGUI()
        {
            // Enter and Escape have to be read before the controls consume them.
            var key = Event.current.type == EventType.KeyDown ? Event.current.keyCode : KeyCode.None;

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(_message, EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space(4);

            GUI.SetNextControlName(FieldName);
            _value = EditorGUILayout.TextField(_value);

            if (!_focused)
            {
                EditorGUI.FocusTextInControl(FieldName);
                _focused = true;
            }

            GUILayout.FlexibleSpace();

            // Decided here, acted on below: closing the window mid-layout would leave the scopes
            // above unbalanced and spray "GUI Error: Invalid GUILayout state" across the console.
            bool accept = false, cancel = false;

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();

                cancel = GUILayout.Button("Cancel", GUILayout.Width(80));

                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_value)))
                    accept = GUILayout.Button("Create", GUILayout.Width(80));
            }

            EditorGUILayout.Space(4);

            if (key == KeyCode.Return || key == KeyCode.KeypadEnter) accept = true;
            else if (key == KeyCode.Escape) cancel = true;

            if (cancel)
            {
                _accepted = false;
                Close();
            }
            else if (accept && !string.IsNullOrWhiteSpace(_value))
            {
                _accepted = true;
                Close();
            }
        }
    }
}
