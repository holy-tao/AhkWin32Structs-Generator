
// The kind of field - for AHK we only care whether it's a primitive, pointer, or array (and its type and rank if an array)
// We don't care about most of the specifics
public enum SimpleFieldKind
{
    /// <summary>
    /// A primitive type - int, float, bool, etc
    /// </summary>
    Primitive,

    /// <summary>
    /// A pointer-sized integer, not for COM interfaces or WinRT classes. UnderlyingType contains the pointed-to type
    /// </summary>
    Pointer,

    /// <summary>
    /// An Array (not to be confused with SZArray)
    /// </summary>
    Array,

    /// <summary>
    /// A struct pointer. In this case, TypeDef contains the type of the struct itself
    /// </summary>
    Struct,

    /// <summary>
    /// A WinRT Class pointer. In this case, TypeDef contains the type of the class itself
    /// </summary>
    Class,

    /// <summary>
    /// A special type that doesn't fit into other categories. An error in almost all cases
    /// </summary>
    Other,

    /// <summary>
    /// A COM interface pointer
    /// </summary>
    COM,

    /// <summary>
    /// A string buffer for which we can use StrPut / StrGet (usually a character array). Types like BSTR and HSTRING
    /// are represented as structs, the way they appear in the metadata, and are treated as handles.
    /// </summary>
    String,

    /// <summary>
    /// A HRESULT return type (32-bit Integer)
    /// </summary>
    HRESULT,

    /// <summary>
    /// A NativeTypeDef, usually representing an alias to another type (HWND, etc).
    /// </summary>
    NativeTypedef,

    /// <summary>
    /// A generic type parameter (like T in List<T>)
    /// </summary>
    OpenGeneric,

    /// <summary>
    /// Single-dimensional array with zero lower bound. At the the ABI level, this is actually two parameters - 
    /// a UInt32 length and a T* pointer. Only used in WinRT methods.
    /// <br/> <br/>
    /// See <see cref="https://learn.microsoft.com/en-us/uwp/winrt-cref/winrt-type-system#array-parameters"/>
    /// </summary>
    SZArray
}