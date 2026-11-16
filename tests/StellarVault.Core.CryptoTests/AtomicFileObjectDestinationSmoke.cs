using System.Text;
using VaultEngine.Core;

internal static class AtomicFileObjectDestinationSmoke
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "vaultengine-export-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "verified.bin");
            await using (var destination = new AtomicFileObjectDestination(target))
            {
                await using (var temporary = await destination.OpenTemporaryAsync())
                {
                    await temporary.WriteAsync(Encoding.UTF8.GetBytes("verified payload"));
                    Check(!File.Exists(target), "Unverified plaintext became publicly visible.");
                }
                Check(Directory.GetFiles(root, "*.partial").Length == 1,
                    "Verified export did not use a private temporary file.");
                await destination.CommitAsync();
                Check(File.Exists(target) &&
                    File.ReadAllText(target) == "verified payload" &&
                    Directory.GetFiles(root, "*.partial").Length == 0,
                    "Verified export did not atomically commit the completed file.");
            }

            // A failed integrity check must never leave a completed file.
            var abortedTarget = Path.Combine(root, "never-visible.bin");
            await using (var destination = new AtomicFileObjectDestination(abortedTarget))
            {
                await using (var temporary = await destination.OpenTemporaryAsync())
                    await temporary.WriteAsync(Encoding.UTF8.GetBytes("unverified secret"));
                await destination.AbortAsync();
                Check(!File.Exists(abortedTarget) &&
                    Directory.GetFiles(root, "*.partial").Length == 0,
                    "Aborted verified export retained plaintext temporary file.");
            }

            // Dispose without an explicit commit also aborts.
            var droppedTarget = Path.Combine(root, "not-committed.bin");
            await using (var destination = new AtomicFileObjectDestination(droppedTarget))
            {
                await using var temporary = await destination.OpenTemporaryAsync();
                await temporary.WriteAsync(new byte[] { 1, 2, 3 });
            }
            Check(!File.Exists(droppedTarget) &&
                Directory.GetFiles(root, "*.partial").Length == 0,
                "Disposing an uncommitted destination did not clean up.");

            // Never replace a caller's existing file.
            await using (var destination = new AtomicFileObjectDestination(target))
            {
                try
                {
                    await destination.OpenTemporaryAsync();
                    throw new Exception("An existing export file was accepted.");
                }
                catch (IOException) { }
            }
            Check(File.ReadAllText(target) == "verified payload",
                "Verified export overwrote an existing destination.");

            // An external writer might create the final name *after* staging:
            // File.Move without overwrite must still fail at commit.
            var racedTarget = Path.Combine(root, "race.bin");
            await using (var destination = new AtomicFileObjectDestination(racedTarget))
            {
                await using (var temporary = await destination.OpenTemporaryAsync())
                    await temporary.WriteAsync(new byte[] { 4, 5, 6 });
                File.WriteAllText(racedTarget, "created separately");
                try
                {
                    await destination.CommitAsync();
                    throw new Exception("A race replaced the existing final file.");
                }
                catch (IOException) { }
                await destination.AbortAsync();
            }
            Check(File.ReadAllText(racedTarget) == "created separately" &&
                Directory.GetFiles(root, "*.partial").Length == 0,
                "A failed race left a plaintext temporary or replaced final data.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        Console.WriteLine("PASS: atomic filesystem verified destination commit, abort and no-overwrite");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
