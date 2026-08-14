using System;
using System.IO;
using EditorCoreKit.Editor;
using Kobapps.CodeEditor;
using Kobapps.GameTestKit.Scripting;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kobapps.GameTestKit.Editor
{
    /// <summary>
    /// The <b>Script</b> tab of the selected test — editing a script in the same window that runs it.
    /// </summary>
    /// <remarks>
    /// The editing itself is <see cref="CodeEditorView"/>, which knows nothing about test scripts; this
    /// tab only supplies the two things that are specific to them — a completion source generated from
    /// the live <see cref="StepRegistry"/> and <see cref="GameTestBindings"/>, and a validator that runs
    /// the runner's own parser. Splitting it that way is what lets the editor be reused, and it is also
    /// why the completions can never drift from what the runner accepts: they are the same objects.
    /// <para>
    /// The tab is rebuilt whenever the detail pane is — which includes typing in the list's filter box —
    /// so unsaved text is held in <see cref="_pendingText"/> and restored on the way back in. Losing an
    /// edit because a rebuild happened underneath you is the kind of bug that stops people trusting an
    /// in-window editor at all.
    /// </para>
    /// <para>
    /// The layout is a strict column — editor, then nothing — with the editor as the only flexible row.
    /// An earlier version nested a text field and a palette inside a scroll view and let both grow,
    /// which is how its controls ended up on top of each other.
    /// </para>
    /// </remarks>
    public sealed partial class GameTesterWindow
    {
        private string _editingPath;
        private string _savedText = "";

        /// <summary>Unsaved text, kept across rebuilds of the pane. Null when there is none.</summary>
        private string _pendingText;
        private string _pendingPath;

        private CodeEditorView _editor;
        private readonly GameTestValidator _validator = new GameTestValidator();
        private Button _saveButton;
        private Button _revertButton;
        private Button _undoButton;
        private Button _redoButton;
        private Label _dirtyMark;

        private bool IsDirty => _editor != null && _editor.Value != _savedText;

        /// <summary>True when a script has edits that are not on disk — including while its tab is closed.</summary>
        private bool HasUnsavedScript =>
            IsDirty || (_pendingText != null && _pendingText != _savedText);

        // ---------------------------------------------------------------- the tab

        private void ShowScriptTab(VisualElement body, GameTest test)
        {
            body.Clear();

            if (string.IsNullOrEmpty(test.SourcePath))
            {
                CloseScriptEditor();
                body.Add(new KUIBanner(KUITone.Neutral, "Nothing to edit",
                    "This test was built in C# rather than loaded from a .gametest.json file, so there "
                    + "is no script behind it."));
                return;
            }

            var column = KUILayout.Column();
            column.style.flexGrow = 1;
            column.style.minHeight = 0;

            _editor = new CodeEditorView
            {
                CompletionSource = new GameTestCompletionSource(),
                Validator = _validator,
            };
            _editor.style.flexGrow = 1;
            _editor.style.minHeight = 0;
            _editor.TextChanged += text =>
            {
                _pendingPath = _editingPath;
                _pendingText = text;
                RefreshDirtyState();
            };
            _editor.SaveRequested += SaveFile;

            BuildEditorToolbar(_editor.Toolbar);
            column.Add(_editor);
            body.Add(column);

            LoadFile(test.SourcePath);
        }

        /// <summary>Drops the editor when the pane moves off it, so nothing stale is written later.</summary>
        private void CloseScriptEditor()
        {
            _editor = null;
            _saveButton = null;
            _revertButton = null;
            _undoButton = null;
            _redoButton = null;
            _dirtyMark = null;
        }

        /// <summary>The editor's own toolbar — actions that operate on the open document.</summary>
        private void BuildEditorToolbar(VisualElement toolbar)
        {
            _saveButton = new Button(SaveFile) { text = "Save" };
            toolbar.Add(_saveButton);

            _revertButton = new Button(RevertFile) { text = "Revert" };
            toolbar.Add(_revertButton);

            _undoButton = new Button(() => { _editor.Undo(); RefreshDirtyState(); })
            {
                text = "↶",
                tooltip = "Undo (Ctrl+Z). Covers accepted completions and Format, not just typing.",
            };
            toolbar.Add(_undoButton);

            _redoButton = new Button(() => { _editor.Redo(); RefreshDirtyState(); })
            {
                text = "↷",
                tooltip = "Redo (Ctrl+Y or Ctrl+Shift+Z).",
            };
            toolbar.Add(_redoButton);

            toolbar.Add(new Button(FormatJson) { text = "Format" });
            toolbar.Add(new Button(() => _editor.UpdateCompletions(true))
            {
                text = "Suggest",
                tooltip = "Show what may go here (Ctrl+Space). Verbs and state come from the live registry, " +
                          "so your game's own steps and bindings are in the list.",
            });

            _dirtyMark = new Label { style = { color = new Color(0.95f, 0.75f, 0.25f), flexShrink = 0 } };
            toolbar.Add(_dirtyMark);

            var spacer = new VisualElement { style = { flexGrow = 1 } };
            toolbar.Add(spacer);

            toolbar.Add(new Button(RunThisScript) { text = "▶ Run this" });
            toolbar.Add(new Button(RevealInProject) { text = "Reveal" });
        }

        private void RefreshDirtyState()
        {
            bool dirty = IsDirty;
            if (_dirtyMark != null) _dirtyMark.text = dirty ? "● unsaved" : "";
            _saveButton?.SetEnabled(dirty);
            _revertButton?.SetEnabled(dirty);
            _undoButton?.SetEnabled(_editor != null && _editor.CanUndo);
            _redoButton?.SetEnabled(_editor != null && _editor.CanRedo);
        }

        // ---------------------------------------------------------------- file ops

        private void LoadFile(string path)
        {
            _editingPath = path;
            _validator.SourcePath = path;

            try
            {
                _savedText = File.Exists(path) ? File.ReadAllText(path) : "";
            }
            catch (Exception e)
            {
                _savedText = "";
                SetStatus($"Could not read {Path.GetFileName(path)}: {e.Message}", KUITone.Error);
            }

            // An edit in progress outlives a rebuild of the pane, but only for the file it belongs to.
            bool restoring = _pendingText != null &&
                             string.Equals(_pendingPath, path, StringComparison.OrdinalIgnoreCase);

            if (_editor != null) _editor.Value = restoring ? _pendingText : _savedText;

            if (!restoring) ForgetPending();
            RefreshDirtyState();
        }

        /// <summary>Throws away the in-progress edit and goes back to what is on disk.</summary>
        private void RevertFile()
        {
            ForgetPending();
            LoadFile(_editingPath);
        }

        private void ForgetPending()
        {
            _pendingText = null;
            _pendingPath = null;
        }

        private void SaveFile()
        {
            if (string.IsNullOrEmpty(_editingPath)) return;

            // Not necessarily from the editor: an edit made on the Script tab survives a switch to
            // Overview, where "Save" can still be reached through the unsaved-changes prompt.
            var text = _editor != null ? _editor.Value : _pendingText;
            if (text == null) return;

            // Refuse quietly-broken saves: an unparseable script on disk becomes a red run later, far from
            // the typo that caused it.
            try
            {
                TestScriptParser.ParseTest(text, _editingPath);
            }
            catch (Exception e)
            {
                if (!EditorUtility.DisplayDialog("Save anyway?",
                        $"This script does not parse:\n\n{e.Message}\n\nSave it regardless?",
                        "Save anyway", "Keep editing"))
                    return;
            }

            try
            {
                File.WriteAllText(_editingPath, text);
                _savedText = text;
                ForgetPending();
                AssetDatabase.Refresh();

                var saved = _editingPath;
                Refresh();

                // Renaming a test in its own script moves its row; follow it rather than leaving the
                // pane pointing at whatever landed at the old index.
                SelectTestBySourcePath(saved);

                RefreshDirtyState();
                SetStatus($"Saved {Path.GetFileName(saved)}", KUITone.Success);
            }
            catch (Exception e)
            {
                SetStatus($"Could not save: {e.Message}", KUITone.Error);
            }
        }

        private void FormatJson()
        {
            if (_editor == null) return;
            try
            {
                _editor.ReplaceDocument(JsonValue.Parse(_editor.Value).ToJson());
                RefreshDirtyState();
            }
            catch (Exception e)
            {
                SetStatus($"Cannot format — the script does not parse: {e.Message}", KUITone.Error);
            }
        }

        /// <summary>
        /// Asks before an unsaved script is left behind. Called when the list selection is about to
        /// move to a different test.
        /// </summary>
        private bool ConfirmDiscard()
        {
            if (!HasUnsavedScript) return true;

            var answer = EditorUtility.DisplayDialogComplex("Unsaved changes",
                $"{Path.GetFileName(_editingPath)} has unsaved changes.",
                "Save", "Cancel", "Discard");

            switch (answer)
            {
                case 0:
                    SaveFile();
                    return !HasUnsavedScript;   // the save may have been refused at the parse prompt
                case 2:
                    ForgetPending();
                    return true;
                default:
                    return false;
            }
        }

        private void RevealInProject()
        {
            if (string.IsNullOrEmpty(_editingPath)) return;
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(_editingPath);
            if (asset != null) EditorGUIUtility.PingObject(asset);
            else EditorUtility.RevealInFinder(_editingPath);
        }

        private void RunThisScript()
        {
            if (IsDirty) SaveFile();
            if (string.IsNullOrEmpty(_editingPath)) return;

            var options = (_options ?? GameTesterSettings.Instance.CreateRunOptions()).Clone();
            options.Paths.Clear();
            options.Paths.Add(_editingPath);

            if (!EditorTestRunner.Run(options, true, out var problem))
            {
                SetStatus(problem, KUITone.Error);
                return;
            }

            // Scoped to this one script, so the rest of the list keeps its marks.
            BeginStreaming(new[] { _editingPath.Replace('\\', '/') });
            SetStatus($"Running {Path.GetFileName(_editingPath)}…", KUITone.Neutral);
        }
    }
}
