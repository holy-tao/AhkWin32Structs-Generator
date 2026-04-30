namespace AhkWin32.Generator.Emit.Emitters;

using AhkWin32.Generator.Metadata;
using AhkWin32.Generator.Model.Members;
using AhkWin32.Generator.Model.Types;
using YamlDotNet.Core.Tokens;


/// <summary>
/// Emits a HandleType as a v2.1 native `struct` block. Handles are emitted as a
/// single-field struct with `__value` get/set so the instance is transparently
/// usable as the underlying integer/pointer in DllCall and assignment.
///
/// `Free()` is emitted from <see cref="HandleType.FreeFunc"/> metadata but is NOT
/// wired into `__delete` - auto-cleanup is the caller's responsibility for now.
/// </summary>
public sealed class HandleEmitter21 : ITypeEmitter
{
    public bool CanEmit(Win32Type type) => type is HandleType;

    public EmitResult Emit(Win32Type type, string outputRoot)
    {
        var handleType = (HandleType)type;
        var w = new AhkWriter(AhkVersion.v21);

        EmitHandle(w, handleType);

        string filePath = ImportResolver.GetFilePath(outputRoot, handleType.Namespace, handleType.CanonicalName);
        return new EmitResult(w.ToString(), filePath);
    }

    private static void EmitHandle(AhkWriter w, HandleType handleType)
    {
        w.Require("AutoHotkey v2.1-alpha.26+ 64-bit");

        EmitImports(w, handleType);

        w.BlankLine();

        DocCommentWriter.WriteTypeDoc(w, handleType);

        FieldMember valueField = handleType.Members.Single();
        long firstInvalid = handleType.InvalidValues.FirstOrDefault(0);

        using (w.Struct(handleType.Name))
        {
            w.Line($"{valueField.Name} : {valueField.Type.TypeSpecifier}");

            w.BlankLine();
            using (w.InstanceProperty("__value"))
            {
                w.Line($"get => this.{valueField.Name}");
                w.Line($"set => this.{valueField.Name} := value");
            }

            w.BlankLine();
            w.Line("/**");
            w.Line(" * The list of values which indicate that the handle is invalid");
            w.Line(" * @type {Array<Integer>}");
            w.Line(" */");
            w.StaticField("invalidValues", $"[{string.Join(", ", handleType.InvalidValues)}]");

            w.BlankLine();
            using (w.InstanceMethod("__New", $"{valueField.Name} := {firstInvalid}"))
            {
                w.Line($"this.{valueField.Name} := {valueField.Name}");
            }

            // Extension code blocks (e.g. helper methods from metadata/extensions/*.yml)
            StructEmitter21.EmitExtensions(w, handleType);

            // Free() is emitted but NOT auto-invoked via __delete.
            if (handleType.FreeFunc is not null)
            {
                w.BlankLine();
                w.Line("Free() {");
                w.Line($"    {handleType.FreeFunc.Name}(this.{valueField.Name})");
                w.Line($"    this.{valueField.Name} := {firstInvalid}");
                w.Line("}");
            }
        }
    }

    private static void EmitImports(AhkWriter w, Win32Type type)
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
