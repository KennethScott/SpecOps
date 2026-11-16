using SpecOps.Web.Infrastructure;

namespace SpecOps.Tests.Web;

public sealed class SpecOpsPathsTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("specops-paths-");

    public void Dispose() => _root.Delete(recursive: true);

    private string Seed()
    {
        var seed = Path.Combine(_root.FullName, "seed");
        Directory.CreateDirectory(Path.Combine(seed, "scripts"));
        File.WriteAllText(Path.Combine(seed, "specops.json"), "{ \"seeded\": true }");
        File.WriteAllText(Path.Combine(seed, "scripts", "Demo.ps1"), "'demo'");
        return seed;
    }

    [Fact]
    public void Seeds_an_empty_data_directory()
    {
        var data = Path.Combine(_root.FullName, "data");
        var paths = new SpecOpsPaths(data, Path.Combine(data, "specops.json"));

        Assert.True(paths.SeedIfMissing(Seed()));
        Assert.True(File.Exists(Path.Combine(data, "scripts", "Demo.ps1")));
    }

    [Fact]
    public void Never_overwrites_existing_configuration()
    {
        var data = Path.Combine(_root.FullName, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "specops.json"), "{ \"mine\": true }");
        var paths = new SpecOpsPaths(data, Path.Combine(data, "specops.json"));

        Assert.False(paths.SeedIfMissing(Seed()));
        Assert.Contains("mine", File.ReadAllText(Path.Combine(data, "specops.json")), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(data, "scripts", "Demo.ps1")));
    }

    [Fact]
    public void Does_nothing_without_a_seed_directory()
    {
        var data = Path.Combine(_root.FullName, "data");
        var paths = new SpecOpsPaths(data, Path.Combine(data, "specops.json"));

        Assert.False(paths.SeedIfMissing(null));
        Assert.False(paths.SeedIfMissing(Path.Combine(_root.FullName, "missing")));
    }
}
