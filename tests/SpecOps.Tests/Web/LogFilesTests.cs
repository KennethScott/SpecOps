using SpecOps.Web.Infrastructure;
using SpecOps.Web.Services;

namespace SpecOps.Tests.Web;

public sealed class LogFilesTests : IDisposable
{
    private const string Sample = """
        2026-11-26 09:04:59.123 -05:00 [INF] Now listening on: http://localhost:5065
        2026-11-26 09:05:00.001 -05:00 [WRN] Failed to determine the https port for redirect.
        2026-11-26 09:05:01.500 -05:00 [ERR] Run 42: unexpected failure
        System.InvalidOperationException: Boom
           at SpecOps.Something()
        2026-11-26 09:05:02.000 -05:00 [INF] Audit signin by Dev User
        """;

    private readonly DirectoryInfo _data = Directory.CreateTempSubdirectory("specops-logs-tests-");
    private readonly LogFiles _logs;

    public LogFilesTests()
    {
        _logs = new LogFiles(new SpecOpsPaths(_data.FullName, Path.Combine(_data.FullName, "specops.json")));
        Directory.CreateDirectory(_logs.Folder);
        File.WriteAllText(Path.Combine(_logs.Folder, "specops-20261126.log"), Sample);
        File.WriteAllText(Path.Combine(_data.FullName, "secret.txt"), "not a log");
    }

    public void Dispose() => _data.Delete(recursive: true);

    [Fact]
    public void Parses_entries_with_multi_line_messages()
    {
        var entries = LogFiles.Parse(Sample).ToList();

        Assert.Equal(["INF", "WRN", "ERR", "INF"], entries.Select(e => e.Level));
        Assert.Equal(new DateTimeOffset(2026, 11, 26, 9, 5, 1, 500, TimeSpan.FromHours(-5)), entries[2].Timestamp);
        Assert.Contains("at SpecOps.Something()", entries[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Filters_by_level_and_search()
    {
        var warnings = await _logs.ReadAsync("specops-20261126.log", "WRN");
        var errors = await _logs.ReadAsync("specops-20261126.log", "ERR");
        var search = await _logs.ReadAsync("specops-20261126.log", search: "BOOM");

        Assert.Equal(2, warnings!.Matching);
        Assert.Equal("ERR", Assert.Single(errors!.Entries).Level);
        Assert.Equal("ERR", Assert.Single(search!.Entries).Level);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData(@"..\secret.txt")]
    [InlineData("secret.txt")]
    [InlineData("missing.log")]
    public async Task Only_files_in_the_log_folder_can_be_opened(string name)
    {
        Assert.Null(_logs.Resolve(name));
        Assert.Null(await _logs.ReadAsync(name));
    }

    [Fact]
    public async Task Large_files_are_read_from_the_end()
    {
        var path = Path.Combine(_logs.Folder, "specops-20261127.log");
        var line = "2026-11-27 10:00:00.000 +00:00 [INF] " + new string('x', 200) + "\n";
        await File.WriteAllTextAsync(
            path,
            string.Concat(Enumerable.Repeat(line, (LogFiles.MaxReadBytes / line.Length) + 100))
                + "2026-11-27 11:00:00.000 +00:00 [INF] the last one\n",
            TestContext.Current.CancellationToken
        );

        var view = await _logs.ReadAsync("specops-20261127.log", take: 5);

        Assert.True(view!.Truncated);
        Assert.Equal("the last one", view.Entries[^1].Text);
        Assert.All(view.Entries, e => Assert.Equal("INF", e.Level));
    }
}
