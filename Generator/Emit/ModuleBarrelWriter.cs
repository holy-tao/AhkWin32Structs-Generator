namespace AhkWin32.Generator.Emit;

using AhkWin32.Generator.Metadata;
using Microsoft.Extensions.Logging;

/// <summary>
/// v2.1 folder-module support: writes an <c>__Init.ahk</c> barrel into every directory of
/// the generated <c>Windows</c> module tree so the whole projection can be pulled in with a
/// single <c>#Import Windows</c> (which AutoHotkey resolves to <c>Windows\__Init.ahk</c>).
///
/// Each barrel re-exports its directory's contents so a wildcard import in the parent barrel
/// chains all the way to the root:
/// <list type="bullet">
///   <item>type files (one <c>export default</c> declaration named after the file) are
///     re-exported by name - <c>#Import export ".\RECT.ahk" {RECT}</c> - because a
///     wildcard does not pick up a module's <em>default</em> export;</item>
///   <item><c>Apis.ahk</c>/<c>Constants.ahk</c> (many named <c>export</c>s, no default) and
///     child directory barrels are re-exported with a wildcard - <c>{*}</c>.</item>
/// </list>
///
/// This runs over the output on disk, so hand-written fixtures that live in the module root
/// (<c>Guid.ahk</c>, <c>Win32ComInterface.ahk</c>, <c>Vector.ahk</c>) are folded into the top
/// barrel automatically. v2.1 emission only - v2.0 has no modules.
/// </summary>
public sealed class ModuleBarrelWriter(ILogger<ModuleBarrelWriter> logger)
{
    private const string BarrelFileName = "__Init.ahk";

    /// <summary>Highest alpha the generated tree relies on (COM interfaces, Vector).</summary>
    private const string RequiredVersion = "AutoHotkey v2.1-alpha.30+ 64-bit";

    /// <summary>Files whose exports are all named (not a default), so a wildcard re-export works.</summary>
    private static readonly HashSet<string> WildcardFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Apis.ahk",
        "Constants.ahk",
    };

    private readonly ILogger<ModuleBarrelWriter> _logger = logger;

    /// <summary>
    /// Write <c>__Init.ahk</c> barrels throughout the <c>Windows</c> module tree under
    /// <paramref name="outputDir"/>. No-ops if the module directory does not exist (e.g. a
    /// namespace-filtered run that emitted nothing under <c>Windows</c>).
    /// </summary>
    public void WriteBarrels(string outputDir)
    {
        string moduleRoot = Path.Combine(outputDir, "Windows");
        if (!Directory.Exists(moduleRoot))
        {
            _logger.LogWarning("Module root {ModuleRoot} does not exist; skipping barrel generation", moduleRoot);
            return;
        }

        _logger.LogInformation("Writing __Init.ahk barrels under {ModuleRoot}...", moduleRoot);
        int count = WriteBarrelForDirectory(moduleRoot);
        _logger.LogInformation("Wrote {Count} __Init.ahk barrels", count);
    }

    /// <summary>Recursively writes a barrel for <paramref name="dir"/> and every subdirectory.</summary>
    private int WriteBarrelForDirectory(string dir)
    {
        // Depth-first so the whole subtree is covered; ordinal sort keeps output stable.
        string[] subDirs = [.. Directory.GetDirectories(dir).OrderBy(Path.GetFileName, StringComparer.Ordinal)];
        int count = subDirs.Sum(WriteBarrelForDirectory);

        string[] files =
        [
            .. Directory
                .GetFiles(dir, "*.ahk")
                .Select(Path.GetFileName)
                .Where(f => f is not null && !string.Equals(f, BarrelFileName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal)!,
        ];

        var w = new AhkWriter(AhkVersion.v21);
        w.Require(RequiredVersion);
        w.BlankLine();

        foreach (string subDir in subDirs)
        {
            string name = Path.GetFileName(subDir);
            w.ReExport($".{Path.DirectorySeparatorChar}{name}{Path.DirectorySeparatorChar}{BarrelFileName}", ["*"]);
        }

        foreach (string file in files)
        {
            string path = $".{Path.DirectorySeparatorChar}{file}";
            if (WildcardFiles.Contains(file))
                w.ReExport(path, ["*"]);
            else
                w.ReExport(path, [Path.GetFileNameWithoutExtension(file)]);
        }

        File.WriteAllText(Path.Combine(dir, BarrelFileName), w.ToString());
        return count + 1;
    }
}
