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
    /// Calculate the relative #Include path from one namespace to a type identified by FQN.
    /// </summary>
    public static string GetIncludePath(string fromNs, string importFqn)
    {
        if (importFqn == "System.Guid")
            return $"{GetPathToBase(fromNs)}Guid.ahk";

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
