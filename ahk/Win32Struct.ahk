#Requires AutoHotkey v2.0

/**
 * A Win32Struct is the base class from which all generated structs are derived. It's a 
 * buffer-like object that can either be created at a specific memory location. If created
 * "new", the struct is backed by a native AutoHotkey buffer.
 */
class Win32Struct extends Object{

    __buf := unset

    /**
     * @readoly Pointer to the struct's memory location.
     * @type {Integer}
     */
    ptr => this.__buf.ptr

    /**
     * @readonly The size of the struct in bytes.
     * @type {Integer}
     */
    size => this.__buf.size

    /**
     * @readonly The size of the struct for packing purposes. This value may be larger than the
     * size of the struct
     * @type {Integer}
     */
    packedSize {
        get{
            packingSize := %this.__Class%.packingSize
            return this.size + Mod(packingSize - Mod(this.size, packingSize), packingSize)
        }
    }

    /**
     * Initializes a new `Win32Struct` object at the given memory location. Classes extending `Struct`
     * must contain a static member called `sizeof`, which is taken to be the size of the struct.
     * 
     * @param {Integer} ptr Pointer to the memory location at which to create the struct, or
     *      0 to use a new `Buffer`.
     */
    __New(ptr := 0){
        size := %this.__Class%.sizeof

        if(ptr == 0){
            this.__buf := Buffer(size, 0)
        }
        else{
            this.__buf := {ptr: ptr, size: size}
        }
    }

    /**
     * Returns a string representing the contents of the struct, primarilly for debugging
     */
    HexDump(){
        throw MethodError("TODO")
    }

    /**
     * Returns a string with the type of the struct, its size, and its memory location.
     * @returns {String} A string reprsentation of the struct
     */
    ToString(){
        return Format("{1} ({2} bytes at 0x{3:X})", Type(this), this.size, this.ptr)
    }
}