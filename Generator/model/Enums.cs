namespace AhkWin32.Generator.Model;

/// <summary>
/// String encoding for StringType and method charset.
/// </summary>
public enum StringEncoding
{
    None,
    Ansi,
    Unicode
}

/// <summary>
/// Calling convention for methods.
/// Replaces MethodImportAttributes calling convention mask.
/// </summary>
public enum CallingConvention
{
    StdCall,
    CDecl,
    ThisCall,
    FastCall,
    WinApi
}

/// <summary>
/// Struct layout kind.
/// Replaces System.Runtime.InteropServices.LayoutKind.
/// </summary>
public enum StructLayoutKind
{
    Sequential,
    Explicit,
    Auto
}

/// <summary>
/// Parameter direction flags.
/// Replaces System.Reflection.ParameterAttributes In/Out/Optional.
/// </summary>
[Flags]
public enum ParameterDirection
{
    None     = 0,
    In       = 1,
    Out      = 2,
    Optional = 4
}

/// <summary>
/// Custom parameter attribute flags decoded from metadata.
/// Replaces the existing CustomParamAttributes flags enum.
/// </summary>
[Flags]
public enum ParameterFlags
{
    None              = 0,
    Reserved          = 1,
    Constant          = 2,
    SizedBuffer       = 4,
    ComOutPtr         = 8,
    RetVal            = 16,
    DoNotRelease      = 32,
    HasIgnoreIfReturn = 64,
    HasRAIIFree       = 128,
    HasFreeWith       = 256
}
