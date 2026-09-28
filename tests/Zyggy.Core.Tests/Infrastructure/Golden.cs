using System.Text;

namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>Loads the golden envelope cases copied to the test output under <c>golden/</c> (tests/golden/README.md).</summary>
public static class Golden
{
    public static string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "golden");

    /// <summary>Every case name, i.e. every <c>golden/*.md</c> file without its extension, excluding the README.</summary>
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (string path in System.IO.Directory.EnumerateFiles(Directory, "*.md").Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (!string.Equals(name, "README", StringComparison.Ordinal))
            {
                data.Add(name);
            }
        }

        return data;
    }

    public static byte[] Md(string name) => File.ReadAllBytes(Path.Combine(Directory, name + ".md"));

    public static byte[] Canonical(string name) => File.ReadAllBytes(Path.Combine(Directory, name + ".canonical"));

    public static string Sig(string name) => Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(Directory, name + ".sig")));
}
