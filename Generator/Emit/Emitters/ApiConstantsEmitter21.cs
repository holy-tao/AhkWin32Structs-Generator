namespace AhkWin32.Generator.Emit.Emitters;

using AhkWin32.Generator.Metadata;
using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Types;

/// <summary>
/// Emits a v2.1 ApiType's constants as a separate Constants.ahk file alongside Apis.ahk.
/// In v2.1, module-scope <c>export global</c> values load eagerly when the module is
/// imported, so constants are split out and users opt in by importing Constants.ahk
/// explicitly.
/// </summary>
public sealed class ApiConstantsEmitter21 : ITypeEmitter
{
    public bool CanEmit(Win32Type type) => type is ApiType { Constants.Count: > 0 };

    public EmitResult Emit(Win32Type type, string outputRoot)
    {
        var apiType = (ApiType)type;
        var w = new AhkWriter(AhkVersion.v21);

        EmitConstantsFile(w, apiType);

        string filePath = ImportResolver.GetFilePath(outputRoot, apiType.Namespace, "Constants");
        return new EmitResult(w.ToString(), filePath);
    }

    private static void EmitConstantsFile(AhkWriter w, ApiType apiType)
    {
        string pathToBase = ImportResolver.GetPathToBase(apiType.Namespace);
        w.Require("AutoHotkey >= v2.1-alpha.24+ 64-bit");

        if (HasHandleConstant(apiType))
            w.Import($"{pathToBase}Win32Handle.ahk", ["Win32Handle"]);

        if (apiType.NeedsGuid)
            w.Import($"{pathToBase}Guid.ahk", ["Guid"]);

        EmitImports(w, apiType);

        w.BlankLine();
        DocCommentWriter.WriteTypeDoc(w, apiType);
        w.BlankLine();

        w.RawLine(";@region Constants");
        foreach (var constant in apiType.Constants)
        {
            w.BlankLine();
            ConstantEmitter.EmitConstant21(w, constant);
        }
        w.RawLine(";@endregion Constants");
    }

    private static void EmitImports(AhkWriter w, ApiType apiType)
    {
        var seen = new HashSet<string>();
        foreach (string fqn in apiType.Constants.SelectMany(c => c.Imports.GetTypes()))
        {
            if (!seen.Add(fqn))
                continue;
            string path = ImportResolver.GetIncludePath(apiType.Namespace, fqn);
            w.Import(path, [ImportResolver.GetImportName(fqn)]);
        }
    }

    private static bool HasHandleConstant(ApiType apiType)
        => apiType.Constants.Any(c => c.Value is StructConstantValue { IsHandle: true });
}
