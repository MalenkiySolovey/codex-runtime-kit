using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Codex.RuntimeKit.Configuration;

namespace Codex.RuntimeKit.Lifecycle;

public enum CodexLaunchProfileSharingFailureReason
{
    ExistingProfileData,
    WindowsLinkPermission,
    MainStoragePathUnresolved,
}

public sealed class CodexLaunchProfileSharingException : InvalidOperationException
{
    public CodexLaunchProfileSharingFailureReason Reason { get; }

    public CodexLaunchProfileSharingException(
        CodexLaunchProfileSharingFailureReason reason,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }
}

/// <summary>Manages only conversation links recorded in the isolated home ownership marker.</summary>
public sealed class CodexSharedConversationStorage(ICodexConfigFileSystem files, string manifestFileName)
{
    private const uint SymbolicLinkFlagDirectory = 0x1;
    private const uint SymbolicLinkFlagAllowUnprivilegedCreate = 0x2;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorAlreadyExists = 183;
    private const int ErrorPrivilegeNotHeld = 1314;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly ICodexConfigFileSystem _fs = files ?? throw new ArgumentNullException(nameof(files));

    public void Prepare(string launchHome, string mainCodexHome, string sqliteHome)
    {
        var mainHome = NormalizePath(mainCodexHome);
        var normalizedLaunchHome = NormalizePath(launchHome);
        var normalizedSqliteHome = NormalizePath(sqliteHome);
        if (PathsOverlap(mainHome, normalizedLaunchHome) || PathsOverlap(normalizedSqliteHome, normalizedLaunchHome))
        {
            throw new CodexLaunchProfileSharingException(
                CodexLaunchProfileSharingFailureReason.MainStoragePathUnresolved,
                "The main Codex home overlaps this isolated launch profile; conversation sharing was not enabled.");
        }

        if (!Directory.Exists(mainHome))
        {
            throw new CodexLaunchProfileSharingException(
                CodexLaunchProfileSharingFailureReason.MainStoragePathUnresolved,
                "The main Codex home does not exist, so its conversations cannot be shared.");
        }

        Remove(launchHome);

        var sessionsLink = Path.Combine(launchHome, "sessions");
        var archivedSessionsLink = Path.Combine(launchHome, "archived_sessions");
        var sessionIndexLink = Path.Combine(launchHome, "session_index.jsonl");
        ValidateShareLinkPath(sessionsLink, isDirectory: true);
        ValidateShareLinkPath(archivedSessionsLink, isDirectory: true);
        ValidateShareLinkPath(sessionIndexLink, isDirectory: false);

        var sessionsTarget = Path.Combine(mainHome, "sessions");
        var archivedSessionsTarget = Path.Combine(mainHome, "archived_sessions");
        var sessionIndexTarget = Path.Combine(mainHome, "session_index.jsonl");
        Directory.CreateDirectory(sessionsTarget);
        Directory.CreateDirectory(archivedSessionsTarget);
        EnsureEmptyFileExists(sessionIndexTarget);

        var manifest = new SharedChatsManifest
        {
            Version = 2,
            CodexHome = mainHome,
            SqliteHome = normalizedSqliteHome,
            OwnsSessionsLink = true,
            OwnsArchivedSessionsLink = true,
            OwnsSessionIndexLink = true,
        };
        var manifestPath = GetSharedChatsManifestPath(launchHome);
        _fs.WriteAllTextAtomic(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));

