namespace Jellyfin.Plugin.MediaHub;

/// <summary>Only plugin-owned files. Never chmod, follow or overwrite a linked state path.</summary>
internal static class StateFiles
{
    internal const long FreeSpaceReserve = 256L * 1024 * 1024;
    internal static void CheckSpaceBudget(long available, long additional)
    {
        if (additional < 0 || additional > 1024L * 1024 * 1024 || available < FreeSpaceReserve || additional > available - FreeSpaceReserve)
            throw new HubError("Plugin storage is below its safety reserve. No write/reset is authorized; free space or restore storage availability before retrying.");
    }
    internal static void RequireSpace(string directory, long additional = 0)
    {
        Check(directory);
        try { CheckSpaceBudget(new DriveInfo(Path.GetFullPath(directory)).AvailableFreeSpace, additional); }
        catch (HubError) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { throw new HubError("Plugin storage availability could not be verified. No write/reset is authorized."); }
    }
    public static void Check(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 4096) throw new HubError("Invalid or oversized Media Hub state path.");
        // Check attributes rather than Exists: a dangling link must also fail closed.
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new HubError("Media Hub state paths and their ancestors must not be symbolic links or junctions. No linked file was changed.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    public static string In(string directory, string name)
    {
        Check(directory);
        if (string.IsNullOrEmpty(name) || name is "." or ".." || Path.GetFileName(name) != name || name.Any(c => char.IsControl(c) || c is '/' or '\\' or ':')) throw new HubError("Invalid Media Hub state filename.");
        var file = Path.Combine(directory, name); Check(file); return file;
    }
    public static FileStream PrivateFile(string path, FileMode mode = FileMode.CreateNew, FileShare share = FileShare.None)
    {
        Check(path);
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = share };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }
    public static FileStream Lease(string directory, string name)
    {
        try { return PrivateFile(In(directory, name), FileMode.OpenOrCreate); }
        catch (IOException) { throw new HubError("Another Media Hub state operation is in progress or storage is unavailable. No reset was performed; retry after checking state."); }
    }
    public static byte[] ReadBounded(string directory, string name, int maximumBytes)
    {
        using var file = new FileStream(In(directory, name), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var length = file.Length;
        if (length > maximumBytes) throw new HubError("Media Hub state file exceeds its supported size. No reset was performed.");
        var bytes = new byte[(int)length]; file.ReadExactly(bytes);
        if (file.Length != length) throw new HubError("Media Hub state changed while reading. Recheck state before retrying.");
        return bytes;
    }
    public static void Publish(string directory, string name, ReadOnlySpan<byte> bytes, string? previous = null, Action<string>? checkpoint = null)
    {
        RequireSpace(directory, checked(bytes.Length * 2L));
        var target = In(directory, name);
        var backup = previous is null ? null : In(directory, previous);
        var temp = In(directory, name + "." + HubStore.Nonce() + ".tmp");
        var created = false;
        try
        {
            using (var file = PrivateFile(temp)) { created = true; file.Write(bytes); file.Flush(true); }
            checkpoint?.Invoke("File.Flushed");
            // Same-directory rename/replace: never truncate a live settings/key file.
            In(directory, name); if (previous is not null) In(directory, previous);
            if (backup is not null && File.Exists(target)) File.Replace(temp, target, backup);
            else File.Move(temp, target, false);
        }
        finally { if (created) DeleteTemporary(temp); }
    }
    public static void DeleteTemporary(string path)
    {
        // Only caller-created staging files; failed cleanup must not mask the original failure.
        try { Check(path); File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (HubError) { }
    }
}
