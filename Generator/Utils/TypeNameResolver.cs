using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;

/// <summary>
/// Centralized utility for resolving type name conflicts with AutoHotkey built-in classes
/// </summary>
public static class TypeNameResolver
{
    /// <summary>
    /// List of top-level AutoHotkey built-in class names, to prevent collisions.
    /// See <a href="https://www.autohotkey.com/docs/v2/ObjList.htm">Built-in Classes</a> in the AHK docs
    /// </summary>
    private static readonly ImmutableArray<string> BuiltinClassNames = [
        "Any", "Object", "Array", "Buffer", "ClipboardAll", "Class", "Error", "MemoryError", "OSError", "TargetError",
        "TimeoutError", "TypeError", "UnsetError", "MemberError", "PropertyError", "MethodError", "UnsetItemError",
        "ValueError", "IndexError", "ZeroDivisionError", "File", "Func", "BoundFunc", "Closure", "Enumerator", "Gui",
        "InputHook", "Map", "Menu", "MenuBar", "RegExMatchInfo", "Primitive", "Number", "Float", "Integer", "String",
        "VarRef", "ComValue", "ComObjArray", "ComObject", "ComValueRef"
    ];

    /// <summary>
    /// Resolves name conflicts with AutoHotkey built-in classes
    /// </summary>
    /// <param name="candidateName">The type name to check for conflicts</param>
    /// <param name="isWinRT">Whether the type is a WinRT type (true) or Win32 type (false)</param>
    /// <returns>The resolved name, with "WinRT" or "Win32" prefix if there was a conflict</returns>
    public static string ResolveConflict(string candidateName, bool isWinRT)
    {
        if (BuiltinClassNames.Contains(candidateName, StringComparer.OrdinalIgnoreCase))
        {
            return (isWinRT ? "WinRT" : "Win32") + candidateName;
        }
        return candidateName;
    }

    /// <summary>
    /// Determines if a namespace indicates a WinRT type based on naming convention
    /// </summary>
    /// <param name="ns">The namespace to check</param>
    /// <returns>True if the namespace indicates a WinRT type, false otherwise</returns>
    public static bool IsWinRTNamespace(string ns)
    {
        return ns.StartsWith("Windows.") &&
               !ns.StartsWith("Windows.Win32.") &&
               !ns.StartsWith("Windows.Wdk.");
    }

    /// <summary>
    /// Resolves a type name given its metadata reader and type definition
    /// </summary>
    /// <param name="reader">The metadata reader containing the type</param>
    /// <param name="typeDef">The type definition</param>
    /// <returns>The resolved type name with conflict resolution applied</returns>
    public static string ResolveTypeName(MetadataReader reader, TypeDefinition typeDef)
    {
        string rawName = reader.GetString(typeDef.Name)
            .TrimEnd("_e__Struct")
            .Split('`').First();

        bool isWinRT = typeDef.Attributes.HasFlag(TypeAttributes.WindowsRuntime);
        return ResolveConflict(rawName, isWinRT);
    }
}
