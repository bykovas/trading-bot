namespace TradingBot.Arena;

public static class PrivateFiles
{
    public static string DirectoryOutsideRepository(string directory)
    {
        string full = Path.GetFullPath(directory);
        // Resolve directory links first (including macOS /var -> /private/var), then enforce Git/privacy on the real target.
        for (int links = 0; ; links++)
        {
            if (links >= 40) throw new InvalidOperationException("Private directory has too many symbolic links.");
            bool changed = false;
            for (var p = new DirectoryInfo(full); p is not null; p = p.Parent)
            {
                if (p.LinkTarget is null) continue;
                string target = p.ResolveLinkTarget(true)?.FullName ?? throw new InvalidOperationException("Broken private-directory symlink.");
                full = Path.GetFullPath(Path.Combine(target, Path.GetRelativePath(p.FullName, full)));
                changed = true; break;
            }
            if (!changed) break;
        }
        for (var p = new DirectoryInfo(full); p is not null; p = p.Parent)
        {
            if (File.Exists(Path.Combine(p.FullName, ".git")) || System.IO.Directory.Exists(Path.Combine(p.FullName, ".git")))
                throw new InvalidOperationException("Private capture/manifest files must stay outside a Git working tree.");
        }
        if (!System.IO.Directory.Exists(full))
        {
            if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(full);
            else System.IO.Directory.CreateDirectory(full, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(full) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new InvalidOperationException("The private import directory must be mode 0700.");
        return full;
    }
    public static FileStream CreateNew(string path)
    {
        string full = Path.GetFullPath(path);
        full = Path.Combine(DirectoryOutsideRepository(Path.GetDirectoryName(full)!), Path.GetFileName(full));
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(full, options);
    }
}
