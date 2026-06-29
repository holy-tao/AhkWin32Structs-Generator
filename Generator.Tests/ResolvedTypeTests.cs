namespace AhkWin32.Generator.Tests;

using AhkWin32.Generator.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class ResolvedTypeTests
{
    [DataTestMethod]
    [DataRow("Int32", 4)]
    [DataRow("UInt32", 4)]
    [DataRow("Single", 4)]
    [DataRow("Int64", 8)]
    [DataRow("Double", 8)]
    [DataRow("IntPtr", 8)]
    [DataRow("Int16", 2)]
    [DataRow("UInt16", 2)]
    [DataRow("Byte", 1)]
    [DataRow("SByte", 1)]
    public void PrimitiveType_Width(string name, int expected)
    {
        Assert.AreEqual(expected, new PrimitiveType(name).Width);
    }

    [TestMethod]
    public void PointerType_Width_IsPointerSized_RegardlessOfPointee()
    {
        Assert.AreEqual(8, new PointerType(null).Width);
        Assert.AreEqual(8, new PointerType(new PrimitiveType("Byte")).Width);
        Assert.AreEqual(8, new PointerType(new StructRef("Test.Big", "Big")).Width);
    }

    [TestMethod]
    public void ArrayType_Width_IsLengthTimesElementWidth()
    {
        Assert.AreEqual(16, new ArrayType(new PrimitiveType("Byte"), 16).Width);
        Assert.AreEqual(40, new ArrayType(new PrimitiveType("Int64"), 5).Width);
    }

    [TestMethod]
    public void StructRef_Width_Throws()
    {
        var sr = new StructRef("Test.A", "A");
        Assert.ThrowsException<InvalidOperationException>(() => _ = sr.Width);
    }

    [TestMethod]
    public void PrimitiveType_Int32_Strings()
    {
        var p = new PrimitiveType("Int32");
        Assert.AreEqual("Integer", p.DisplayName);
        Assert.AreEqual("int", p.DllCallType);
        Assert.AreEqual("Int32", p.TypeSpecifier);
    }

    [TestMethod]
    public void PointerType_ToStruct_EmitsTypedPtrSpecifier()
    {
        var p = new PointerType(new StructRef("Test.Foo", "Foo"));
        Assert.AreEqual("Pointer<Foo>", p.DisplayName);
        Assert.AreEqual("ptr", p.DllCallType);
        Assert.AreEqual("Foo.Ptr", p.TypeSpecifier);
    }

    [TestMethod]
    public void PointerType_Opaque_FallsBackToIntPtr()
    {
        var p = new PointerType(null);
        Assert.AreEqual("Pointer", p.DisplayName);
        Assert.AreEqual("IntPtr", p.TypeSpecifier);
    }

    [TestMethod]
    public void ArrayType_Specifier_IndexesElementSpecifier()
    {
        var a = new ArrayType(new PrimitiveType("Byte"), 16);
        Assert.AreEqual("Int8[16]", a.TypeSpecifier);
        Assert.AreEqual("Array<Integer>", a.DisplayName);
    }
}
