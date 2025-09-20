
// The kind of field - for AHK we only care whether it's a primitive, pointer, or array (and its type and rank if an array)
// We don't care about most of the specifics
public enum SimpleFieldKind
{
    Primitive,  // int, int16, uint, etc
    Pointer,    // any pointer-sized integer (function pointers, COM interface pointers, etc)
    Array,
    Struct,     // an embedded struct. TypeDef contains the type of the struct itself in this case
    Class,
    Other,      // unhandled, will produce an error
    COM,        // A pointer to a COM Interface
    String,     // A string buffer for which we can use StrPut / StrGet (usually a character array)
    HRESULT     // An int that's specifically an HRESULT
}