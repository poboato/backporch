using System.Reflection;
using Jellyfin.Plugin.Backporch.Acme;
using Xunit;

namespace Jellyfin.Plugin.Backporch.Tests;

/// <summary>
/// The bundle on disk holds the certificate's private key, so how it is written is a
/// security property, not an implementation detail.
/// </summary>
public class CertificateWriteTests
{
    private static Task WriteAsync(string path, byte[] pfx, bool secret = true)
    {
        var method = typeof(AcmeService).GetMethod(
            "WriteCertificateAsync",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("WriteCertificateAsync is missing.");

        return (Task)method.Invoke(null, new object[] { path, pfx, secret, CancellationToken.None })!;
    }

    private static string NewDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "backporch-write-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Certificate_IsOwnerOnly()
    {
        var dir = NewDirectory();
        var path = Path.Combine(dir, "certificate.pfx");

        try
        {
            await WriteAsync(path, new byte[] { 1, 2, 3 });

            Assert.True(File.Exists(path));

            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(path);
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
            }
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DirectoryWeCreate_IsOwnerOnly()
    {
        var dir = NewDirectory();
        var nested = Path.Combine(dir, "backporch");
        var path = Path.Combine(nested, "certificate.pfx");

        try
        {
            await WriteAsync(path, new byte[] { 1, 2, 3 });

            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(nested);
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    mode);
            }
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Write_LeavesNoTemporaryFileBehind()
    {
        var dir = NewDirectory();
        var path = Path.Combine(dir, "certificate.pfx");

        try
        {
            await WriteAsync(path, new byte[] { 1, 2, 3 });

            var leftovers = System.IO.Directory
                .GetFileSystemEntries(dir)
                .Where(e => !string.Equals(Path.GetFileName(e), "certificate.pfx", StringComparison.Ordinal))
                .ToArray();

            Assert.Empty(leftovers);
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task PrePlacedTempSymlink_IsNotWrittenThrough()
    {
        // The old code wrote to a predictable "<path>.tmp". Anything able to create that
        // name first — which matters when the output path is in a shared directory —
        // could have had the private key written through a symlink of its choosing.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = NewDirectory();
        var path = Path.Combine(dir, "certificate.pfx");
        var victim = Path.Combine(dir, "victim.txt");
        await File.WriteAllTextAsync(victim, "untouched");
        File.CreateSymbolicLink(path + ".tmp", victim);

        try
        {
            await WriteAsync(path, new byte[] { 9, 9, 9 });

            Assert.Equal("untouched", await File.ReadAllTextAsync(victim));
            Assert.Equal(new byte[] { 9, 9, 9 }, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Rewrite_ReplacesAnExistingBundleAndKeepsItOwnerOnly()
    {
        var dir = NewDirectory();
        var path = Path.Combine(dir, "certificate.pfx");

        try
        {
            await WriteAsync(path, new byte[] { 1 });
            await WriteAsync(path, new byte[] { 2, 2 });

            Assert.Equal(new byte[] { 2, 2 }, await File.ReadAllBytesAsync(path));

            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(path));
            }
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }
}
