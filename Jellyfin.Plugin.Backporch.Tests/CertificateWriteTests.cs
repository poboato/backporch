using System.Reflection;
using System.Runtime.InteropServices;
using Jellyfin.Plugin.Backporch.Acme;
using Xunit;

namespace Jellyfin.Plugin.Backporch.Tests;

/// <summary>
/// The bundle on disk holds the certificate's private key, so how it is written is a
/// security property, not an implementation detail.
/// </summary>
public class CertificateWriteTests
{
    // DllImport rather than LibraryImport: the generated marshalling code the latter
    // emits requires AllowUnsafeBlocks, which is a heavy thing to switch on across a test
    // project for one call taking a single integer.
#pragma warning disable SYSLIB1054
    [DllImport("libc", EntryPoint = "umask")]
    private static extern uint SetProcessUmask(uint mask);
#pragma warning restore SYSLIB1054

    private static Task WriteAsync(string path, byte[] pfx, bool secret = true)
    {
        var method = typeof(AcmeService).GetMethod(
            "WriteCertificateAsync",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("WriteCertificateAsync is missing.");

        return (Task)method.Invoke(null, new object[] { path, pfx, secret, CancellationToken.None })!;
    }

    /// <summary>
    /// The chain a reverse proxy reads has to be readable by the account that proxy runs
    /// as. The mode asked for at creation goes through open(2) and is therefore masked by
    /// the process umask — Jellyfin's images expose UMASK, and at 077 the chain lands
    /// owner-only, so the proxy serves the previous certificate with nothing on screen to
    /// say why. The test is worth its awkwardness because the default umask hides it: on
    /// any ordinary machine, and on CI, this passes whether or not the code is right.
    /// </summary>
    [Fact]
    public async Task PublicChain_IsReadableEvenUnderARestrictiveUmask()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = NewDirectory();
        var path = Path.Combine(dir, "chain.pem");
        var previous = SetProcessUmask(0x3F); // 077: deny group and other everything

        try
        {
            await WriteAsync(path, new byte[] { 1, 2, 3 }, secret: false);

            Assert.True(
                File.GetUnixFileMode(path).HasFlag(UnixFileMode.OtherRead),
                "a proxy running as another user could not read the chain");
        }
        finally
        {
            SetProcessUmask(previous);
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The other half of the same rule: a restrictive umask must never be talked out of
    /// tightening the private key. Only the public file is ever re-stated.
    /// </summary>
    [Fact]
    public async Task PrivateKey_StaysOwnerOnlyUnderARestrictiveUmask()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = NewDirectory();
        var path = Path.Combine(dir, "key.pem");
        var previous = SetProcessUmask(0x3F); // 077: deny group and other everything

        try
        {
            await WriteAsync(path, new byte[] { 1, 2, 3 }, secret: true);

            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(path));
        }
        finally
        {
            SetProcessUmask(previous);
            System.IO.Directory.Delete(dir, recursive: true);
        }
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
