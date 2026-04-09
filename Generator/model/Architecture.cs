namespace AhkWin32.Generator.Model;

/// <summary>
/// Architecture flags from the metadata.
/// </summary>
[Flags]
public enum Architecture : uint
{
    None  = 0,
    X86   = 1,
    X64   = 2,
    Arm64 = 4,
    All   = X86 | X64 | Arm64
}
