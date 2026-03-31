namespace AhkWin32.Generator.Emit;

using System.Text;

/// <summary>
/// Indentation-aware text writer for generating AutoHotkey v2 source files.
/// Wraps a StringBuilder with indent tracking and convenience methods for
/// common AHK constructs (classes, properties, static fields, etc.).
/// </summary>
public sealed class AhkWriter
{
    private readonly StringBuilder _sb = new();
    private int _indentLevel;
    private const int IndentSize = 4;

    private string Indent => new(' ', _indentLevel * IndentSize);

    // --- Directives (always at column 0) ---

    /// <summary>Write a #Requires directive.</summary>
    public void Require(string version) => _sb.AppendLine($"#Requires {version}");

    /// <summary>Write a #Include directive.</summary>
    public void Include(string path) => _sb.AppendLine($"#Include {path}");

    // --- Structure ---

    /// <summary>Write an empty line.</summary>
    public void BlankLine() => _sb.AppendLine();

    /// <summary>
    /// Open a class block. Returns an IDisposable that writes the closing brace on Dispose.
    /// Format: "class NAME extends BASE {" or "class NAME {" (always space before brace).
    /// Supports nesting — indent level is always relative.
    /// </summary>
    public IndentScope Class(string name, string? extends = null)
    {
        string header = extends != null
            ? $"class {name} extends {extends}"
            : $"class {name}";

        _sb.AppendLine($"{Indent}{header} {{");
        _indentLevel++;
        return new IndentScope(this);
    }

    /// <summary>
    /// Open a static property block: "static NAME {".
    /// </summary>
    public IndentScope Property(string name)
    {
        _sb.AppendLine($"{Indent}static {name} {{");
        _indentLevel++;
        return new IndentScope(this);
    }

    /// <summary>
    /// Open an instance property block: "NAME {".
    /// Used for struct member properties (non-static).
    /// </summary>
    public IndentScope InstanceProperty(string name)
    {
        _sb.AppendLine($"{Indent}{name} {{");
        _indentLevel++;
        return new IndentScope(this);
    }

    /// <summary>
    /// Open a getter block: "get {".
    /// </summary>
    public IndentScope GetBlock()
    {
        _sb.AppendLine($"{Indent}get {{");
        _indentLevel++;
        return new IndentScope(this);
    }

    // --- Content ---

    /// <summary>Write a static fat-arrow field: "static NAME => expr".</summary>
    public void StaticField(string name, string expr)
        => _sb.AppendLine($"{Indent}static {name} => {expr}");

    /// <summary>Write a line at the current indent level.</summary>
    public void Line(string code) => _sb.AppendLine($"{Indent}{code}");

    /// <summary>Write a line verbatim (no indent added).</summary>
    public void RawLine(string code) => _sb.AppendLine(code);

    /// <summary>Write a region marker at the current indent level.</summary>
    public void Region(string name) => _sb.AppendLine($"{Indent};@region {name}");

    /// <summary>Write an end-region marker at the current indent level.</summary>
    public void EndRegion(string name) => _sb.AppendLine($"{Indent};@endregion {name}");

    // --- Output ---

    /// <summary>Get the generated text.</summary>
    public override string ToString() => _sb.ToString();

    /// <summary>Current length of the output buffer.</summary>
    public int Length => _sb.Length;

    /// <summary>Current indent level (for external use, e.g., doc comment writers).</summary>
    public int CurrentIndentLevel => _indentLevel;

    /// <summary>Get the current indent string (for external use).</summary>
    public string CurrentIndent => Indent;

    // --- Scope helper ---

    /// <summary>
    /// Disposable scope that decrements indent and writes a closing brace.
    /// </summary>
    public readonly struct IndentScope : IDisposable
    {
        private readonly AhkWriter _writer;

        internal IndentScope(AhkWriter writer) => _writer = writer;

        public void Dispose()
        {
            _writer._indentLevel--;
            _writer._sb.AppendLine($"{_writer.Indent}}}");
        }
    }
}
