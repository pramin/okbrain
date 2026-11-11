using System.Text;

namespace OKBrain.Tests;

public abstract class BundleFixture
{
    protected string Root { get; private set; } = "";

    [SetUp]
    public void CreateBundle() => Root = Path.Combine(Path.GetTempPath(), "okbrain-tests", Guid.NewGuid().ToString("N"));

    protected void Write(string path, string text)
    {
        var full = Path.Combine(Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, new UTF8Encoding(false));
    }

    protected void EnsureBundle() => Directory.CreateDirectory(Root);

    [TearDown]
    public void RemoveBundle()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
