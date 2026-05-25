namespace AhkWin32.Generator.Emit.Emitters;

using AhkWin32.Generator.Metadata;
using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Types;

/// <summary>
/// Emits a v2.1 ApiType's free functions as a complete .ahk file.
/// Constants are emitted separately by <see cref="ApiConstantsEmitter21"/> so users
/// can opt in to loading them (v2.1 module-scope globals are not lazy).
/// </summary>
public sealed class ApiTypeEmitter21(TypeRegistry registry) : ITypeEmitter
{
    private readonly TypeRegistry _registry = registry;

    public bool CanEmit(Win32Type type) => type is ApiType { Methods.Count: > 0 };

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
        w.BlankLine();

        // Referenced type imports needed by methods (and any extension imports not consumed
        // exclusively by constants).
        EmitImports(w, apiType);

        w.BlankLine();

        // Type documentation
        DocCommentWriter.WriteTypeDoc(w, apiType);

        w.BlankLine();

        // Functions region
        EmitFunctions(w, apiType);
    }

    private static void EmitImports(AhkWriter w, ApiType apiType)
    {
        // Strip imports that are only referenced by constants (those go in Constants.ahk).
        var constantOnlyTypes = new HashSet<string>(GetConstantOnlyTypeImports(apiType));

        foreach (string fqn in apiType.Imports.GetTypes())
        {
            if (constantOnlyTypes.Contains(fqn))
                continue;
            string path = ImportResolver.GetIncludePath(apiType.Namespace, fqn);
            w.Import(path, [ImportResolver.GetImportName(fqn)]);
        }

        foreach (string apisFqn in apiType.Imports.GetFunctionNamespaces())
        {
            string path = ImportResolver.GetIncludePath(apiType.Namespace, apisFqn);
            w.Import(path, apiType.Imports.GetFunctionsForNamespace(apisFqn));
        }
    }

    /// <summary>
    /// Type FQNs referenced by at least one constant but no method. These belong only in
    /// Constants.ahk and should not appear in the functions file's import list.
    /// </summary>
    private static IEnumerable<string> GetConstantOnlyTypeImports(ApiType apiType)
    {
        var methodTypes = new HashSet<string>(apiType.Methods.SelectMany(m => m.Imports.GetTypes()));

        return apiType.Constants.SelectMany(c => c.Imports.GetTypes()).Where(t => !methodTypes.Contains(t)).Distinct();
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
