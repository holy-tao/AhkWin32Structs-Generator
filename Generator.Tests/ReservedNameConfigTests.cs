namespace AhkWin32.Generator.Tests;

using AhkWin32.Generator.Transform;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class ReservedNameConfigTests
{
    [TestMethod]
    public void Load_ReturnsCaseInsensitiveSet()
    {
        string path = Path.Combine(Path.GetTempPath(), $"reserved-{Guid.NewGuid():N}.yml");
        File.WriteAllText(path, "- HWND\n- Buffer\n- type\n");

        try
        {
            HashSet<string> names = ReservedNameConfig.Load(path);

            Assert.AreEqual(3, names.Count);
            Assert.IsTrue(names.Contains("HWND"));
            Assert.IsTrue(names.Contains("hwnd"), "lookup must be case-insensitive");
            Assert.IsTrue(names.Contains("BUFFER"));
            Assert.IsFalse(names.Contains("NotReserved"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
