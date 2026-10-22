using TradingBot.Arena;
using Xunit;

namespace TradingBot.Arena.Tests;

public sealed class PrivateFilesTests
{
    [Fact]
    public void Refuses_a_Git_worktree_and_its_subdirectories()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()); Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: /fixture");
            Assert.Throws<InvalidOperationException>(() => PrivateFiles.DirectoryOutsideRepository(Path.Combine(root, "private")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void Creates_private_files_exclusively_without_overwriting_evidence()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            PrivateFiles.DirectoryOutsideRepository(root);
            string path = Path.Combine(root, "capture.json");
            using (PrivateFiles.CreateNew(path)) { }
            Assert.Throws<IOException>(() => PrivateFiles.CreateNew(path));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
