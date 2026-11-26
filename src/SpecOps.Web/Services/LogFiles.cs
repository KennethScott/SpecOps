using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SpecOps.Web.Infrastructure;

namespace SpecOps.Web.Services;

public sealed record LogFileInfo(string Name, long Size, DateTimeOffset Modified);

/// <summary>One log event: its first line's timestamp and level, plus any following lines (e.g. a stack trace).</summary>
public sealed record LogEntry(DateTimeOffset? Timestamp, string Level, string Text);

/// <summary>A filtered view of one log file. <see cref="Truncated"/> means only the end of the file was read.</summary>
public sealed record LogView(IList<LogEntry> Entries, int Matching, bool Truncated);

/// <summary>
/// The application's log files (Serilog, data/logs) for the admin Logs page. Only files in that folder can be
/// opened, by name; reading never blocks Serilog, which keeps writing the current file.
/// </summary>
public sealed partial class LogFiles(SpecOpsPaths paths)
{
    public const string FolderName = "logs";

    /// <summary>Only the last part of a file is read (enough for days of normal logging).</summary>
    public const int MaxReadBytes = 8 * 1024 * 1024;

    private static readonly string[] Levels = ["VRB", "DBG", "INF", "WRN", "ERR", "FTL"];

    public string Folder => Path.Combine(paths.DataDirectory, FolderName);

    /// <summary>Log files, newest first.</summary>
    public IReadOnlyList<LogFileInfo> List()
    {
        var folder = new DirectoryInfo(Folder);
        return folder.Exists
            ?
            [
                .. folder
                    .GetFiles("*.log")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Select(f => new LogFileInfo(
                        f.Name,
                        f.Length,
                        new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero)
                    )),
            ]
            : [];
    }

    /// <summary>The full path of a listed log file, or null (so a crafted name can't reach anything else).</summary>
    public string? Resolve(string? name) =>
        name is null ? null
        : List().FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal)) is { } file
            ? Path.Combine(Folder, file.Name)
        : null;

    /// <summary>Opens a listed log file for reading while Serilog may still be writing it.</summary>
    public Stream? Open(string name) =>
        Resolve(name) is { } path
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
            : null;

    /// <summary>
    /// The last <paramref name="take"/> entries at <paramref name="minimumLevel"/> or above that contain
    /// <paramref name="search"/> (case-insensitive), oldest first.
    /// </summary>
    public async Task<LogView?> ReadAsync(
        string name,
        string minimumLevel = "VRB",
        string? search = null,
        int take = 1000
    )
    {
        await using var stream = Open(name);
        if (stream is null)
        {
            return null;
        }

        var truncated = stream.Length > MaxReadBytes;
        if (truncated)
        {
            stream.Seek(-MaxReadBytes, SeekOrigin.End);
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync();
        if (truncated)
        {
            // Skip the partial first line.
            text = text[(text.IndexOf('\n', StringComparison.Ordinal) + 1)..];
        }

        var minimum = Math.Max(0, Array.IndexOf(Levels, minimumLevel));
        var matching = Parse(text)
            .Where(e => Array.IndexOf(Levels, e.Level) is var level && (level < 0 || level >= minimum))
            .Where(e =>
                string.IsNullOrWhiteSpace(search) || e.Text.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
        return new LogView([.. matching.TakeLast(take)], matching.Count, truncated);
    }

    /// <summary>Splits Serilog's file format ("2026-11-26 09:04:59.123 -05:00 [INF] Message") into entries.</summary>
    public static IEnumerable<LogEntry> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        DateTimeOffset? timestamp = null;
        string? level = null;
        var body = new StringBuilder();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (EntryStart().Match(line) is { Success: true } start)
            {
                if (level is not null)
                {
                    yield return new LogEntry(timestamp, level, body.ToString().TrimEnd());
                }

                timestamp = DateTimeOffset.TryParseExact(
                    start.Groups["time"].Value,
                    "yyyy-MM-dd HH:mm:ss.fff zzz",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed
                )
                    ? parsed
                    : null;
                level = start.Groups["level"].Value;
                body.Clear().Append(start.Groups["message"].Value);
            }
            else if (level is not null)
            {
                body.Append('\n').Append(line);
            }
            else if (line.Length > 0)
            {
                // Text before the first entry (e.g. the end of an entry cut off by reading only the tail).
                level = "";
                body.Append(line);
            }
        }

        if (level is not null)
        {
            yield return new LogEntry(timestamp, level, body.ToString().TrimEnd());
        }
    }

    [GeneratedRegex(
        @"^(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) \[(?<level>[A-Z]{3})\] (?<message>.*)$",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex EntryStart();
}
