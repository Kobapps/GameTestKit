using System;
using System.Collections.Generic;
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
    /// Editing a script in the same window that runs it.
    /// </summary>
    /// <remarks>
    /// The editing itself is <see cref="CodeEditorView"/>, which knows nothing about test scripts; this
    /// page only supplies the two things that are specific to them — a completion source generated from
    /// the live <see cref="StepRegistry"/> and <see cref="GameTestBindings"/>, and a validator that runs
    /// the runner's own parser. Splitting it that way is what lets the editor be reused, and it is also
    /// why the completions can never drift from what the runner accepts: they are the same objects.
    /// <para>
    /// The layout is a strict column — file bar, editor, actions — with the editor as the only flexible
    /// row. The previous version nested a text field and a palette inside a scroll view and let both
    /// grow, which is how its controls ended up on top of each other.
    /// </para>
    /// </remarks>
    public sealed partial class GameTesterWindow
    {
        private string _editingPath;
        private string _savedText = "";
        private CodeEditorView _editor;
        private readonly GameTestValidator _validator = new GameTestValidator();
        private Button _saveButton;
        private Button _revertButton;
        private Button _undoButton;
        private Button _redoButton;
        private Label _dirtyMark;

        private bool IsDirty => _editor != null && _editor.Value != _savedText;

        private VisualElement BuildScriptEditorPage()
        {
            var root = KUILayout.Column();
            root.style.flexGrow = 1;
            root.style.minHeight = 0;

            var paths = ScriptPaths();
            if (paths.Count == 0)
            {
                root.Add(new KUIBanner(KUITone.Neutral, "No scripts found",
                    "Create one from Tools ▸ GameTestKit ▸ New Test Script, or point " +
                    "GameTesterSettings ▸ Test Folders at where yours live."));
                return root;
            }

            if (string.IsNullOrEmpty(_editingPath) || !paths.Contains(_editingPath))
                _editingPath = PreferredPath(paths);

            root.Add(BuildFileBar(paths));

            _editor = new CodeEditorView
            {
                CompletionSource = new GameTestCompletionSource(),
                Validator = _validator,
            };
            _editor.TextChanged += _ => RefreshDirtyState();
            _editor.SaveRequested += SaveFile;
            BuildEditorToolbar(_editor.Toolbar);
            root.Add(_editor);

            LoadFile(_editingPath);
            return root;
        }

        // ---------------------------------------------------------------- file bar

        private List<string> ScriptPaths()
        {
            var paths = new List<string>();
            foreach (var source in EditorTestCatalog.Discover())
                if (!string.IsNullOrEmpty(source.Path) && !paths.Contains(source.Path))
                    paths.Add(source.Path);
            paths.Sort(StringComparer.OrdinalIgnoreCase);
            return paths;
        }

        /// <summary>The script behind the selected test, so switching pages keeps your place.</summary>
        private string PreferredPath(List<string> paths)
        {
            if (_detailIndex >= 0 && _detailIndex < _visible.Count)
            {
                var path = _visible[_detailIndex].SourcePath;
                if (!string.IsNullOrEmpty(path) && paths.Contains(path)) return path;
            }
            return paths[0];
        }

        private VisualElement BuildFileBar(List<string> paths)
        {
            var bar = KUILayout.Row();
            bar.style.flexShrink = 0;
            bar.style.alignItems = Align.Center;
            bar.style.marginBottom = 6;

            var names = new List<string>();
            foreach (var path in paths) names.Add(Path.GetFileName(path));

            var popup = new PopupField<string>(names, Mathf.Max(0, paths.IndexOf(_editingPath)));
            popup.style.flexGrow = 1;
            popup.style.marginRight = 6;
            popup.RegisterValueChangedCallback(_ =>
            {
                var chosen = paths[Mathf.Clamp(popup.index, 0, paths.Count - 1)];
                if (chosen == _editingPath) return;

                if (!ConfirmDiscard())
                {
                    popup.SetValueWithoutNotify(Path.GetFileName(_editingPath));
                    return;
                }
                _editingPath = chosen;
                LoadFile(chosen);
            });
            bar.Add(popup);

            _dirtyMark = new Label { style = { color = new Color(0.95f, 0.75f, 0.25f), flexShrink = 0 } };
            bar.Add(_dirtyMark);

            return bar;
        }

        /// <summary>The editor's own toolbar — actions that operate on the open document.</summary>
        private void BuildEditorToolbar(VisualElement toolbar)
        {
            _saveButton = new Button(SaveFile) { text = "Save" };
            toolbar.Add(_saveButton);

            _revertButton = new Button(() => LoadFile(_editingPath)) { text = "Revert" };
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

            var spacer = new VisualElement { style = { flexGrow = 1 } };
            toolbar.Add(spacer);

            toolbar.Add(new Button(RunThisScript) { text = "▶ Run this" });
            toolbar.Add(new Button(RevealInProject) { text = "Reveal" });
        }

        private void RefreshDirtyState()
        {
            bool dirty = IsDirty;
            _dirtyMark?.SetEnabled(true);
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

            if (_editor != null) _editor.Value = _savedText;
            RefreshDirtyState();
        }

        private void SaveFile()
        {
            if (_editor == null || string.IsNullOrEmpty(_editingPath)) return;

            var text = _editor.Value;

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
                AssetDatabase.Refresh();
                Refresh();
                RefreshDirtyState();
                SetStatus($"Saved {Path.GetFileName(_editingPath)}", KUITone.Success);
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

        private bool ConfirmDiscard()
        {
            if (!IsDirty) return true;
            return EditorUtility.DisplayDialog("Unsaved changes",
                $"{Path.GetFileName(_editingPath)} has unsaved changes. Discard them?",
                "Discard", "Keep editing");
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
                SetStatus(problem, KUITone.Error);
            else
                SetStatus($"Running {Path.GetFileName(_editingPath)}…", KUITone.Neutral);
        }
    }
}
