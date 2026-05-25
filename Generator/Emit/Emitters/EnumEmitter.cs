namespace AhkWin32.Generator.Emit.Emitters;

using AhkWin32.Generator.Model.Types;

/// <summary>
/// Emits EnumType as a complete .ahk file.
/// Port of legacy AhkEnum.ToAhk().
/// </summary>
public sealed class EnumEmitter : ITypeEmitter
{
    public bool CanEmit(Win32Type type) => type is EnumType;

    public EmitResult Emit(Win32Type type, string outputRoot)
    {
        var enumType = (EnumType)type;
        var w = new AhkWriter();

        EmitEnum(w, enumType);

        string filePath = ImportResolver.GetFilePath(outputRoot, enumType.Namespace, enumType.CanonicalName);
        return new EmitResult(w.ToString(), filePath);
    }

    private static void EmitEnum(AhkWriter w, EnumType enumType)
    {
        // Directives
        string pathToBase = ImportResolver.GetPathToBase(enumType.Namespace);
        w.Require("AutoHotkey v2.0.0 64-bit");
        w.Include($"{pathToBase}Win32Enum.ahk");

        // Extension imports
        EmitImports(w, enumType);

        w.BlankLine();

        // Type documentation
        DocCommentWriter.WriteTypeDoc(w, enumType);

        // Class declaration
        string baseClass = enumType.IsFlags ? "Win32BitflagEnum" : "Win32Enum";
        using (w.Class(enumType.Name, baseClass))
        {
            // Constants
            foreach (var constant in enumType.Constants)
            {
                w.BlankLine();
                ConstantEmitter.EmitConstant(w, constant);
            }

            // Extensions
            EmitExtensions(w, enumType);
        }
    }

    private static void EmitImports(AhkWriter w, EnumType enumType)
    {
        // Enum imports come only from extensions
        foreach (string import in enumType.Imports.GetIncludeTargets())
        {
            w.Include(ImportResolver.GetIncludePath(enumType.Namespace, import));
        }
    }

    private static void EmitExtensions(AhkWriter w, EnumType enumType)
    {
        if (enumType.Extensions.Count == 0)
            return;

        foreach (var ext in enumType.Extensions)
        {
            // Replace tokens (only $Class for now)
            string code = ext.Code.Replace("$Class", enumType.Name);

            // Indent extension code to current level (inside class body)
            string indentStr = w.CurrentIndent;
            string indented = indentStr + code.Replace("\n", "\n" + indentStr);
            w.RawLine(indented);
        }
    }
}
