namespace AhkWin32.Generator.Emit.Emitters;

using AhkWin32.Generator.Model.Types;

/// <summary>
/// Emits HandleType as a complete .ahk file.
/// Port of legacy AhkHandle.ToAhk().
/// Delegates body emission to StructEmitter shared methods.
/// </summary>
public sealed class HandleEmitter : ITypeEmitter
{
    public bool CanEmit(Win32Type type) => type is HandleType;

    public EmitResult Emit(Win32Type type, string outputRoot)
    {
        var handleType = (HandleType)type;
        var w = new AhkWriter();

        EmitHandle(w, handleType);

        string filePath = ImportResolver.GetFilePath(outputRoot, handleType.Namespace, handleType.CanonicalName);
        return new EmitResult(w.ToString(), filePath);
    }

    private static void EmitHandle(AhkWriter w, HandleType handleType)
    {
        string pathToBase = ImportResolver.GetPathToBase(handleType.Namespace);

        // Headers — Win32Handle.ahk comes after imports (matching legacy ordering)
        w.Require("AutoHotkey v2.0.0 64-bit");
        w.Include($"{pathToBase}Win32Struct.ahk");

        EmitImports(w, handleType);

        w.Include($"{pathToBase}Win32Handle.ahk");

        w.BlankLine();

        DocCommentWriter.WriteTypeDoc(w, handleType);

        using (w.Class(handleType.Name, "Win32Handle"))
        {
            w.StaticField("sizeof", handleType.Size.ToString());
            w.BlankLine();
            w.StaticField("packingSize", handleType.PackingSize.ToString());

            // Invalid values
            w.BlankLine();
            w.Line("/**");
            w.Line(" * The list of values which indicate that the handle is invalid");
            w.Line(" * @type {Array<Integer>}");
            w.Line(" */");
            w.StaticField("invalidValues", $"[{string.Join(", ", handleType.InvalidValues)}]");

            // Body (member properties, extensions, __New)
            StructEmitter.EmitBody(w, handleType, 0, [], handleType.Name);

            // Free destructor
            if (handleType.FreeFunc is not null && handleType.Members.Count > 0)
            {
                w.BlankLine();
                string firstMemberName = handleType.Members[0].Name;
                long firstInvalidValue = handleType.InvalidValues.FirstOrDefault(0);

                w.Line($"Free(){{");
                w.Line($"    {handleType.FreeFunc.DeclarerName}.{handleType.FreeFunc.Name}(this.{firstMemberName})");
                w.Line($"    this.{firstMemberName} := {firstInvalidValue}");
                w.Line("}");
            }
        }
    }

    private static void EmitImports(AhkWriter w, Win32Type type)
    {
        foreach (string import in type.ReferencedTypes.Distinct())
        {
            w.Include(ImportResolver.GetIncludePath(type.Namespace, import));
        }
    }
}
