namespace AhkWin32.Generator.Emit.Emitters;

using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Types;

/// <summary>
/// Emits ApiType as a complete .ahk file.
/// Port of legacy AhkApiType.ToAhk().
/// </summary>
public sealed class ApiTypeEmitter : ITypeEmitter
{
    private readonly TypeRegistry _registry;

    public ApiTypeEmitter(TypeRegistry registry)
    {
        _registry = registry;
    }

    public bool CanEmit(Win32Type type) => type is ApiType;

    public EmitResult Emit(Win32Type type, string outputRoot)
    {
        var apiType = (ApiType)type;
        var w = new AhkWriter();

        EmitApiType(w, apiType);

        string filePath = ImportResolver.GetFilePath(outputRoot, apiType.Namespace, apiType.CanonicalName);
        return new EmitResult(w.ToString(), filePath);
    }

    private void EmitApiType(AhkWriter w, ApiType apiType)
    {
        // Directives
        string pathToBase = ImportResolver.GetPathToBase(apiType.Namespace);
        w.Require("AutoHotkey v2.0.0 64-bit");
        w.Include($"{pathToBase}Win32Handle.ahk");

        if (apiType.NeedsGuid)
            w.Include($"{pathToBase}Guid.ahk");

        // Referenced type imports (from constants, methods, and extensions)
        EmitImports(w, apiType);

        w.BlankLine();

        // Type documentation
        DocCommentWriter.WriteTypeDoc(w, apiType);

        // Class declaration — ApiType uses last namespace segment as class name
        using (w.Class(apiType.DisplayName))
        {
            w.BlankLine();

            // Constants region
            EmitConstants(w, apiType);

            w.BlankLine();

            // Methods region
            EmitMethods(w, apiType);
        }
    }

    private static void EmitImports(AhkWriter w, ApiType apiType)
    {
        foreach (string import in apiType.ReferencedTypes.Distinct())
        {
            w.Include(ImportResolver.GetIncludePath(apiType.Namespace, import));
        }
    }

    private static void EmitConstants(AhkWriter w, ApiType apiType)
    {
        // Region markers are at column 0 (matching legacy format)
        w.RawLine(";@region Constants");

        foreach (var constant in apiType.Constants)
        {
            w.BlankLine();
            ConstantEmitter.EmitConstant(w, constant);
        }

        w.RawLine(";@endregion Constants");
    }

    private void EmitMethods(AhkWriter w, ApiType apiType)
    {
        w.RawLine(";@region Methods");

        foreach (var method in apiType.Methods)
        {
            MethodEmitter.EmitDllImportMethod(w, method, _registry);
            w.BlankLine();
        }

        w.RawLine(";@endregion Methods");
    }
}
