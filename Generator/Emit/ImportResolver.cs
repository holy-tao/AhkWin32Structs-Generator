namespace AhkWin32.Generator.Emit;

/// <summary>
/// Calculates relative #Include paths between namespaces and types.
/// Port of path calculation logic from legacy AhkType.
/// </summary>
public static class ImportResolver
{
    /// <summary>
    /// Calculate the relative path from a type's namespace directory to the output root.
    /// E.g., "Windows.Win32.Foundation" becomes "..\..\..\".
    /// </summary>
    public static string GetPathToBase(string ns)
    {
        return ns.Split('.').Select(_ => $"..{Path.DirectorySeparatorChar}").Aggregate((agg, cur) => agg + cur);
    }

    /// <summary>
    /// v2.1 folder-module layout: calculate the relative path from a type's namespace
    /// directory up to the module root - the first namespace segment's directory (e.g.
    /// "Windows"), where the hand-written fixtures (Guid, Win32ComInterface, Vector) live.
    /// One level shallower than <see cref="GetPathToBase"/>, because the first namespace
    /// segment IS the module-root directory rather than a level to ascend past.
    /// </summary>
    public static string GetPathToModuleRoot(string ns)
    {
        int depth = ns.Split('.').Length - 1;
        return string.Concat(Enumerable.Repeat($"..{Path.DirectorySeparatorChar}", Math.Max(depth, 0)));
    }

    /// <summary>
    /// Calculate the relative #Include path from one namespace to a type identified by FQN.
    /// When <paramref name="moduleRelative"/> is set (v2.1 folder-module emission), the
    /// hand-written <c>Guid.ahk</c> fixture resolves relative to the module root rather than
    /// the repository root; regular type paths are unaffected.
    /// </summary>
    public static string GetIncludePath(string fromNs, string importFqn, bool moduleRelative = false)
    {
        if (importFqn == "System.Guid")
        {
            string toRoot = moduleRelative ? GetPathToModuleRoot(fromNs) : GetPathToBase(fromNs);
            return $"{toRoot}Guid.ahk";
        }

        int lastDot = importFqn.LastIndexOf('.');
        string importNs = importFqn[..lastDot];
        string importName = importFqn[(lastDot + 1)..];

        string relativePath = RelativePathBetweenNamespaces(fromNs, importNs);
        return $"{relativePath}{importName}.ahk";
    }

    public static string GetImportName(string importFqn)
    {
        int lastDot = importFqn.LastIndexOf('.');
        return importFqn[(lastDot + 1)..];
    }

    /// <summary>
    /// Calculate the relative directory path between two namespace directories.
    /// Port of AhkType.RelativePathBetweenNamespaces.
    /// </summary>
    public static string RelativePathBetweenNamespaces(string fromNs, string toNs)
    {
        if (string.IsNullOrEmpty(toNs))
            return $".{Path.DirectorySeparatorChar}";

        string fromDir = NamespaceToPath(fromNs);
        string toDir = NamespaceToPath(toNs);

        string relativePath = Path.GetRelativePath(fromDir, toDir);
        if (!relativePath.EndsWith(Path.DirectorySeparatorChar))
            relativePath += Path.DirectorySeparatorChar;
        return relativePath;
    }

    /// <summary>
    /// Get the desired output file path for a type.
    /// </summary>
    public static string GetFilePath(string outputRoot, string ns, string canonicalName)
    {
        string namespacePath = Path.Join(ns.Split('.'));
        return Path.Join(outputRoot, namespacePath, $"{canonicalName}.ahk");
    }

    /// <summary>
    /// Convert a dot-separated namespace to a directory path.
    /// </summary>
    internal static string NamespaceToPath(string ns)
    {
        return ns.Replace('.', Path.DirectorySeparatorChar);
    }
}
