namespace AhkWin32.Generator.Emit;

using AhkWin32.Generator.Model.Members;
using AhkWin32.Generator.Model.Types;

/// <summary>
/// Generates JSDoc-style documentation comments for AHK output files.
/// </summary>
public static class DocCommentWriter
{
    /// <summary>
    /// Write a type-level JSDoc comment.
    /// Port of AhkType.MaybeAddTypeDocumentation.
    /// </summary>
    public static void WriteTypeDoc(AhkWriter w, Win32Type type)
    {
        w.Line("/**");

        if (!string.IsNullOrWhiteSpace(type.Description))
        {
            w.Line($" * {EscapeDocs(type.Description)}");

            if (!string.IsNullOrWhiteSpace(type.Remarks))
            {
                w.Line(" * @remarks");
                w.Line($" * {EscapeDocs(type.Remarks, "")}");
            }
        }

        if (type.HelpLink != null)
            w.Line($" * @see {type.HelpLink}");

        w.Line($" * @namespace {type.Namespace}");
        w.Line($" * @version {type.MetadataVersion}");

        if (type.IsAnsi)
            w.Line(" * @charset ANSI");
        if (type.IsUnicode)
            w.Line(" * @charset Unicode");

        if (type.IsDeprecated)
        {
            string deprecatedTag =  !string.IsNullOrWhiteSpace(type.DeprecationMessage)
                ? $" * @deprecated {type.DeprecationMessage}"
                : " * @deprecated";
            w.Line(deprecatedTag);
        }

        w.Line(" */");
    }

    /// <summary>
    /// Write a constant-level JSDoc comment.
    /// Based on AhkConstant.AppendDocumentation, with deprecation message fix.
    /// </summary>
    public static void WriteConstantDoc(AhkWriter w, ConstantMember constant)
    {
        w.Line("/**");

        if (!string.IsNullOrWhiteSpace(constant.Description))
            w.Line($" * {EscapeDocs(constant.Description, new string(' ', w.CurrentIndentLevel * 4))}");

        if (constant.IsDeprecated)
        {
            string deprecatedTag =  !string.IsNullOrWhiteSpace(constant.DeprecationMessage)
                ? $" * @deprecated {constant.DeprecationMessage}"
                : " * @deprecated";
            w.Line(deprecatedTag);
        }

        w.Line($" * @type {{{constant.Value.AhkTypeName}}}");
        w.Line(" */");
    }

    /// <summary>
    /// Escape documentation content for JSDoc comments.
    /// Replaces block comment markers and converts newlines to continuation lines.
    /// Port of AhkType.EscapeDocs.
    /// </summary>
    public static string? EscapeDocs(string? docString, string? indent = " ")
    {
        return docString?
            .Replace("/*", "//")
            .Replace("*/", "")
            .Replace("\n", $"\n{indent} * ");
    }
}
