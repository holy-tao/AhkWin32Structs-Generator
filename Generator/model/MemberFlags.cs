namespace AhkWin32.Generator.Model;

/// <summary>
/// Flags for type and field members indicating special characteristics.
/// </summary>
[Flags]
public enum MemberFlags
{
    None           = 0,
    Deprecated     = 1,
    Reserved       = 2,
    Alignment      = 4,
    Union          = 8,
    Anonymous      = 16,
    Ansi           = 32,
    Unicode        = 64,
    NativeBitField = 128
}
