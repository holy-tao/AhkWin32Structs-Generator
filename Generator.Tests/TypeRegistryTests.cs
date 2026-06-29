namespace AhkWin32.Generator.Tests;

using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Types;
using AhkWin32.Generator.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class TypeRegistryTests
{
    [TestMethod]
    public void Register_ThenResolve_RoundTrips()
    {
        var s = Ir.Struct("Test.A", Ir.Field("value", Ir.Prim("Int32")));
        var registry = new TypeRegistry();
        registry.Register(s);

        Assert.AreSame(s, registry.Resolve("Test.A", Architecture.X64));
        Assert.AreSame(s, registry.Resolve<StructType>("Test.A", Architecture.X64));
        Assert.AreEqual(1, registry.Count);
    }

    [TestMethod]
    public void ResolveGeneric_FiltersByType()
    {
        var s = Ir.Struct("Test.A", Ir.Field("value", Ir.Prim("Int32")));
        var registry = new TypeRegistry();
        registry.Register(s);

        // No ApiType registered under this FQN, so the typed resolve returns null.
        Assert.IsNull(registry.Resolve<ApiType>("Test.A", Architecture.X64));
    }

    [TestMethod]
    public void ArchitectureVariants_CoexistAndResolveIndependently()
    {
        var x64 = Ir.Struct("Test.A", Architecture.X64, Ir.Field("p", Ir.Prim("IntPtr")));
        var x86 = Ir.Struct("Test.A", Architecture.X86, Ir.Field("p", Ir.Prim("Int32")));

        var registry = new TypeRegistry();
        registry.Register(x64);
        registry.Register(x86);

        Assert.AreEqual(2, registry.Count, "the two arch variants are distinct entries");
        Assert.AreEqual(2, registry.GetAllVariants("Test.A").Count);
        Assert.AreSame(x64, registry.Resolve("Test.A", Architecture.X64));
        Assert.AreSame(x86, registry.Resolve("Test.A", Architecture.X86));
    }

    [TestMethod]
    public void Remove_ReturnsVariantCount_AndDropsAllVariants()
    {
        var x64 = Ir.Struct("Test.A", Architecture.X64, Ir.Field("p", Ir.Prim("IntPtr")));
        var x86 = Ir.Struct("Test.A", Architecture.X86, Ir.Field("p", Ir.Prim("Int32")));

        var registry = new TypeRegistry();
        registry.Register(x64);
        registry.Register(x86);

        int removed = registry.Remove("Test.A");

        Assert.AreEqual(2, removed);
        Assert.IsFalse(registry.Contains("Test.A"));
        Assert.AreEqual(0, registry.Count);
        Assert.AreEqual(0, registry.Remove("Test.A"), "removing an absent type removes nothing");
    }

    [TestMethod]
    public void Contains_DiscriminatesByPresenceAndKind()
    {
        var s = Ir.Struct("Test.A", Ir.Field("value", Ir.Prim("Int32")));
        var registry = new TypeRegistry();
        registry.Register(s);

        Assert.IsTrue(registry.Contains("Test.A"));
        Assert.IsTrue(registry.Contains<StructType>("Test.A"));
        Assert.IsFalse(registry.Contains<ApiType>("Test.A"));
        Assert.IsFalse(registry.Contains("Test.Missing"));
    }
}
