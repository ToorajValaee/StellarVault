using System.Security.Cryptography;

namespace VaultEngine.Core;

/// <summary>
/// Trusted-filesystem implementation of an atomic verified export destination.
/// The caller chooses a FINAL ABSOLUTE path in a trusted directory. Plaintext
/// is staged in a random, exclusive temporary file in that same directory and
/// never exposed at the final path until verification has succeeded.
/// 
/// This is suitable for native/hosted filesystem callers, NOT a browser
/// download or a public/static web directory. It does not securely erase
/// plaintext from SSDs, backups or filesystem snapshots.
/// </summary>
public sealed class AtomicFileObjectDestination : IVerifiedObjectDestination, IAsyncDisposable
{
    private readonly string _finalPath;
    private string? _temporaryPath;
    private FileStream? _stream;
    private bool _opened;
    private bool _committed;
    private bool _finished;

    public AtomicFileObjectDestination(string finalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        if (!Path.IsPathFullyQualified(finalPath))
            throw new ArgumentException("Verified export destination needs an absolute path.",
                nameof(finalPath));
        _finalPath = Path.GetFullPath(finalPath);
        var dir = Path.GetDirectoryName(_finalPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            throw new DirectoryNotFoundException(
                "Verified export destination directory does not exist.");
        // Never place plaintext in a publicly served directory. The caller
        // must choose a private trusted directory and enforce its permissions.
    }

    public Task<Stream> OpenTemporaryAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_finished || _opened)
            throw new InvalidOperationException(
                "Verified export destination can only open once.");
        if (File.Exists(_finalPath) || Directory.Exists(_finalPath))
            throw new IOException("Verified export will not overwrite an existing destination.");

        var directory = Path.GetDirectoryName(_finalPath)!;
        FileStream? created = null;
        for (var attempt = 0; attempt < 8 && created is null; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = Path.Combine(directory,
                ".vaultengine-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) +
                ".partial");
            try
            {
                created = new FileStream(candidate, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                _temporaryPath = candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Another file already owns this random temporary name.
            }
        }
        _stream = created ?? throw new IOException(
            "Could not allocate an exclusive verified export temporary file.");
        _opened = true;
        return Task.FromResult<Stream>(_stream);
    }

    public Task CommitAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_opened || _finished || _temporaryPath is null)
            throw new InvalidOperationException("No uncommitted verified export is staged.");

        // The verifier disposes its writable stream before invoking Commit.
        // File.Move without overwrite is atomic within this trusted directory
        // on supported local filesystems and fails closed if the target exists.
        _stream?.Dispose();
        _stream = null;
        if (File.Exists(_finalPath) || Directory.Exists(_finalPath))
            throw new IOException("Verified export destination already exists.");
        File.Move(_temporaryPath, _finalPath, overwrite: false);
        _temporaryPath = null;
        _committed = true;
        _finished = true;
        return Task.CompletedTask;
    }

    public Task AbortAsync(CancellationToken ct = default)
    {
        // Cancellation must not skip cleanup of an unfinished plaintext file.
        if (_committed) return Task.CompletedTask;
        _stream?.Dispose();
        _stream = null;
        if (_temporaryPath is not null)
        {
            File.Delete(_temporaryPath);
            _temporaryPath = null;
        }
        _finished = true;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_committed)
            await AbortAsync(CancellationToken.None);
    }
}
