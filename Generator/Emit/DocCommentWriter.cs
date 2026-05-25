namespace AhkWin32.Generator.Emit;

using AhkWin32.Generator.Metadata;
using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Members;
using AhkWin32.Generator.Model.Types;

/// <summary>
/// Generates JSDoc-style documentation comments for AHK output files.
/// </summary>
public static class DocCommentWriter
{
    /// <summary>
    /// Doc string for the variadic argument for variadic args
    /// </summary>
    private const string VAR_ARGS_DOC =
        "Additional arguments as alternating DllCall type/value pairs (e.g., \"int\", 42, \"str\", \"hello\")";

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
        // w.Line($" * @version {type.MetadataVersion}");

        if (type.IsAnsi)
            w.Line(" * @charset ANSI");
        if (type.IsUnicode)
            w.Line(" * @charset Unicode");

        if (type.Arch is not Architecture.All or Architecture.None)
            w.Line($" * @architecture {type.Arch}");

        if (type.IsDeprecated)
        {
            string deprecatedTag = !string.IsNullOrWhiteSpace(type.DeprecationMessage)
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
            string deprecatedTag = !string.IsNullOrWhiteSpace(constant.DeprecationMessage)
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
    public static void WriteFieldDoc(AhkWriter w, FieldMember field, AhkVersion version = AhkVersion.v20)
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

        // No type doc for v2.1 struct fields since they carry type info in their
        // type specifiers
        if (version is AhkVersion.v20)
        {
            string typeName = field.EmbeddedStruct is not null ? field.EmbeddedStruct.Name : field.Type.DisplayName;
            w.Line($" * @type {{{typeName}}}");
        }
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

        string typeName = parent.EmbeddedStruct is not null ? parent.EmbeddedStruct.Name : parent.Type.DisplayName;
        w.Line($" * @type {{{typeName}}}");
        w.Line(" */");
    }

    /// <summary>
    /// Write a COM property JSDoc comment.
    /// Port of AhkComProperty.MaybeAppendDocumentation.
    /// </summary>
    public static void WritePropertyDoc(AhkWriter w, ComPropertyMember prop)
    {
        w.Line("/**");

        if (!string.IsNullOrWhiteSpace(prop.Description))
        {
            w.Line($" * {EscapeDocs(prop.Description, new string(' ', w.CurrentIndentLevel * 4))}");
        }

        // Type is getter's output param type if getter exists, otherwise setter's first non-reserved param type
        string? typeName = null;
        if (prop.Getter?.OutputParameter is { } getterOut)
        {
            typeName = getterOut.IsPtr ? getterOut.Pointee?.DisplayName : getterOut.Type.DisplayName;
        }
        else if (prop.Setter is { } setter)
        {
            var firstParam = setter.Parameters.Skip(1).FirstOrDefault(p => !p.IsReserved);
            if (firstParam is not null)
            {
                typeName = firstParam.IsPtr ? firstParam.Pointee?.DisplayName : firstParam.Type.DisplayName;
            }
        }

        if (!string.IsNullOrWhiteSpace(typeName))
        {
            w.Line($" * @type {{{typeName}}} ");
        }

        w.Line(" */");
    }

    /// <summary>
    /// Write a method-level JSDoc comment.
    /// Port of AhkMethod.MaybeAppendDocumentation.
    /// </summary>
    public static void WriteMethodDoc(AhkWriter w, MethodMember method)
    {
        string indent = new(' ', w.CurrentIndentLevel * 4);

        w.Line("/**");
        w.Line($" * {EscapeDocs(method.Description, indent)}");

        if (!string.IsNullOrWhiteSpace(method.Remarks))
        {
            w.Line(" * @remarks");
            w.Line($" * {EscapeDocs(method.Remarks, indent)}");
        }

        // @param tags — skip reserved and output parameters
        for (int i = 1; i < method.Parameters.Count; i++)
        {
            ParameterMember param = method.Parameters[i];
            if (param.IsReserved || param == method.OutputParameter)
                continue;

            string paramDoc = !string.IsNullOrWhiteSpace(param.Description)
                ? EscapeDocs(param.Description, indent) ?? ""
                : "";
            w.Line($" * @param {{{param.Type.DisplayName}}} {param.Name} {paramDoc}");
        }

        if (method.IsVariadic)
            w.Line($" * @param {{Any}} {method.VariadicParamName}* {VAR_ARGS_DOC}");

        // @returns tag
        if (method.HasReturnValue || method.OutputParameter != null)
        {
            if (method.OutputParameter is { } outParam)
            {
                string? returnTypeName = outParam.IsPtr ? outParam.Pointee?.DisplayName : outParam.Type.DisplayName;
                string returnDoc = !string.IsNullOrWhiteSpace(outParam.Description)
                    ? EscapeDocs(outParam.Description, indent) ?? ""
                    : "";
                w.Line($" * @returns {{{returnTypeName}}} {returnDoc}");
            }
            else
            {
                w.Line(
                    $" * @returns {{{method.Parameters[0].Type.DisplayName}}} {EscapeDocs(method.ReturnValueDoc, indent)}"
                );
            }
        }
        else
        {
            w.Line(" * @returns {String} Nothing - always returns an empty string");
        }

        if (method.HelpLink != null)
            w.Line($" * @see {method.HelpLink}");

        if (method.CharSet == StringEncoding.Ansi)
            w.Line(" * @charset ANSI");
        if (method.CharSet == StringEncoding.Unicode)
            w.Line(" * @charset Unicode");

        if (!string.IsNullOrWhiteSpace(method.DeprecationMessage))
            w.Line($" * @deprecated {method.DeprecationMessage}");

        if (!string.IsNullOrWhiteSpace(method.SupportedOSPlatform))
            w.Line($" * @since {method.SupportedOSPlatform}");

        w.Line(" */");
    }

    /// <summary>
    /// Escape documentation content for JSDoc comments.
    /// Replaces block comment markers and converts newlines to continuation lines.
    /// Port of AhkType.EscapeDocs.
    /// </summary>
    public static string? EscapeDocs(string? docString, string? indent = " ")
    {
        return docString?.Replace("/*", "//").Replace("*/", "").Replace("\n", $"\n{indent} * ");
    }
}
