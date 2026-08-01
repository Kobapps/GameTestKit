using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kobapps.CodeEditor
{
    /// <summary>
    /// A reusable code-editing surface: gutter, monospace text area, completion popup, diagnostics.
    /// </summary>
    /// <remarks>
    /// Unity has no built-in code editor, so every tool that wants one grows its own half-finished
    /// version — a multiline <c>TextField</c>, no line numbers, no idea what you may type. This is that
    /// widget, written once and parameterised by two small interfaces: an
    /// <see cref="ICompletionSource"/> for what may be typed here and an <see cref="ICodeValidator"/> for
    /// what is wrong with it. It knows nothing about any particular language.
    /// <para>
    /// <b>Layout.</b> Everything is a column of fixed-height bars around one flexible middle. The middle
    /// row carries <c>min-height: 0</c>, without which a flex child in UIElements refuses to shrink below
    /// its content and overlaps whatever sits after it — the specific reason the first version of this
    /// editor had its controls on top of each other. The completion popup is absolutely positioned inside
    /// a relative parent so it floats over the text instead of pushing it around.
    /// </para>
    /// <para>
    /// <b>Caret.</b> Completions replace the token before the caret, so the widget needs to know where the
    /// caret is. Unity exposes that through <c>textSelection</c>, which has moved between versions, so it
    /// is read defensively and falls back to end-of-text — a wrong caret would silently corrupt the
    /// document, which is far worse than a completion landing at the end.
    /// </para>
    /// </remarks>
    public sealed class CodeEditorView : VisualElement
    {
        private const int MAX_VISIBLE_COMPLETIONS = 12;
        private const float LINE_HEIGHT = 15f;

        private readonly TextField _text;
        private readonly Label _gutter;
        private readonly ScrollView _scroll;
        private readonly VisualElement _completionPopup;
        private readonly ListView _completionList;
        private readonly Label _completionDoc;
        private readonly VisualElement _diagnosticsBar;
        private readonly Label _diagnosticsLabel;
        private readonly Label _caretLabel;

        private readonly List<CompletionItem> _completions = new List<CompletionItem>();
        private bool _completionOpen;
        private string _activePrefix = "";

        private readonly VisualElement _surface;

        // --- undo -------------------------------------------------------------------------------
        private readonly Stack<Snapshot> _undo = new Stack<Snapshot>();
        private readonly Stack<Snapshot> _redo = new Stack<Snapshot>();
        private string _lastText = "";
        private double _lastEditAt;
        private bool _applyingHistory;

        /// <summary>Typing pauses longer than this start a new undo entry.</summary>
        private const double COALESCE_SECONDS = 0.6;

        /// <summary>Undo entries kept before the oldest is dropped.</summary>
        private const int MAX_UNDO = 200;

        private readonly struct Snapshot
        {
            public readonly string Text;
            public readonly int Caret;

            public Snapshot(string text, int caret) { Text = text; Caret = caret; }
        }

        /// <summary>Raised whenever the document changes, for whatever hosts it to track dirtiness.</summary>
        public event Action<string> TextChanged;

        /// <summary>Raised when the user asks to save (Ctrl+S).</summary>
        public event Action SaveRequested;

        public ICompletionSource CompletionSource { get; set; }

        public ICodeValidator Validator { get; set; }

        /// <summary>A row above the editor that a host fills with its own buttons.</summary>
        public VisualElement Toolbar { get; }

        public string Value
        {
            get => _text.value ?? "";
            set
            {
                if (_text.value == value) return;
                _text.SetValueWithoutNotify(value ?? "");
                _lastText = _text.value ?? "";
                ClearHistory();
                RefreshGutter();
                Revalidate();
            }
        }

        public CodeEditorView()
        {
            style.flexGrow = 1;
            style.minHeight = 0;
            style.flexDirection = FlexDirection.Column;

            Toolbar = new VisualElement();
            Toolbar.style.flexDirection = FlexDirection.Row;
            Toolbar.style.flexShrink = 0;
            Toolbar.style.flexWrap = Wrap.Wrap;
            Toolbar.style.marginBottom = 4;
            Add(Toolbar);

            // --- the editing surface -------------------------------------------------------------
            // Relative, so the completion popup can be absolutely positioned against it.
            var surface = _surface = new VisualElement();
            surface.style.position = Position.Relative;
            surface.style.flexGrow = 1;
            surface.style.minHeight = 0;
            Add(surface);

            _scroll = new ScrollView(ScrollViewMode.VerticalAndHorizontal);
            _scroll.style.flexGrow = 1;
            _scroll.style.minHeight = 0;
            _scroll.style.borderTopWidth = _scroll.style.borderBottomWidth = 1;
            _scroll.style.borderLeftWidth = _scroll.style.borderRightWidth = 1;
            _scroll.style.borderTopColor = _scroll.style.borderBottomColor =
                _scroll.style.borderLeftColor = _scroll.style.borderRightColor = Border;
            surface.Add(_scroll);

            var row = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            _scroll.Add(row);

            _gutter = new Label();
            ApplyMono(_gutter.style);
            _gutter.style.fontSize = 12;
            _gutter.style.color = GutterText;
            _gutter.style.unityTextAlign = TextAnchor.UpperRight;
            _gutter.style.minWidth = 34;
            _gutter.style.paddingRight = 6;
            _gutter.style.paddingTop = 2;
            _gutter.style.flexShrink = 0;
            _gutter.style.backgroundColor = GutterBack;
            row.Add(_gutter);

            _text = new TextField { multiline = true };
            _text.style.flexGrow = 1;
            _text.style.marginLeft = 0;
            _text.style.marginTop = 0;
            _text.style.whiteSpace = WhiteSpace.NoWrap;
            StyleInput(_text);
            row.Add(_text);

            _text.RegisterValueChangedCallback(OnTextChanged);
            _text.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);

            // --- completion popup ----------------------------------------------------------------
            _completionPopup = new VisualElement();
            _completionPopup.style.position = Position.Absolute;
            _completionPopup.style.left = 44;
            _completionPopup.style.top = 4;
            _completionPopup.style.width = 420;
            _completionPopup.style.maxHeight = 260;
            _completionPopup.style.backgroundColor = PopupBack;
            _completionPopup.style.borderTopWidth = _completionPopup.style.borderBottomWidth = 1;
            _completionPopup.style.borderLeftWidth = _completionPopup.style.borderRightWidth = 1;
            _completionPopup.style.borderTopColor = _completionPopup.style.borderBottomColor =
                _completionPopup.style.borderLeftColor = _completionPopup.style.borderRightColor = Accent;
            _completionPopup.style.display = DisplayStyle.None;
            surface.Add(_completionPopup);

            _completionList = new ListView
            {
                fixedItemHeight = 18,
                selectionType = SelectionType.Single,
                makeItem = MakeCompletionRow,
                bindItem = BindCompletionRow,
                itemsSource = _completions,
            };
            _completionList.style.maxHeight = MAX_VISIBLE_COMPLETIONS * 18;
            _completionList.style.flexGrow = 0;
            _completionList.selectionChanged += _ => UpdateCompletionDoc();
#if UNITY_2022_2_OR_NEWER
            _completionList.itemsChosen += _ => AcceptCompletion();
#endif
            _completionPopup.Add(_completionList);

            _completionDoc = new Label();
            _completionDoc.style.whiteSpace = WhiteSpace.Normal;
            _completionDoc.style.paddingLeft = _completionDoc.style.paddingRight = 6;
            _completionDoc.style.paddingTop = _completionDoc.style.paddingBottom = 4;
            _completionDoc.style.fontSize = 11;
            _completionDoc.style.color = MutedText;
            _completionDoc.style.borderTopWidth = 1;
            _completionDoc.style.borderTopColor = Border;
            _completionPopup.Add(_completionDoc);

            // --- status --------------------------------------------------------------------------
            _diagnosticsBar = new VisualElement();
            _diagnosticsBar.style.flexDirection = FlexDirection.Row;
            _diagnosticsBar.style.flexShrink = 0;
            _diagnosticsBar.style.justifyContent = Justify.SpaceBetween;
            _diagnosticsBar.style.paddingTop = 4;
            Add(_diagnosticsBar);

            _diagnosticsLabel = new Label { style = { whiteSpace = WhiteSpace.Normal, flexGrow = 1 } };
            _diagnosticsBar.Add(_diagnosticsLabel);

            _caretLabel = new Label { style = { color = MutedText, flexShrink = 0, marginLeft = 8 } };
            _diagnosticsBar.Add(_caretLabel);

            RefreshGutter();
        }

        // ================================================================ editing

        private void OnTextChanged(ChangeEvent<string> evt)
        {
            if (!_applyingHistory) RecordEdit(evt.previousValue, evt.newValue);
            _lastText = evt.newValue ?? "";

            RefreshGutter();
            Revalidate();
            TextChanged?.Invoke(evt.newValue);

            // Re-filter while the user keeps typing, and close once the token is finished.
            if (_completionOpen) UpdateCompletions(explicitly: false);
        }

        // ================================================================ undo

        /// <summary>Undoable history, including edits this widget makes on the user's behalf.</summary>
        public bool CanUndo => _undo.Count > 0;

        public bool CanRedo => _redo.Count > 0;

        /// <summary>
        /// Decides whether this change starts a new undo entry or joins the previous one.
        /// </summary>
        /// <remarks>
        /// Per-keystroke undo is useless — nobody wants sixty presses to take back a word. So runs of
        /// ordinary typing coalesce into one entry, and a new one starts when the user pauses, crosses a
        /// word boundary, or when the change is too big to be a keystroke at all. That last case is what
        /// covers the edits the editor itself makes: accepting a completion or reformatting the document
        /// replaces a span, so it always lands as its own undo step rather than being absorbed into
        /// whatever the user happened to be typing.
        /// </remarks>
        private void RecordEdit(string before, string after)
        {
            before ??= "";
            after ??= "";

            double now = EditorApplication.timeSinceStartup;
            bool structural = Math.Abs(after.Length - before.Length) > 1;
            bool boundary = EndsAtBoundary(after);
            bool paused = now - _lastEditAt > COALESCE_SECONDS;

            if (_undo.Count == 0 || structural || boundary || paused)
                Push(new Snapshot(before, Math.Min(CaretIndex, before.Length)));

            _lastEditAt = now;
            _redo.Clear();
        }

        /// <summary>True when the text now ends on whitespace or punctuation — a natural undo boundary.</summary>
        private static bool EndsAtBoundary(string text)
        {
            if (text.Length == 0) return false;
            char last = text[text.Length - 1];
            return char.IsWhiteSpace(last) || char.IsPunctuation(last) || char.IsSymbol(last);
        }

        private void Push(Snapshot snapshot)
        {
            if (_undo.Count > 0 && _undo.Peek().Text == snapshot.Text) return;

            _undo.Push(snapshot);
            if (_undo.Count <= MAX_UNDO) return;

            // Stack has no bounded form, so trim by rebuilding oldest-out.
            var kept = new List<Snapshot>(_undo);
            kept.RemoveAt(kept.Count - 1);
            _undo.Clear();
            for (int i = kept.Count - 1; i >= 0; i--) _undo.Push(kept[i]);
        }

        public void Undo()
        {
            if (_undo.Count == 0) return;
            _redo.Push(new Snapshot(Value, CaretIndex));
            Apply(_undo.Pop());
        }

        public void Redo()
        {
            if (_redo.Count == 0) return;
            _undo.Push(new Snapshot(Value, CaretIndex));
            Apply(_redo.Pop());
        }

        private void Apply(Snapshot snapshot)
        {
            CloseCompletions();
            _applyingHistory = true;
            try
            {
                _text.value = snapshot.Text;
                _lastText = snapshot.Text;
            }
            finally
            {
                _applyingHistory = false;
            }

            RefreshGutter();
            Revalidate();
            SetCaret(Mathf.Clamp(snapshot.Caret, 0, snapshot.Text.Length));
            TextChanged?.Invoke(snapshot.Text);
        }

        /// <summary>Drops the history. Called when the document is replaced outright.</summary>
        public void ClearHistory()
        {
            _undo.Clear();
            _redo.Clear();
            _lastEditAt = 0;
        }

        /// <summary>
        /// Replaces the whole document as one undoable step — for Format and similar.
        /// </summary>
        /// <remarks>
        /// Distinct from the <see cref="Value"/> setter, which clears history because it means "a
        /// different file is now open". Reformatting is an edit to the file you are already in, and
        /// taking it back is exactly what a user expects Ctrl+Z to do.
        /// </remarks>
        public void ReplaceDocument(string text)
        {
            if (Value == text) return;
            Push(new Snapshot(Value, CaretIndex));
            _redo.Clear();

            _applyingHistory = true;
            try
            {
                _text.value = text ?? "";
                _lastText = _text.value;
            }
            finally
            {
                _applyingHistory = false;
            }

            RefreshGutter();
            Revalidate();
            TextChanged?.Invoke(_text.value);
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            // Ctrl+Space asks for the list no matter what is under the caret.
            if (evt.keyCode == KeyCode.Space && (evt.ctrlKey || evt.commandKey))
            {
                UpdateCompletions(explicitly: true);
                evt.StopPropagation();
                return;
            }

            if (evt.keyCode == KeyCode.S && (evt.ctrlKey || evt.commandKey))
            {
                SaveRequested?.Invoke();
                evt.StopPropagation();
                return;
            }

            // Take undo over from the text field. Its own history only knows about typing, so an
            // accepted completion or a reformat would be invisible to it — and worse, undoing past one
            // would restore text without restoring what the widget believes the document is.
            if (evt.keyCode == KeyCode.Z && (evt.ctrlKey || evt.commandKey))
            {
                if (evt.shiftKey) Redo(); else Undo();
                evt.StopPropagation();
                evt.PreventDefault();
                return;
            }

            if (evt.keyCode == KeyCode.Y && (evt.ctrlKey || evt.commandKey))
            {
                Redo();
                evt.StopPropagation();
                evt.PreventDefault();
                return;
            }

            if (!_completionOpen)
            {
                // Typing a trigger character opens the list, so `"` in JSON offers the keys.
                if (CompletionSource != null && evt.character != '\0')
                {
                    foreach (var trigger in CompletionSource.TriggerCharacters)
                        if (trigger == evt.character)
                        {
                            schedule.Execute(() => UpdateCompletions(explicitly: true)).ExecuteLater(1);
                            break;
                        }
                }
                return;
            }

            switch (evt.keyCode)
            {
                case KeyCode.DownArrow:
                    MoveSelection(1);
                    evt.StopPropagation();
                    evt.PreventDefault();
                    break;
                case KeyCode.UpArrow:
                    MoveSelection(-1);
                    evt.StopPropagation();
                    evt.PreventDefault();
                    break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                case KeyCode.Tab:
                    AcceptCompletion();
                    evt.StopPropagation();
                    evt.PreventDefault();
                    break;
                case KeyCode.Escape:
                    CloseCompletions();
                    evt.StopPropagation();
                    break;
            }
        }

        private void MoveSelection(int delta)
        {
            if (_completions.Count == 0) return;
            int next = Mathf.Clamp(_completionList.selectedIndex + delta, 0, _completions.Count - 1);
            _completionList.selectedIndex = next;
            _completionList.ScrollToItem(next);
        }

        // ================================================================ completion

        /// <summary>Recomputes the list for the current caret, and opens or closes it accordingly.</summary>
        public void UpdateCompletions(bool explicitly)
        {
            if (CompletionSource == null) { CloseCompletions(); return; }

            var text = Value;
            int caret = Mathf.Clamp(CaretIndex, 0, text.Length);
            _activePrefix = TokenBefore(text, caret);

            var context = new CompletionContext(text, caret, _activePrefix, LineBefore(text, caret), explicitly);

            _completions.Clear();
            foreach (var item in CompletionSource.GetCompletions(context))
            {
                if (item == null || string.IsNullOrEmpty(item.Label)) continue;
                if (!Matches(item, _activePrefix)) continue;
                _completions.Add(item);
            }

            if (_completions.Count == 0 || (!explicitly && _activePrefix.Length == 0))
            {
                CloseCompletions();
                return;
            }

            _completions.Sort((a, b) =>
            {
                int byRank = Rank(b, _activePrefix).CompareTo(Rank(a, _activePrefix));
                return byRank != 0 ? byRank : string.CompareOrdinal(a.Label, b.Label);
            });

            _completionList.itemsSource = _completions;
            _completionList.Rebuild();
            _completionList.selectedIndex = 0;
            _completionPopup.style.display = DisplayStyle.Flex;
            _completionOpen = true;
            UpdateCompletionDoc();

            // Position after showing it, so the popup has a resolved size to fit on screen with.
            PositionPopupAtToken(text, caret);
            _completionPopup.schedule.Execute(() => PositionPopupAtToken(Value, CaretIndex)).ExecuteLater(1);
        }

        /// <summary>
        /// Puts the popup under the word being completed, the way an IDE does.
        /// </summary>
        /// <remarks>
        /// UIElements does not expose a caret rectangle, so the position is computed from the caret's
        /// line and column. That is exact rather than approximate here only because the editor is
        /// monospace — every column is the same width, so column × glyph width is the real offset. The
        /// glyph is measured rather than assumed, since the resolved font depends on what the Editor
        /// actually had available.
        /// <para>
        /// It anchors to the <em>start of the token</em>, not the caret, so the list stays put as the
        /// word is typed instead of creeping right with every character. It flips above the line when
        /// there is no room below, and is clamped inside the surface so it can never hang off the edge.
        /// </para>
        /// </remarks>
        private void PositionPopupAtToken(string text, int caret)
        {
            if (_surface == null) return;

            caret = Mathf.Clamp(caret, 0, text.Length);
            int tokenStart = Mathf.Max(0, caret - (_activePrefix?.Length ?? 0));

            int line = 0, column = 0;
            for (int i = 0; i < tokenStart; i++)
            {
                if (text[i] == '\n') { line++; column = 0; }
                else column++;
            }

            float glyph = GlyphWidth();
            float lineHeight = LINE_HEIGHT;
            float gutter = _gutter.resolvedStyle.width > 0 ? _gutter.resolvedStyle.width : 40f;
            var scrolled = _scroll.scrollOffset;

            float x = gutter + column * glyph - scrolled.x;
            float y = (line + 1) * lineHeight - scrolled.y + 4f;

            float surfaceWidth = _surface.resolvedStyle.width;
            float surfaceHeight = _surface.resolvedStyle.height;
            float popupWidth = _completionPopup.resolvedStyle.width > 0
                ? _completionPopup.resolvedStyle.width
                : 420f;
            float popupHeight = _completionPopup.resolvedStyle.height > 0
                ? _completionPopup.resolvedStyle.height
                : 200f;

            if (surfaceWidth > 0) x = Mathf.Clamp(x, 0f, Mathf.Max(0f, surfaceWidth - popupWidth));

            // No room below the line? Sit above it rather than spilling out of the view.
            if (surfaceHeight > 0 && y + popupHeight > surfaceHeight)
            {
                float above = (line * lineHeight) - scrolled.y - popupHeight - 2f;
                y = above >= 0f ? above : Mathf.Max(0f, surfaceHeight - popupHeight);
            }

            _completionPopup.style.left = x;
            _completionPopup.style.top = y;
        }

        private float _glyphWidth;

        /// <summary>Width of one character in the editor's font, measured once.</summary>
        private float GlyphWidth()
        {
            if (_glyphWidth > 0f) return _glyphWidth;
            try
            {
                var size = _text.MeasureTextSize("MMMMMMMMMM", 0, MeasureMode.Undefined, 0, MeasureMode.Undefined);
                if (size.x > 0f) _glyphWidth = size.x / 10f;
            }
            catch (Exception) { /* fall through to the default below */ }

            if (_glyphWidth <= 0f) _glyphWidth = 7f;
            return _glyphWidth;
        }

        private static bool Matches(CompletionItem item, string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return true;
            return item.Label.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Prefix matches beat contains-matches, and the source's own priority breaks ties.</summary>
        private static int Rank(CompletionItem item, string prefix)
        {
            int score = item.Priority;
            if (string.IsNullOrEmpty(prefix)) return score;
            if (item.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) score += 100;
            return score;
        }

        private void AcceptCompletion()
        {
            if (!_completionOpen || _completions.Count == 0) return;

            int index = Mathf.Clamp(_completionList.selectedIndex, 0, _completions.Count - 1);
            var item = _completions[index];

            var text = Value;
            int caret = Mathf.Clamp(CaretIndex, 0, text.Length);
            int start = caret - _activePrefix.Length;
            if (start < 0) start = caret;

            var inserted = item.Text;
            var updated = text.Substring(0, start) + inserted + text.Substring(caret);

            CloseCompletions();
            _text.value = updated;

            int landing = start + (item.CaretOffset ?? inserted.Length);
            SetCaret(Mathf.Clamp(landing, 0, updated.Length));
        }

        private void CloseCompletions()
        {
            _completionOpen = false;
            _completionPopup.style.display = DisplayStyle.None;
        }

        private void UpdateCompletionDoc()
        {
            if (_completions.Count == 0) { _completionDoc.text = ""; return; }
            int index = Mathf.Clamp(_completionList.selectedIndex, 0, _completions.Count - 1);
            var item = _completions[index];
            _completionDoc.text = string.IsNullOrEmpty(item.Documentation)
                ? item.Detail ?? ""
                : item.Documentation;
        }

        private VisualElement MakeCompletionRow()
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, paddingLeft = 6 } };
            var label = new Label { name = "label", style = { flexGrow = 1, fontSize = 12 } };
            ApplyMono(label.style);
            row.Add(label);
            row.Add(new Label
            {
                name = "detail",
                style = { color = MutedText, fontSize = 11, flexShrink = 0, marginRight = 6 },
            });
            return row;
        }

        private void BindCompletionRow(VisualElement element, int index)
        {
            if (index < 0 || index >= _completions.Count) return;
            var item = _completions[index];
            element.Q<Label>("label").text = item.Label;
            element.Q<Label>("detail").text = item.Detail ?? item.Kind.ToString();
        }

        // ================================================================ diagnostics & gutter

        /// <summary>Re-runs the validator and repaints the status line.</summary>
        public void Revalidate()
        {
            UpdateCaretLabel();

            if (Validator == null) { _diagnosticsLabel.text = ""; return; }

            CodeDiagnostic worst = null;
            int errors = 0, warnings = 0;

            foreach (var diagnostic in Validator.Validate(Value))
            {
                if (diagnostic == null) continue;
                if (diagnostic.Severity == DiagnosticSeverity.Error) errors++;
                else if (diagnostic.Severity == DiagnosticSeverity.Warning) warnings++;
                if (worst == null || diagnostic.Severity > worst.Severity) worst = diagnostic;
            }

            if (worst == null)
            {
                _diagnosticsLabel.text = "";
                return;
            }

            var suffix = errors + warnings > 1 ? $"   (+{errors + warnings - 1} more)" : "";
            _diagnosticsLabel.text = Prefix(worst.Severity) + worst.Describe() + suffix;
            _diagnosticsLabel.style.color = ColorFor(worst.Severity);
        }

        private static string Prefix(DiagnosticSeverity severity) => severity switch
        {
            DiagnosticSeverity.Error => "✘  ",
            DiagnosticSeverity.Warning => "▲  ",
            _ => "✔  ",
        };

        private void UpdateCaretLabel()
        {
            var text = Value;
            int caret = Mathf.Clamp(CaretIndex, 0, text.Length);
            int line = 1, column = 1;
            for (int i = 0; i < caret; i++)
            {
                if (text[i] == '\n') { line++; column = 1; }
                else column++;
            }
            _caretLabel.text = $"Ln {line}, Col {column}";
        }

        private void RefreshGutter()
        {
            var text = Value;
            int lines = 1;
            for (int i = 0; i < text.Length; i++) if (text[i] == '\n') lines++;

            var sb = new StringBuilder(lines * 4);
            for (int i = 1; i <= lines; i++) sb.Append(i).Append('\n');
            _gutter.text = sb.ToString();
            _gutter.style.minHeight = lines * LINE_HEIGHT;
        }

        // ================================================================ caret plumbing

        /// <summary>
        /// The caret offset, read defensively because Unity has moved this API between versions.
        /// </summary>
        /// <remarks>
        /// Falls back to the end of the document rather than 0: a completion inserted at the end is
        /// obvious and easy to undo, whereas one spliced into position 0 silently mangles the file.
        /// </remarks>
        private int CaretIndex
        {
            get
            {
                try { return _text.cursorIndex; }
                catch (Exception) { return Value.Length; }
            }
        }

        private void SetCaret(int index)
        {
            try
            {
                _text.cursorIndex = index;
                _text.selectIndex = index;
            }
            catch (Exception)
            {
                // Not fatal — the text is already correct, only the caret is not where we asked.
            }
        }

        // ================================================================ styling

        private static Font _mono;
        private static bool _monoResolved;

        /// <summary>
        /// A monospace font, or null to keep Unity's default.
        /// </summary>
        /// <remarks>
        /// Resolved once and defensively. The built-in font names have changed across Unity versions —
        /// <c>LiberationSans.ttf</c> is gone in Unity 6 and asking for it logs an error on every layout
        /// pass, which is how a cosmetic detail turns into a console full of noise. Returning null is a
        /// perfectly good outcome: the editor renders in the default font and nothing complains.
        /// </remarks>
        private static Font Mono
        {
            get
            {
                if (_monoResolved) return _mono;
                _monoResolved = true;

                foreach (var path in new[]
                         {
                             "Fonts/RobotoMono/RobotoMono-Regular.ttf",
                             "Fonts/consola.ttf",
                             "Fonts/Inconsolata.ttf",
                         })
                {
                    try
                    {
                        if (EditorGUIUtility.Load(path) is Font found) { _mono = found; return _mono; }
                    }
                    catch (Exception) { /* try the next one */ }
                }

                foreach (var name in new[] { "Consolas", "Menlo", "DejaVu Sans Mono", "Courier New" })
                {
                    try
                    {
                        var created = Font.CreateDynamicFontFromOSFont(name, 12);
                        if (created != null) { _mono = created; return _mono; }
                    }
                    catch (Exception) { /* try the next one */ }
                }

                return _mono;
            }
        }

        private static void ApplyMono(IStyle style)
        {
            var font = Mono;
            if (font != null) style.unityFont = font;
        }

        private static void StyleInput(TextField field)
        {
            var input = field.Q(TextField.textInputUssName);
            if (input == null) return;
            ApplyMono(input.style);
            input.style.fontSize = 12;
            input.style.whiteSpace = WhiteSpace.NoWrap;
            input.style.paddingTop = 2;
        }

        private static Color Border => EditorGUIUtility.isProSkin
            ? new Color(0.16f, 0.16f, 0.16f)
            : new Color(0.6f, 0.6f, 0.6f);

        private static Color GutterBack => EditorGUIUtility.isProSkin
            ? new Color(0.18f, 0.18f, 0.18f)
            : new Color(0.85f, 0.85f, 0.85f);

        private static Color GutterText => new Color(0.5f, 0.5f, 0.5f);

        private static Color MutedText => new Color(0.62f, 0.62f, 0.62f);

        private static Color PopupBack => EditorGUIUtility.isProSkin
            ? new Color(0.22f, 0.22f, 0.22f)
            : new Color(0.94f, 0.94f, 0.94f);

        private static Color Accent => new Color(0.35f, 0.55f, 0.85f);

        private static Color ColorFor(DiagnosticSeverity severity) => severity switch
        {
            DiagnosticSeverity.Error => new Color(0.95f, 0.4f, 0.35f),
            DiagnosticSeverity.Warning => new Color(0.95f, 0.75f, 0.25f),
            _ => new Color(0.45f, 0.8f, 0.5f),
        };

        // ================================================================ text helpers

        /// <summary>The identifier-ish token immediately before the caret.</summary>
        public static string TokenBefore(string text, int caret)
        {
            int start = caret;
            while (start > 0)
            {
                char c = text[start - 1];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '$') start--;
                else break;
            }
            return text.Substring(start, caret - start);
        }

        /// <summary>The current line up to the caret.</summary>
        public static string LineBefore(string text, int caret)
        {
            int start = caret;
            while (start > 0 && text[start - 1] != '\n') start--;
            return text.Substring(start, caret - start);
        }
    }
}
