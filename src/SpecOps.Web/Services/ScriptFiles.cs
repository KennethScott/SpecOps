using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using SpecOps.Core.Configuration;
using SpecOps.Core.Security;

namespace SpecOps.Web.Services;

/// <summary>A file in the scripts folder, by its path relative to that folder (with '/' separators).</summary>
public sealed record ScriptFileInfo(string Path, long Size, DateTimeOffset Modified);

/// <summary>
/// Reads, writes, uploads and deletes PowerShell files under the scripts folder (ScriptsRoot) for the admin pages.
/// Paths are always resolved inside that folder: anything outside it (absolute paths, "..") is refused. Admin-only
/// and audited.
/// </summary>
public sealed class ScriptFiles(IOptionsMonitor<SpecOpsOptions> options, ISpecOpsAuthorizer authorizer, AuditLog audit)
{
    /// <summary>Largest file the editor opens or saves.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>Largest file that can be uploaded (modules can be bigger than scripts).</summary>
    public const int MaxUploadBytes = 10 * 1024 * 1024;

    /// <summary>Where Authenticode puts the signature in a signed script.</summary>
    public const string SignatureMarker = "# SIG # Begin signature block";

    private static readonly string[] Extensions = [".ps1", ".psm1", ".psd1"];

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>The file types that can be uploaded, edited and run.</summary>
    public static IReadOnlyList<string> AllowedExtensions => Extensions;

    public string Root => options.CurrentValue.ScriptsRoot;

    /// <summary>PowerShell files under the scripts folder, as paths relative to it (with '/' separators).</summary>
    public IReadOnlyList<string> List() => [.. ListDetails().Select(f => f.Path)];

    /// <summary>PowerShell files under the scripts folder with their size and last change, ordered by path.</summary>
    public IReadOnlyList<ScriptFileInfo> ListDetails()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        return
        [
            .. new DirectoryInfo(Root)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(f => Extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
                .Select(f => new ScriptFileInfo(
                    Path.GetRelativePath(Root, f.FullName).Replace('\\', '/'),
                    f.Length,
                    new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero)
                ))
                .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase),
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

    /// <summary>Saves text from the editor (creating folders as needed). Returns an error message, or null on success.</summary>
    public async Task<string?> SaveAsync(ClaimsPrincipal user, string relativePath, string content)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(content);
        if (Check(user, relativePath) is { } error)
        {
            return error;
        }

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
            .GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(content))
            .ToArray();
        if (bytes.Length > MaxBytes)
        {
            return "The file is too large to save from the editor (1 MB limit).";
        }

        return await WriteAsync(user, relativePath, bytes, "");
    }

    /// <summary>
    /// Stores an uploaded file exactly as it is (so a signed script keeps a valid signature), replacing any file with
    /// the same path. Returns an error message, or null on success.
    /// </summary>
    public async Task<string?> UploadAsync(ClaimsPrincipal user, string relativePath, Stream content)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(content);
        if (Check(user, relativePath) is { } error)
        {
            return error;
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer);
        return buffer.Length > MaxUploadBytes
            ? $"{relativePath} is larger than {MaxUploadBytes / 1024 / 1024} MB."
            : await WriteAsync(user, relativePath, buffer.ToArray(), ", uploaded");
    }

    /// <summary>Deletes a file from the scripts folder. Returns an error message, or null on success.</summary>
    public async Task<string?> DeleteAsync(ClaimsPrincipal user, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (Check(user, relativePath) is { } error)
        {
            return error;
        }

        var path = Resolve(relativePath)!;
        if (!File.Exists(path))
        {
            return $"{relativePath} doesn't exist.";
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Couldn't delete {relativePath}: {ex.Message}";
        }

        await audit.RecordAsync(user, AuditActions.ScriptFileDeleted, relativePath);
        return null;
    }

    private string? Check(ClaimsPrincipal user, string relativePath)
    {
        if (!authorizer.GetAccess(user).IsAdmin)
        {
            return "Only administrators can change script files.";
        }

        return Resolve(relativePath) is null
            ? $"'{relativePath}' isn't a PowerShell file ({string.Join(", ", Extensions)}) inside the scripts folder."
            : null;
    }

    private async Task<string?> WriteAsync(ClaimsPrincipal user, string relativePath, byte[] bytes, string note)
    {
        var path = Resolve(relativePath)!;
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
            $"{bytes.Length:N0} bytes{note}"
        );
        return null;
    }
}
