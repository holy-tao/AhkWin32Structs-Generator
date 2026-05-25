namespace AhkWin32.Generator.Emit;

using System.Text;
using AhkWin32.Generator.Metadata;

/// <summary>
/// Indentation-aware text writer for generating AutoHotkey v2 source files.
/// Wraps a StringBuilder with indent tracking and convenience methods for
/// common AHK constructs (classes, properties, static fields, etc.).
/// </summary>
public sealed class AhkWriter(AhkVersion version = AhkVersion.v20)
{
    private readonly StringBuilder _sb = new(4096);
    private int _indentLevel;
    private const int IndentSize = 4;

    private string Indent => new(' ', _indentLevel * IndentSize);

    // --- Directives (always at column 0) ---

    /// <summary>Write a #Requires directive.</summary>
    public void Require(string version) => _sb.AppendLine($"#Requires {version}");

    /// <summary>Write a #Include directive.</summary>
    public void Include(string path) => _sb.AppendLine($"#Include {path}");

    /// <summary>
    /// Write an #Import directive using a path-qualified name - https://www.autohotkey.com/docs/alpha/lib/_Import.htm.
    /// Requires v2.1-alpha.21+
    /// </summary>
    public void Import(string path, IEnumerable<string> names)
    {
        _sb.Append($"#Import \"{path}\"");
        if (names.Any())
        {
            _sb.Append($" {{ {string.Join(", ", names)} }}");
        }
        _sb.AppendLine();
    }

    public void Import(string path) => Import(path, []);

    /// <summary>
    /// Write an #Import directive using a path-qualified name and an alias
    /// </summary>
    public void ImportAs(string path, string alias, IEnumerable<string> names)
    {
        _sb.Append($"#Import \"{path}\", as {alias}");
        if (names.Any())
        {
            _sb.Append($" {{ {string.Join(", ", names)} }}");
        }
        _sb.AppendLine();
    }

    public void ImportAs(string path, string alias) => ImportAs(path, alias, []);

    /// <summary>
    /// Write an #Import export directive using a path-qualified name - https://www.autohotkey.com/docs/alpha/lib/_Import.htm.
    /// Requires v2.1-alpha.24+ if using a wildcard import, otherwise v2.1-alpha.21+
    /// </summary>
    public void ReExport(string path, IEnumerable<string> names)
    {
        _sb.Append($"#Import export \"{path}\"");
        if (names.Any())
        {
            _sb.Append($" {{{string.Join(", ", names)}}}");
        }
        _sb.AppendLine();
    }

    public void ReExport(string path) => ReExport(path, []);

    /// <summary>
    /// Write a #Module statement.
    /// </summary>
    public void Module(string name) => _sb.AppendLine($"#Module {name}");

    // --- Structure ---

    /// <summary>Write an empty line.</summary>
    public void BlankLine() => _sb.AppendLine();

    /// <summary>
    /// Open a class block. Returns an IDisposable that writes the closing brace on Dispose.
    /// Format: "class NAME extends BASE {" or "class NAME {" (always space before brace).
    /// Supports nesting — indent level is always relative.
    ///
    /// If writer is for v2.1, the class is exported as the default export. This means there can only
    /// be one class per module / file.
    /// </summary>
    public IndentScope Class(string name, string? extends = null)
    {
        string header = extends != null ? $"class {name} extends {extends}" : $"class {name}";

        if (version is AhkVersion.v21)
            header = $"export default {header}";

        _sb.AppendLine($"{Indent}{header} {{");
        _indentLevel++;
        return new IndentScope(this);
    }

    /// <summary>
    /// Open a struct block (v2.1 native struct). Returns an IDisposable that writes
    /// the closing brace on Dispose. Format: "struct NAME {" or "struct NAME extends BASE {".
    /// `struct` defaults to extending Struct, so an explicit extends is only needed when
    /// inheriting from another struct subclass.
    /// </summary>
    public IndentScope Struct(string name, string? extends = null)
    {
        string header = extends != null ? $"struct {name} extends {extends}" : $"struct {name}";

        if (version is AhkVersion.v21 && _indentLevel == 0)
            header = $"export default {header}";

        _sb.AppendLine($"{Indent}{header} {{");
        _indentLevel++;
        return new IndentScope(this);
    }

    /// <summary>
    /// Open a top-level function.
    /// </summary>
    public IndentScope Function(string name, string args = "")
    {
        _sb.AppendLine(
            version switch
            {
                AhkVersion.v20 => $"{Indent}{name}({args}) {{",
                AhkVersion.v21 => $"{Indent}export {name}({args}) {{",
                _ => throw new NotImplementedException($"Unsupported AHK version {version.ToFriendlyString()}"),
            }
        );

        _indentLevel++;
        return new IndentScope(this);
    }

    /// <summary>
    /// Write a top-level variable, like [export global] foo := "bar". If version supports it, variable is exported.
    ///
    /// Does *not* open a new indentation level. Exports require v2.1-alpha.21+
    /// </summary>
    /// <exception cref="NotImplementedException"></exception>
    public void Variable(string name, string value)
    {
        _sb.AppendLine(
            version switch
            {
                AhkVersion.v20 => $"{Indent}{name} := {value}",
                AhkVersion.v21 => $"{Indent}export global {name} := {value}",
                _ => throw new NotImplementedException($"Unsupported AHK version {version.ToFriendlyString()}"),
            }
        );
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
    /// Open a static method block: "static Name(args) {".
    /// </summary>
    public IndentScope StaticMethod(string name, string args)
    {
        _sb.AppendLine($"{Indent}static {name}({args}) {{");
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
    /// Open an instance method block: "Name(args) {".
    /// Used for COM interface methods (non-static).
    /// </summary>
    public IndentScope InstanceMethod(string name, string args)
    {
        _sb.AppendLine($"{Indent}{name}({args}) {{");
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
    public void StaticField(string name, string expr) => _sb.AppendLine($"{Indent}static {name} => {expr}");

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
