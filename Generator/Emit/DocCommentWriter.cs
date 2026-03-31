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
    /// Write a field-level JSDoc comment.
    /// Port of AhkStructMember.MaybeAppendDocumentation.
    /// </summary>
    public static void WriteFieldDoc(AhkWriter w, FieldMember field)
    {
        w.Line("/**");

        if (!string.IsNullOrWhiteSpace(field.Description))
            w.Line($" * {EscapeDocs(field.Description, new string(' ', w.CurrentIndentLevel * 4))}");

        if (field.IsBitField)
        {
            w.Line(" * This bitfield backs the following members:");
            foreach (var bf in field.Bitfields)
                w.Line($" * - {bf.Name}");
        }

        if (field.IsDeprecated)
        {
            string deprecatedTag = !string.IsNullOrWhiteSpace(field.DeprecationMessage)
                ? $" * @deprecated {field.DeprecationMessage}"
                : " * @deprecated";
            w.Line(deprecatedTag);
        }

        string typeName = field.EmbeddedStruct is not null
            ? field.EmbeddedStruct.Name
            : field.Type.DisplayName;
        w.Line($" * @type {{{typeName}}}");
        w.Line(" */");
    }

    /// <summary>
    /// Write a bitfield member JSDoc comment.
    /// Port of AhkStructMember.AppendBitfieldMember documentation portion.
    /// </summary>
    public static void WriteBitfieldDoc(AhkWriter w, FieldMember parent, BitfieldMember bitfield, string? description)
    {
        w.Line("/**");

        if (!string.IsNullOrWhiteSpace(description))
            w.Line($" * {EscapeDocs(description, new string(' ', w.CurrentIndentLevel * 4))}");

        string typeName = parent.EmbeddedStruct is not null
            ? parent.EmbeddedStruct.Name
            : parent.Type.DisplayName;
        w.Line($" * @type {{{typeName}}}");
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
