namespace AhkWin32.Generator.Emit.Emitters;

using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Types;
using AhkWin32.Generator.Metadata;

/// <summary>
/// Emits a v2.1 ApiType as a complete .ahk file.
/// TODO: consider moving constants to their own file since we cannot lazy-load them as fat arrow functions anymore
/// </summary>
public sealed class ApiTypeEmitter21(TypeRegistry registry) : ITypeEmitter
{
    private readonly TypeRegistry _registry = registry;

    public bool CanEmit(Win32Type type) => type is ApiType;

    public EmitResult Emit(Win32Type type, string outputRoot)
    {
        var apiType = (ApiType)type;
        var w = new AhkWriter(AhkVersion.v21);

        EmitApiType(w, apiType);

        string filePath = ImportResolver.GetFilePath(outputRoot, apiType.Namespace, apiType.CanonicalName);
        return new EmitResult(w.ToString(), filePath);
    }

    private void EmitApiType(AhkWriter w, ApiType apiType)
    {
        // Directives
        string pathToBase = ImportResolver.GetPathToBase(apiType.Namespace);
        w.Require("AutoHotkey >= v2.1-alpha.24+ 64-bit");
        w.Import($"{pathToBase}Win32Handle.ahk", ["Win32Handle"]);

        if (apiType.NeedsGuid)
            w.Import($"{pathToBase}Guid.ahk", ["Guid"]);

        // Referenced type imports (from constants, methods, and extensions)
        EmitImports(w, apiType);

        w.BlankLine();

        // Type documentation
        DocCommentWriter.WriteTypeDoc(w, apiType);

         w.BlankLine();

        // Constants region
        EmitConstants(w, apiType);

        w.BlankLine();

        // Functions region
        EmitFunctions(w, apiType);
    }

    private static void EmitImports(AhkWriter w, ApiType apiType)
    {
        foreach (string fqn in apiType.Imports.GetTypes())
        {
            string path = ImportResolver.GetIncludePath(apiType.Namespace, fqn);
            w.Import(path, [ImportResolver.GetImportName(fqn)]);
        }

        foreach (string apisFqn in apiType.Imports.GetFunctionNamespaces())
        {
            string path = ImportResolver.GetIncludePath(apiType.Namespace, apisFqn);
            w.Import(path, apiType.Imports.GetFunctionsForNamespace(apisFqn));
        }
    }

    private static void EmitConstants(AhkWriter w, ApiType apiType)
    {
        // Region markers are at column 0 (matching legacy format)
        w.RawLine(";@region Constants");

        foreach (var constant in apiType.Constants)
        {
            w.BlankLine();
            ConstantEmitter.EmitConstant21(w, constant);
        }

        w.RawLine(";@endregion Constants");
    }

    private void EmitFunctions(AhkWriter w, ApiType apiType)
    {
        w.RawLine(";@region Functions");

        foreach (var method in apiType.Methods)
        {
            MethodEmitter.EmitDllImportFunction(w, method, _registry);
            w.BlankLine();
        }

        w.RawLine(";@endregion Functions");
    }
}
