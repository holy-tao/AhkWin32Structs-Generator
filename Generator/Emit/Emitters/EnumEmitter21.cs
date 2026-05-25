namespace AhkWin32.Generator.Emit.Emitters;

using AhkWin32.Generator.Metadata;
using AhkWin32.Generator.Model.Types;

/// <summary>
/// Emits an EnumType as a v2.1 native <c>struct</c>. The struct carries a
/// <c>value</c> field plus <c>__value</c> get/set so an instance of the enum class
/// can be used as a typed DllCall parameter; AHK coerces an incoming int via the
/// <c>__value</c> setter. The enum's constants remain plain integer static
/// properties (matching v2.0), so bitflag OR-ing and equality comparisons against
/// constants keep working naturally.
///
/// IsFlags enums use the same shape - operator overloading isn't available on
/// structs in v2.1, so there's no benefit to a separate code path.
/// </summary>
public sealed class EnumEmitter21 : ITypeEmitter
{
    public bool CanEmit(Win32Type type) => type is EnumType;

    public EmitResult Emit(Win32Type type, string outputRoot)
    {
        var enumType = (EnumType)type;
        var w = new AhkWriter(AhkVersion.v21);

        EmitEnum(w, enumType);

        string filePath = ImportResolver.GetFilePath(outputRoot, enumType.Namespace, enumType.CanonicalName);
        return new EmitResult(w.ToString(), filePath);
    }

    private static void EmitEnum(AhkWriter w, EnumType enumType)
    {
        w.Require("AutoHotkey v2.1-alpha.26+ 64-bit");

        EmitImports(w, enumType);

        w.BlankLine();

        DocCommentWriter.WriteTypeDoc(w, enumType);

        using (w.Struct(enumType.Name))
        {
            w.Line($"value : {enumType.UnderlyingTypeName}");

            w.BlankLine();
            using (w.InstanceProperty("__value"))
            {
                w.Line("get => this.value");
                w.Line("set => this.value := value");
            }

            w.BlankLine();
            using (w.InstanceMethod("__New", "value := 0"))
            {
                w.Line("this.value := value");
            }

            foreach (var constant in enumType.Constants)
            {
                w.BlankLine();
                ConstantEmitter.EmitConstant(w, constant);
            }

            StructEmitter21.EmitExtensions(w, enumType);
        }
    }

    private static void EmitImports(AhkWriter w, EnumType enumType)
    {
        foreach (string fqn in enumType.Imports.GetTypes().Where(fqn => fqn != enumType.FQN))
        {
            string path = ImportResolver.GetIncludePath(enumType.Namespace, fqn);
            w.Import(path, [ImportResolver.GetImportName(fqn)]);
        }

        foreach (string apisFqn in enumType.Imports.GetFunctionNamespaces())
        {
            string path = ImportResolver.GetIncludePath(enumType.Namespace, apisFqn);
            w.Import(path, enumType.Imports.GetFunctionsForNamespace(apisFqn));
        }
    }
}
