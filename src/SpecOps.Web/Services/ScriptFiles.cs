using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using SpecOps.Core.Configuration;
using SpecOps.Core.Security;

namespace SpecOps.Web.Services;

/// <summary>
/// Reads and writes PowerShell files under the scripts folder (ScriptsRoot) for the admin script editor. Paths are
/// always resolved inside that folder: anything outside it (absolute paths, "..") is refused. Admin-only and audited.
/// </summary>
public sealed class ScriptFiles(IOptionsMonitor<SpecOpsOptions> options, ISpecOpsAuthorizer authorizer, AuditLog audit)
{
    /// <summary>Largest file the editor opens or saves.</summary>
    public const int MaxBytes = 1024 * 1024;

    private static readonly string[] Extensions = [".ps1", ".psm1", ".psd1"];

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public string Root => options.CurrentValue.ScriptsRoot;

    /// <summary>PowerShell files under the scripts folder, as paths relative to it (with '/' separators).</summary>
    public IReadOnlyList<string> List()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        return
        [
            .. Directory
                .EnumerateFiles(Root, "*", SearchOption.AllDirectories)
                .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/'))
                .Order(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// The full path for <paramref name="relativePath"/>, or null if it isn't a PowerShell file inside the scripts
    /// folder.
    /// </summary>
    public string? Resolve(string? relativePath)
    {
        if (
            string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || !Extensions.Contains(Path.GetExtension(relativePath), StringComparer.OrdinalIgnoreCase)
        )
        {
            return null;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        return full.StartsWith(root, PathComparison) ? full : null;
    }

    /// <summary>The file's text, "" if it doesn't exist yet, or null if the path isn't allowed or the file is too big.</summary>
    public async Task<string?> ReadAsync(string relativePath)
    {
        if (Resolve(relativePath) is not { } path)
        {
            return null;
        }

        if (!File.Exists(path))
        {
            return "";
        }

        return new FileInfo(path).Length > MaxBytes ? null : await File.ReadAllTextAsync(path);
    }

    /// <summary>Saves (creating folders as needed). Returns an error message, or null on success.</summary>
    public async Task<string?> SaveAsync(ClaimsPrincipal user, string relativePath, string content)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(content);
        if (!authorizer.GetAccess(user).IsAdmin)
        {
            return "Only administrators can edit script files.";
        }

        if (Resolve(relativePath) is not { } path)
        {
            return $"'{relativePath}' isn't a PowerShell file (.ps1, .psm1, .psd1) inside the scripts folder.";
        }

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
            .GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(content))
            .ToArray();
        if (bytes.Length > MaxBytes)
        {
            return "The file is too large to save from the editor (1 MB limit).";
        }

        var existed = File.Exists(path);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Write next to the file, then swap it in, so a running script never sees half a file.
            var temp = path + ".specops-tmp";
            await File.WriteAllBytesAsync(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Couldn't save {relativePath}: {ex.Message}";
        }

        await audit.RecordAsync(
            user,
            existed ? AuditActions.ScriptFileSaved : AuditActions.ScriptFileCreated,
            relativePath,
            $"{bytes.Length:N0} bytes"
        );
        return null;
    }
}
