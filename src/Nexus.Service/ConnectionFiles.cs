namespace Nexus.Service;

/// <summary>An upload kept for a connection: the name it came with, and its name in the files directory.</summary>
public sealed record UploadedFile(string FileName, string StoredFile);

/// <summary>The files connections read, such as a CSV upload, kept in the service's data directory, one per connection.</summary>
public sealed class ConnectionFiles(string root)
{
    /// <summary>The largest file a connection takes: 1 GB.</summary>
    public const long MaxBytes = 1L << 30;

    /// <summary>The multipart form around a file, beside the file itself.</summary>
    public const long FormOverheadBytes = 1L << 20;

    /// <summary>Copies an upload in under a name of its own, or answers why it cannot be kept.</summary>
    public async Task<(UploadedFile? File, string? Problem)> SaveAsync(IFormFile? upload, CancellationToken ct)
    {
        if (upload is not { Length: > 0 })
        {
            return (null, "Choose a file to upload.");
        }

        Directory.CreateDirectory(root);
        var stored = Guid.NewGuid().ToString("N");
        await using (var target = new FileStream(PathOf(stored), FileMode.CreateNew, FileAccess.Write))
        {
            await upload.CopyToAsync(target, ct);
        }

        return (new UploadedFile(Path.GetFileName(upload.FileName), stored), null);
    }

    /// <summary>The stored file, as a connector reads it.</summary>
    public ConnectorFile Open(string fileName, string stored) =>
        new(fileName, _ => Task.FromResult<Stream>(new FileStream(PathOf(stored), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)));

    public void Delete(string? stored)
    {
        if (stored is not null && File.Exists(PathOf(stored)))
        {
            File.Delete(PathOf(stored));
        }
    }

    // A stored name is one this class made, so it never climbs out of the directory.
    private string PathOf(string stored) => Path.Combine(root, Path.GetFileName(stored));
}
