namespace AhkWin32.Generator.Tests;

using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Types;
using AhkWin32.Generator.Tests.Support;
using AhkWin32.Generator.Transform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class OverrideApplierTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"overrides-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private void WriteOverride(string yaml) => File.WriteAllText(Path.Combine(_dir, "test.yml"), yaml);

    private OverrideApplier Applier(ILogger<OverrideApplier>? logger = null) =>
        new(new OverrideReader(NullLogger<OverrideReader>.Instance), logger ?? NullLogger<OverrideApplier>.Instance);

    [TestMethod]
    public void SkipOverride_RemovesTypeFromRegistry()
    {
        var registry = new TypeRegistry();
        registry.Register(Ir.Struct("Test.Gone", Ir.Field("value", Ir.Prim("Int32"))));

        WriteOverride("- type: Test.Gone\n  skip: true\n");
        Applier().Apply(registry, _dir);

        Assert.IsFalse(registry.Contains("Test.Gone"));
    }

    [TestMethod]
    public void FieldAddAttributes_OrsFlagsOntoMatchingField()
    {
        var s = Ir.Struct("Test.A", Ir.Field("dwFlags", Ir.Prim("UInt32")));
        var registry = new TypeRegistry();
        registry.Register(s);

        WriteOverride("- type: Test.A\n  fields:\n    dwFlags:\n      add-attributes: [Reserved]\n");
        Applier().Apply(registry, _dir);

        Assert.IsTrue(s.Members[0].IsReserved, "the Reserved flag should be set on the field");
    }

    [TestMethod]
    public void MethodSkip_RemovesMethodFromApiType()
    {
        var api = Ir.Api("Test.Apis", Ir.Method("KeepMe", "Test"), Ir.Method("DropMe", "Test"));
        var registry = new TypeRegistry();
        registry.Register(api);

        WriteOverride("- type: Test.Apis\n  methods:\n    DropMe:\n      skip: true\n");
        Applier().Apply(registry, _dir);

        Assert.AreEqual(1, api.Methods.Count);
        Assert.AreEqual("KeepMe", api.Methods[0].Name);
    }

    [TestMethod]
    public void UnmatchedFqn_WarnsAndLeavesRegistryUnchanged()
    {
        var registry = new TypeRegistry();
        registry.Register(Ir.Struct("Test.Present", Ir.Field("value", Ir.Prim("Int32"))));

        WriteOverride("- type: Test.NotHere\n  struct-size-field: cbSize\n");
        var logger = new ListLogger<OverrideApplier>();
        Applier(logger).Apply(registry, _dir);

        Assert.IsTrue(registry.Contains("Test.Present"));
        Assert.AreEqual(1, registry.Count);
        Assert.IsTrue(logger.HasMessageAt(LogLevel.Warning, "not in registry"));
    }
}
