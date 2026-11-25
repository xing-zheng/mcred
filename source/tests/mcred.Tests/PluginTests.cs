using System.Text.Json;
using System.Xml.Linq;
using Xunit;

// The plugin and the code it builds are one version: a seat updates its plugin only when this string changes, so
// /mcred:init never builds a commit between releases.
public class PluginTests
{
    private static string PluginRoot()
    {
        for (string? directory = AppContext.BaseDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
            if (File.Exists(Path.Combine(directory, "mcred.csproj"))) return directory;
        throw new InvalidOperationException($"No mcred.csproj at or above {AppContext.BaseDirectory}.");
    }

    [Fact]
    public void ThePluginsVersionIsMcredsVersion()
    {
        string root = PluginRoot();
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, ".claude-plugin", "plugin.json")));
        string version = XDocument.Load(Path.Combine(root, "mcred.csproj")).Descendants("Version").Single().Value;
        Assert.Equal(version, manifest.RootElement.GetProperty("version").GetString());
        Assert.Equal(version, Config.Version);
    }
}
