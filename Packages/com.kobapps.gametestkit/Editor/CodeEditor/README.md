# Kobapps.CodeEditor

A reusable code-editing surface for Unity Editor tools: line-number gutter, monospace text area,
autocomplete popup with keyboard navigation, and a diagnostics line. Its own assembly
(`Kobapps.CodeEditor.Editor`) with **no references** — it does not know GameTestKit exists, so it can
be lifted into any other tool that needs an editor.

## Using it

```csharp
var editor = new CodeEditorView
{
    CompletionSource = new MyCompletions(),   // what may be typed here
    Validator        = new MyValidator(),     // what is wrong with it
};
editor.Toolbar.Add(new Button(Save) { text = "Save" });
editor.TextChanged  += text => MarkDirty();
editor.SaveRequested += Save;                 // Ctrl+S
root.Add(editor);
```

Both interfaces are optional — supply one, both or neither.

```csharp
public sealed class MyCompletions : ICompletionSource
{
    public IEnumerable<char> TriggerCharacters => new[] { '"', ':' };

    public IEnumerable<CompletionItem> GetCompletions(CompletionContext ctx)
    {
        yield return new CompletionItem("wait", "flow", CompletionKind.Keyword)
        {
            InsertText = "{ \"wait\": 0.5 }",
            Documentation = "Pause for a number of seconds.",
            Priority = 20,
        };
    }
}
```

Return everything plausible; the widget filters against the token before the caret, ranks
prefix-matches above contains-matches, and breaks ties on `Priority`.

## Keys

| | |
|---|---|
| **Ctrl+Space** | open the completion list regardless of context |
| **↑ ↓** | move through it |
| **Enter / Tab** | insert the selection, replacing the token before the caret |
| **Esc** | dismiss |
| **Ctrl+S** | raise `SaveRequested` |
| **Ctrl+Z** | undo |
| **Ctrl+Y / Ctrl+Shift+Z** | redo |

A trigger character opens the list on its own as you type.

## Undo

The widget owns its own history rather than leaving it to the `TextField`, because the field's history
only knows about typing — an accepted completion or a reformat would be invisible to it, and undoing
past one would restore text without restoring what the widget believes the document is.

Runs of ordinary typing coalesce into a single entry; a new one starts on a pause, a word boundary, or
a change too large to be a keystroke. That last rule is what gives every programmatic edit its own step.

Three entry points, and the difference between the last two matters:

| | |
|---|---|
| `Undo()` / `Redo()` / `CanUndo` / `CanRedo` | drive it from your own toolbar |
| `Value = …` | a **different document** is now open — history is cleared |
| `ReplaceDocument(text)` | an **edit to the open document** — one undoable step |

Use `ReplaceDocument` for anything a user would expect Ctrl+Z to take back (formatting, a bulk
replace). Use `Value` for load and revert.

## Where the completion list appears

Under the word being completed, like an IDE. UIElements exposes no caret rectangle, so the position is
computed from the caret's line and column — exact rather than approximate only because the editor is
monospace, so column × glyph width *is* the offset. The glyph is measured rather than assumed, since
the resolved font depends on what the Editor had available.

It anchors to the **start of the token**, not the caret, so the list stays still as the word is typed
instead of creeping right with every character. It flips above the line when there is no room below,
and is clamped inside the surface so it can never hang off an edge.

## Two things worth knowing before you change it

**`min-height: 0` on the flexible row is load-bearing.** A UIElements flex child will not shrink below
its content, so without it the editor overlaps whatever comes after it — which is exactly what was
wrong with the first version of this editor. Everything here is a column of `flex-shrink: 0` bars
around one flexible middle that carries `min-height: 0`.

**The completion popup is `Position.Absolute` inside a `Position.Relative` parent**, so it floats over
the text instead of pushing it down and resizing everything under it.

## Caret and fonts are both read defensively

The caret comes from `TextField.cursorIndex`, which has moved between Unity versions; if reading it
throws, the widget falls back to end-of-document. That direction matters: a completion appended at the
end is obvious and one undo away, whereas one spliced in at index 0 silently mangles the file.

The monospace font is resolved once from a list of candidates and may end up null, in which case the
default font is used. Built-in font names change across versions and `EditorGUIUtility.Load` logs an
error for each miss — a cosmetic detail that otherwise fills the console on every layout pass.
