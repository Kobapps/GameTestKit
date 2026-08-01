using System.Collections.Generic;

namespace Kobapps.CodeEditor
{
    /// <summary>What kind of thing a completion offers, so the list can show it at a glance.</summary>
    public enum CompletionKind
    {
        Keyword,
        Member,
        Value,
        Snippet,
        Text,
    }

    /// <summary>One entry in the completion list.</summary>
    public sealed class CompletionItem
    {
        /// <summary>What the list shows.</summary>
        public string Label;

        /// <summary>What gets written. Defaults to <see cref="Label"/>.</summary>
        public string InsertText;

        /// <summary>A short right-aligned hint — a type, a category, a signature.</summary>
        public string Detail;

        /// <summary>The longer explanation, shown under the list when this entry is highlighted.</summary>
        public string Documentation;

        public CompletionKind Kind = CompletionKind.Text;

        /// <summary>Higher sorts first among equally good matches.</summary>
        public int Priority;

        /// <summary>Where the caret should land after inserting, measured from the start of the insert.</summary>
        public int? CaretOffset;

        public string Text => string.IsNullOrEmpty(InsertText) ? Label : InsertText;

        public CompletionItem() { }

        public CompletionItem(string label, string detail = null, CompletionKind kind = CompletionKind.Text)
        {
            Label = label;
            Detail = detail;
            Kind = kind;
        }
    }

    /// <summary>Where the caret is and what is around it, so a source can answer in context.</summary>
    public readonly struct CompletionContext
    {
        /// <summary>The whole document.</summary>
        public readonly string Text;

        /// <summary>Caret offset into <see cref="Text"/>.</summary>
        public readonly int Caret;

        /// <summary>The partial word immediately before the caret — what the user has typed so far.</summary>
        public readonly string Prefix;

        /// <summary>The line the caret is on, up to the caret.</summary>
        public readonly string LinePrefix;

        /// <summary>True when the user asked for completions explicitly (Ctrl+Space).</summary>
        public readonly bool Explicit;

        public CompletionContext(string text, int caret, string prefix, string linePrefix, bool isExplicit)
        {
            Text = text;
            Caret = caret;
            Prefix = prefix;
            LinePrefix = linePrefix;
            Explicit = isExplicit;
        }
    }

    /// <summary>
    /// Supplies the completions for one language or document type.
    /// </summary>
    /// <remarks>
    /// The editor knows nothing about what it is editing — this is the whole seam. A host implements one
    /// of these against whatever it can actually answer from (a live registry, a schema, a symbol table),
    /// and gets the same list, filtering and keyboard handling as every other editor built on this widget.
    /// </remarks>
    public interface ICompletionSource
    {
        /// <summary>
        /// Candidates for this caret position. Return everything plausible; the editor filters and ranks.
        /// </summary>
        IEnumerable<CompletionItem> GetCompletions(CompletionContext context);

        /// <summary>
        /// Characters that should pop the list open on their own, beyond ordinary typing.
        /// </summary>
        /// <remarks>
        /// For JSON that is usually <c>"</c> and <c>:</c>; for C# it would be <c>.</c>. Returning nothing
        /// is fine — the list still opens as words are typed and on the explicit shortcut.
        /// </remarks>
        IEnumerable<char> TriggerCharacters { get; }
    }
}
