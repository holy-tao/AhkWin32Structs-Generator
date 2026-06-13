namespace AhkWin32.Generator.Emit.Emitters;

using AhkWin32.Generator.Metadata;
using AhkWin32.Generator.Model.Types;

/// <summary>
/// Emits a NativeTypedefType as a v2.1 native `struct` block. Typedefs are emitted
/// as a single-field struct with `__value` get/set so the instance is transparently
/// usable as the underlying value in DllCall and assignment.
///
/// Mirrors the docs example:
/// <code>
/// struct BOOL {
///     value : Int32
///     __value {
///         get => this.value
///         set => this.value := value
///     }
/// }
/// </code>
/// </summary>
public sealed class NativeTypedefEmitter21 : ITypeEmitter
{
    public bool CanEmit(Win32Type type) => type is NativeTypedefType;

    public EmitResult Emit(Win32Type type, string outputRoot)
    {
        var typedef = (NativeTypedefType)type;
        var w = new AhkWriter(AhkVersion.v21);

        EmitTypedef(w, typedef);

        string filePath = ImportResolver.GetFilePath(outputRoot, typedef.Namespace, typedef.CanonicalName);
        return new EmitResult(w.ToString(), filePath);
    }

    private static void EmitTypedef(AhkWriter w, NativeTypedefType typedef)
    {
        w.Require("AutoHotkey v2.1-alpha.26+ 64-bit");

        EmitImports(w, typedef);

        w.BlankLine();

        DocCommentWriter.WriteTypeDoc(w, typedef);

        using (w.Struct(typedef.Name))
        {
            w.Line($"value : {typedef.Underlying.TypeSpecifier}");

            w.BlankLine();
            using (w.InstanceProperty("__value"))
            {
                using (w.SetBlock())
                {
                    using (w.If($"value is {typedef.Name}"))
                    {
                        w.Line($"this.value := value.value");
                    }
                    using (w.Else())
                    {
                        w.Line($"this.value := value");
                    }
                }
            }

            w.BlankLine();
            using (w.InstanceMethod("__New", "value := 0"))
            {
                w.Line("this.value := value");
            }

            // Extension code blocks (e.g. NTSTATUS helper methods)
            StructEmitter21.EmitExtensions(w, typedef);
        }
    }

    private static void EmitImports(AhkWriter w, NativeTypedefType type)
    {
        foreach (string fqn in type.Imports.GetTypes().Where(fqn => fqn != type.FQN))
        {
            string path = ImportResolver.GetIncludePath(type.Namespace, fqn);
            w.Import(path, [ImportResolver.GetImportName(fqn)]);
        }

        foreach (string apisFqn in type.Imports.GetFunctionNamespaces())
        {
            string path = ImportResolver.GetIncludePath(type.Namespace, apisFqn);
            w.Import(path, type.Imports.GetFunctionsForNamespace(apisFqn));
        }
    }
}
