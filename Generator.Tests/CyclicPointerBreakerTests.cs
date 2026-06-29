namespace AhkWin32.Generator.Tests;

using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Members;
using AhkWin32.Generator.Model.Types;
using AhkWin32.Generator.Tests.Support;
using AhkWin32.Generator.Transform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class CyclicPointerBreakerTests
{
    private static CyclicPointerBreaker Breaker(ILogger<CyclicPointerBreaker>? logger = null) =>
        new(logger ?? NullLogger<CyclicPointerBreaker>.Instance);

    private static FieldMember FieldOf(StructType s, string name) =>
        s.Members.First(f => f.Name.Equals(name, StringComparison.Ordinal));

    [TestMethod]
    public void NoSoftEdges_NothingMarked()
    {
        // A embeds B by value (hard edge), B is a leaf. No pointer-to-struct fields anywhere,
        // so the pass should early-return without marking anything.
        var a = Ir.Struct("Test.A", Ir.Field("b", Ir.StructRefTo("Test.B")));
        var b = Ir.Struct("Test.B", Ir.Field("value", Ir.Prim("Int32")));

        var registry = new TypeRegistry();
        registry.Register(a);
        registry.Register(b);

        Breaker().Apply(registry);

        Assert.IsFalse(FieldOf(a, "b").EmitAsLazyPointer);
    }

    [TestMethod]
    public void PointerPlusValueEmbedCycle_MarksOnlyPointer()
    {
        // A --*--> B and B --value--> A. The pointer side is the cuttable edge.
        var a = Ir.Struct("Test.A", Ir.Field("pB", Ir.Ptr(Ir.StructRefTo("Test.B"))));
        var b = Ir.Struct("Test.B", Ir.Field("a", Ir.StructRefTo("Test.A")));

        var registry = new TypeRegistry();
        registry.Register(a);
        registry.Register(b);

        Breaker().Apply(registry);

        Assert.IsTrue(FieldOf(a, "pB").EmitAsLazyPointer, "pointer-on-cycle should be marked lazy");
        Assert.IsFalse(FieldOf(b, "a").EmitAsLazyPointer, "value-embedded field is not a cuttable edge");
    }

    [TestMethod]
    public void MutualPointerCycle_MarksBothPointers()
    {
        var a = Ir.Struct("Test.A", Ir.Field("pB", Ir.Ptr(Ir.StructRefTo("Test.B"))));
        var b = Ir.Struct("Test.B", Ir.Field("pA", Ir.Ptr(Ir.StructRefTo("Test.A"))));

        var registry = new TypeRegistry();
        registry.Register(a);
        registry.Register(b);

        Breaker().Apply(registry);

        Assert.IsTrue(FieldOf(a, "pB").EmitAsLazyPointer);
        Assert.IsTrue(FieldOf(b, "pA").EmitAsLazyPointer);
    }

    [TestMethod]
    public void SelfPointer_NotMarked()
    {
        // A pointer to one's own struct is a forward reference, not a load-time cycle — and a
        // self-edge never forms a non-trivial SCC, so it must not be flagged.
        var a = Ir.Struct("Test.A", Ir.Field("pSelf", Ir.Ptr(Ir.StructRefTo("Test.A"))));

        var registry = new TypeRegistry();
        registry.Register(a);

        Breaker().Apply(registry);

        Assert.IsFalse(FieldOf(a, "pSelf").EmitAsLazyPointer);
    }

    [TestMethod]
    public void AcyclicPointerChain_NothingMarked()
    {
        // A --*--> B --*--> C, no back edge. Soft edges exist (so no early return) but no cycle.
        var a = Ir.Struct("Test.A", Ir.Field("pB", Ir.Ptr(Ir.StructRefTo("Test.B"))));
        var b = Ir.Struct("Test.B", Ir.Field("pC", Ir.Ptr(Ir.StructRefTo("Test.C"))));
        var c = Ir.Struct("Test.C", Ir.Field("value", Ir.Prim("Int32")));

        var registry = new TypeRegistry();
        registry.Register(a);
        registry.Register(b);
        registry.Register(c);

        Breaker().Apply(registry);

        Assert.IsFalse(FieldOf(a, "pB").EmitAsLazyPointer);
        Assert.IsFalse(FieldOf(b, "pC").EmitAsLazyPointer);
    }

    [TestMethod]
    public void CycleThroughNestedEmbeddedStruct_MarksInnerPointer()
    {
        // A embeds an (unregistered, inlined) nested struct whose field points to B; B embeds A
        // by value. The cuttable pointer lives inside the nested struct, so CollectEdges must
        // recurse into EmbeddedStruct.Members to see it.
        var inner = Ir.Struct("Test.A_Inner", Ir.Field("pB", Ir.Ptr(Ir.StructRefTo("Test.B"))));
        var a = Ir.Struct("Test.A", Ir.EmbeddedField("inner", inner));
        var b = Ir.Struct("Test.B", Ir.Field("a", Ir.StructRefTo("Test.A")));

        var registry = new TypeRegistry();
        registry.Register(a);
        registry.Register(b);

        Breaker().Apply(registry);

        Assert.IsTrue(FieldOf(inner, "pB").EmitAsLazyPointer);
    }

    [TestMethod]
    public void UnbreakableArrayPointerCycle_WarnsAndLeavesUnmarked()
    {
        // Cluster {A,B}: A holds an *array* of pointers to B (currently uncuttable), B embeds A
        // by value. Cluster {C,D}: a normal scalar pointer cycle, present so that softEdges is
        // non-empty and the pass runs to completion (and so WarnUnbrokenCycles executes).
        var a = Ir.Struct("Test.A", Ir.Field("pBs", Ir.ArrayOf(Ir.Ptr(Ir.StructRefTo("Test.B")), 4)));
        var b = Ir.Struct("Test.B", Ir.Field("a", Ir.StructRefTo("Test.A")));
        var c = Ir.Struct("Test.C", Ir.Field("pD", Ir.Ptr(Ir.StructRefTo("Test.D"))));
        var d = Ir.Struct("Test.D", Ir.Field("c", Ir.StructRefTo("Test.C")));

        var registry = new TypeRegistry();
        registry.Register(a);
        registry.Register(b);
        registry.Register(c);
        registry.Register(d);

        var logger = new ListLogger<CyclicPointerBreaker>();
        Breaker(logger).Apply(registry);

        Assert.IsFalse(FieldOf(a, "pBs").EmitAsLazyPointer, "array-of-pointer is not cut in v1");
        Assert.IsTrue(FieldOf(c, "pD").EmitAsLazyPointer, "the scalar pointer cluster is still broken");
        Assert.IsTrue(
            logger.HasMessageAt(LogLevel.Warning, "no scalar pointer edge"),
            "the uncuttable cluster should be surfaced as a warning"
        );
    }

    [TestMethod]
    public void SecondApply_IsIdempotent()
    {
        var a = Ir.Struct("Test.A", Ir.Field("pB", Ir.Ptr(Ir.StructRefTo("Test.B"))));
        var b = Ir.Struct("Test.B", Ir.Field("a", Ir.StructRefTo("Test.A")));

        var registry = new TypeRegistry();
        registry.Register(a);
        registry.Register(b);

        Breaker().Apply(registry);
        Assert.IsTrue(FieldOf(a, "pB").EmitAsLazyPointer);

        var logger = new ListLogger<CyclicPointerBreaker>();
        Breaker(logger).Apply(registry);

        Assert.IsTrue(FieldOf(a, "pB").EmitAsLazyPointer, "the field stays marked");
        Assert.IsTrue(
            logger.HasMessageAt(LogLevel.Information, "Marked 0 pointer field(s)"),
            "a re-run marks no additional fields"
        );
    }
}