        try
        {
            EnsureSharedLink(sessionsLink, sessionsTarget, isDirectory: true);
            EnsureSharedLink(archivedSessionsLink, archivedSessionsTarget, isDirectory: true);
            EnsureSharedLink(sessionIndexLink, sessionIndexTarget, isDirectory: false);
        }
        catch
        {
            try
            {
                Remove(launchHome);
            }
            catch
            {
                // Keep the ownership marker if rollback is incomplete so the
                // next launch can safely retry removing only these exact links.
            }

            throw;
        }
    }

    public void Remove(string launchHome)
    {
        var manifestPath = GetSharedChatsManifestPath(launchHome);
        if (!_fs.FileExists(manifestPath))
        {
            return;
        }

        SharedChatsManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SharedChatsManifest>(_fs.ReadAllText(manifestPath), JsonOptions);
        }
        catch (Exception ex)
        {
            throw new CodexLaunchProfileSharingException(
                CodexLaunchProfileSharingFailureReason.ExistingProfileData,
                "The shared-chat recovery marker is unreadable; the profile was preserved.",
                ex);
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.CodexHome) || !Path.IsPathFullyQualified(manifest.CodexHome))
        {
            throw new CodexLaunchProfileSharingException(
                CodexLaunchProfileSharingFailureReason.ExistingProfileData,
                "The shared-chat recovery marker is invalid; the profile was preserved.");
        }

        if (manifest.Version < 2)
        {
            throw new CodexLaunchProfileSharingException(
                CodexLaunchProfileSharingFailureReason.ExistingProfileData,
                "The shared-chat recovery marker uses an older format; the profile was preserved.");
        }

        var previousMainHome = NormalizePath(manifest.CodexHome);
        if (manifest.OwnsSessionsLink)
        {
            RemoveSharedLink(Path.Combine(launchHome, "sessions"), Path.Combine(previousMainHome, "sessions"), isDirectory: true);
        }

        if (manifest.OwnsArchivedSessionsLink)
        {
            RemoveSharedLink(Path.Combine(launchHome, "archived_sessions"), Path.Combine(previousMainHome, "archived_sessions"), isDirectory: true);
        }

        if (manifest.OwnsSessionIndexLink)
        {
            RemoveSharedLink(Path.Combine(launchHome, "session_index.jsonl"), Path.Combine(previousMainHome, "session_index.jsonl"), isDirectory: false);
        }

        _fs.Delete(manifestPath);
    }

    private static void ValidateShareLinkPath(string linkPath, bool isDirectory)
    {
        if (!TryGetAttributes(linkPath, out var attributes))
        {
            return;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw ExistingProfileDataException(linkPath);
        }

        var isExistingDirectory = (attributes & FileAttributes.Directory) != 0;
        if (isExistingDirectory != isDirectory)
        {
            throw ExistingProfileDataException(linkPath);
        }

        if (isDirectory
            ? Directory.EnumerateFileSystemEntries(linkPath).Any()
            : new FileInfo(linkPath).Length > 0)
        {
            throw ExistingProfileDataException(linkPath);
        }
    }

    private static void EnsureSharedLink(string linkPath, string targetPath, bool isDirectory)
    {
        if (TryGetAttributes(linkPath, out var attributes))
        {
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                // Only remove links recorded by our manifest. Accepting an
                // unmarked matching link would make a later opt-out unlink a
                // user-created junction that the host does not own.
                throw ExistingProfileDataException(linkPath);
            }

            var isExistingDirectory = (attributes & FileAttributes.Directory) != 0;
            if (isExistingDirectory != isDirectory)
            {
                throw ExistingProfileDataException(linkPath);
            }

            if (isDirectory)
            {
                if (Directory.EnumerateFileSystemEntries(linkPath).Any())
                {
                    throw ExistingProfileDataException(linkPath);
                }

                Directory.Delete(linkPath, recursive: false);
            }
            else
            {
                if (new FileInfo(linkPath).Length > 0)
                {
                    throw ExistingProfileDataException(linkPath);
                }

                File.Delete(linkPath);
            }
        }

        CreateSharedSymbolicLink(linkPath, targetPath, isDirectory);
    }

    private static void RemoveSharedLink(string linkPath, string expectedTarget, bool isDirectory)
    {
        if (!TryGetAttributes(linkPath, out var attributes) || (attributes & FileAttributes.ReparsePoint) == 0)
        {
            return;
        }

        if (!LinkTargetMatches(linkPath, expectedTarget, isDirectory))
        {
            throw ExistingProfileDataException(linkPath);
        }

        if (isDirectory)
        {
            Directory.Delete(linkPath, recursive: false);
        }
        else
        {
            File.Delete(linkPath);
        }
    }

    private static void EnsureEmptyFileExists(string path)
    {
        if (Directory.Exists(path))
        {
            throw new IOException($"Expected a Codex history file but found a directory at '{path}'.");
        }

        if (File.Exists(path))
        {
            return;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another Codex process created the file while this profile was prepared.
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }

        attributes = default;
        return false;
    }

    private static bool LinkTargetMatches(string linkPath, string expectedTarget, bool isDirectory)
    {
        try
        {
            FileSystemInfo link = isDirectory ? new DirectoryInfo(linkPath) : new FileInfo(linkPath);
            var target = link.LinkTarget;
            return !string.IsNullOrWhiteSpace(target) &&
                PathsEqual(Path.GetFullPath(target, Path.GetDirectoryName(linkPath)!), expectedTarget);
        }
        catch
        {
            return false;
        }
    }

    private static void CreateSharedSymbolicLink(string linkPath, string targetPath, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new CodexLaunchProfileSharingException(
                CodexLaunchProfileSharingFailureReason.WindowsLinkPermission,
                "Conversation sharing is currently supported only on Windows.");
        }

        var flags = (isDirectory ? SymbolicLinkFlagDirectory : 0) | SymbolicLinkFlagAllowUnprivilegedCreate;
        if (CreateSymbolicLinkW(linkPath, targetPath, flags))
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();
        if (error == ErrorInvalidParameter && CreateSymbolicLinkW(
                linkPath,
                targetPath,
                isDirectory ? SymbolicLinkFlagDirectory : 0))
        {
            return;
        }

        error = Marshal.GetLastWin32Error();
        if (error == ErrorAlreadyExists && LinkTargetMatches(linkPath, targetPath, isDirectory))
        {
            return;
        }

        var win32Exception = new Win32Exception(error);
        if (error is ErrorPrivilegeNotHeld or 5)
        {
            throw new CodexLaunchProfileSharingException(
                CodexLaunchProfileSharingFailureReason.WindowsLinkPermission,
                "Windows did not allow the required conversation-history links to be created.",
                win32Exception);
        }

        throw new IOException($"Could not link '{linkPath}' to the shared Codex history path.", win32Exception);
    }

    private static CodexLaunchProfileSharingException ExistingProfileDataException(string path) => new(
        CodexLaunchProfileSharingFailureReason.ExistingProfileData,
        $"This launch profile already contains local Codex data at '{path}'. ");

    private static bool PathsOverlap(string first, string second) =>
        PathsEqual(first, second) || IsPathWithin(first, second) || IsPathWithin(second, first);

    private static bool IsPathWithin(string parent, string candidate)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return !Path.IsPathRooted(relative) &&
            !string.Equals(relative, "..", StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(NormalizePath(first), NormalizePath(second), StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private string GetSharedChatsManifestPath(string home) => Path.Combine(home, manifestFileName);

    private static bool CreateSymbolicLinkW(string linkPath, string targetPath, uint flags) =>
        CreateSymbolicLinkNative(linkPath, targetPath, flags);

    [DllImport("kernel32.dll", EntryPoint = "CreateSymbolicLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateSymbolicLinkNative(string linkPath, string targetPath, uint flags);

    private sealed class SharedChatsManifest
    {
        public int Version { get; set; }
        public string CodexHome { get; set; } = string.Empty;
        public string SqliteHome { get; set; } = string.Empty;
        public bool OwnsSessionsLink { get; set; }
        public bool OwnsArchivedSessionsLink { get; set; }
        public bool OwnsSessionIndexLink { get; set; }
    }
}
