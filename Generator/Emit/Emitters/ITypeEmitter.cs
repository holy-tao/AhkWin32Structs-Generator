namespace AhkWin32.Generator.Emit.Emitters;

using AhkWin32.Generator.Model.Types;

/// <summary>
/// Interface for type-specific AHK code emitters.
/// Each implementation handles one kind of Win32Type.
/// </summary>
public interface ITypeEmitter
{
    /// <summary>Whether this emitter can handle the given type.</summary>
    bool CanEmit(Win32Type type);

    /// <summary>
    /// Emit the type to AHK source code.
    /// Returns the generated content and the desired output file path.
    /// </summary>
    EmitResult Emit(Win32Type type, string outputRoot);
}
