using CmdManager.Core.Contracts;
using CmdManager.Core.Http;

namespace CmdManager.Core.Sync;

/// <summary>
/// Uploads an existing folder (e.g. a local clone of bigmac529/CMDs) into the library, preserving relative paths.
/// Text files go up as text, everything else as binary; see <see cref="ImportClassifier"/> for command vs asset.
/// Files are sent in batches of roughly <see cref="BatchBytes"/> to POST /api/library/import.
/// </summary>
public sealed class FolderImporter(CmdManagerApiClient api)
{
    public long BatchBytes { get; init; } = 8 * 1024 * 1024;
    public int BatchMaxItems { get; init; } = 200;

    /// <summary>Files larger than this are reported as errors instead of uploaded (match the server's Library:MaxFileBytes).</summary>
    public long MaxFileBytes { get; init; } = 100L * 1024 * 1024;

    public async Task<ImportResult> ImportAsync(string folder, bool overwrite = true, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException(folder);

        int created = 0, updated = 0, unchanged = 0, skipped = 0;
        var errors = new List<ImportError>();
        var batch = new List<ImportItem>();
        long batchSize = 0;

        async Task FlushAsync()
        {
            if (batch.Count == 0)
                return;
            progress?.Report($"Uploading {batch.Count} file(s)…");
            try
            {
                var r = await api.ImportAsync(new ImportRequest(batch.ToList(), overwrite), ct);
                created += r.Created;
                updated += r.Updated;
                unchanged += r.Unchanged;
                skipped += r.Skipped;
                errors.AddRange(r.Errors);
            }
            catch (ApiException ex) when (!ex.IsUnauthorized)
            {
                errors.AddRange(batch.Select(i => new ImportError(i.RelativePath, ex.Message)));
            }

            batch.Clear();
            batchSize = 0;
        }

        foreach (var (fullPath, path, error) in ImportClassifier.EnumerateFolder(folder))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(folder, fullPath).Replace('\\', '/');
            if (path is null)
            {
                errors.Add(new ImportError(rel, error!));
                continue;
            }

            var length = new FileInfo(fullPath).Length;
            if (length > MaxFileBytes)
            {
                errors.Add(new ImportError(path.Value, $"File is {length / 1048576.0:0.#} MB; the limit is {MaxFileBytes / 1048576} MB."));
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(fullPath, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add(new ImportError(path.Value, ex.Message));
                continue;
            }

            var type = ImportClassifier.Classify(path, bytes);
            var item = type == LibraryItemType.Command && TextContent.TryDecode(bytes, out var text)
                ? new ImportItem(path.Value, Text: text, Type: type)
                : new ImportItem(path.Value, Content: bytes, Type: type);

            if (batch.Count > 0 && (batchSize + bytes.Length > BatchBytes || batch.Count >= BatchMaxItems))
                await FlushAsync();
            batch.Add(item);
            batchSize += bytes.Length;
        }

        await FlushAsync();
        return new ImportResult(created, updated, unchanged, skipped, errors);
    }
}
