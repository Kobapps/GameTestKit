using System.Collections.Generic;

namespace Kobapps.CodeEditor
{
    public enum DiagnosticSeverity
    {
        Info,
        Warning,
        Error,
    }

    /// <summary>Something the validator wants to say about the document.</summary>
    public sealed class CodeDiagnostic
    {
        public DiagnosticSeverity Severity = DiagnosticSeverity.Error;
        public string Message;

        /// <summary>1-based. 0 when the diagnostic is about the document as a whole.</summary>
        public int Line;

        /// <summary>1-based. 0 when unknown.</summary>
        public int Column;

        public CodeDiagnostic() { }

        public CodeDiagnostic(DiagnosticSeverity severity, string message, int line = 0, int column = 0)
        {
            Severity = severity;
            Message = message;
            Line = line;
            Column = column;
        }

        public static CodeDiagnostic Error(string message, int line = 0, int column = 0) =>
            new CodeDiagnostic(DiagnosticSeverity.Error, message, line, column);

        public static CodeDiagnostic Warning(string message, int line = 0, int column = 0) =>
            new CodeDiagnostic(DiagnosticSeverity.Warning, message, line, column);

        public static CodeDiagnostic Info(string message, int line = 0, int column = 0) =>
            new CodeDiagnostic(DiagnosticSeverity.Info, message, line, column);

        public string Describe() =>
            Line > 0 ? $"{Message}  (line {Line}{(Column > 0 ? ", col " + Column : "")})" : Message;
    }

    /// <summary>
    /// Checks the document and says what is wrong with it.
    /// </summary>
    /// <remarks>
    /// Kept separate from the completion source because the two answer different questions and are often
    /// backed by different things — a parser here, a registry there. A host that only wants one implements
    /// only one.
    /// </remarks>
    public interface ICodeValidator
    {
        IEnumerable<CodeDiagnostic> Validate(string text);
    }
}
