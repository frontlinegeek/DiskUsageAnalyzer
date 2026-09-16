using System.Runtime.CompilerServices;
using DiskUsageAnalyzer.Core.Caching;
using DiskUsageAnalyzer.Core.Models;
using DiskUsageAnalyzer.Core.Scanning;
using Microsoft.Data.Sqlite;

namespace DiskUsageAnalyzer.Infrastructure.Caching;

public sealed class SqliteScanCache : IScanCache
{
    private readonly string _connectionString;

    public SqliteScanCache(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared, Pooling = true }.ToString();
        Initialize();
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS scans (
                id INTEGER PRIMARY KEY, root_path TEXT NOT NULL COLLATE NOCASE, completed_utc TEXT NOT NULL,
                include_hidden INTEGER NOT NULL, include_system INTEGER NOT NULL, calculate_allocated INTEGER NOT NULL,
                volume_root TEXT, volume_serial INTEGER, file_system TEXT,
                journal_id INTEGER, next_usn INTEGER, first_usn INTEGER);
            CREATE TABLE IF NOT EXISTS entries (
                id INTEGER PRIMARY KEY, scan_id INTEGER NOT NULL REFERENCES scans(id) ON DELETE CASCADE,
                parent_id INTEGER REFERENCES entries(id) ON DELETE CASCADE, name TEXT NOT NULL, full_path TEXT NOT NULL COLLATE NOCASE,
                item_type INTEGER NOT NULL, logical_size INTEGER NOT NULL, allocated_size INTEGER,
                file_count INTEGER NOT NULL, folder_count INTEGER NOT NULL, modified_utc TEXT,
                attributes INTEGER NOT NULL, is_reparse INTEGER NOT NULL, file_id INTEGER, parent_file_id INTEGER,
                subtree_error_count INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS errors (
                id INTEGER PRIMARY KEY, entry_id INTEGER NOT NULL REFERENCES entries(id) ON DELETE CASCADE,
                path TEXT NOT NULL, message TEXT NOT NULL, exception_type TEXT);
            CREATE INDEX IF NOT EXISTS ix_scans_root_completed ON scans(root_path, completed_utc DESC);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_entries_scan_path ON entries(scan_id, full_path);
            CREATE INDEX IF NOT EXISTS ix_entries_scan_parent ON entries(scan_id, parent_id);
            CREATE INDEX IF NOT EXISTS ix_entries_scan_file_id ON entries(scan_id, file_id) WHERE file_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_errors_entry ON errors(entry_id);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    public async Task<CachedScanMetadata?> FindLatestAsync(string rootPath, CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, root_path, completed_utc, include_hidden, include_system, calculate_allocated, volume_root, volume_serial, file_system, journal_id, next_usn, first_usn FROM scans WHERE root_path=$root ORDER BY completed_utc DESC, id DESC LIMIT 1";
        command.Parameters.AddWithValue("$root", Normalize(rootPath));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadMetadata(reader) : null;
    }

    public async Task<CachedScan?> LoadLatestAsync(string rootPath, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var metadata = await FindLatestAsync(rootPath, cancellationToken).ConfigureAwait(false);
        if (metadata is null) return null;
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,parent_id,name,full_path,item_type,logical_size,allocated_size,file_count,folder_count,modified_utc,attributes,is_reparse,file_id,parent_file_id,subtree_error_count FROM entries WHERE scan_id=$scan ORDER BY id";
        command.Parameters.AddWithValue("$scan", metadata.CacheId);
        var items = new Dictionary<long, DiskItem>();
        var parents = new Dictionary<long, long?>();
        long files = 0, folders = 0, bytes = 0;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetInt64(0);
                var item = ReadItem(reader, 2);
                items.Add(id, item); parents.Add(id, reader.IsDBNull(1) ? null : reader.GetInt64(1));
                if (item.ItemType == DiskItemType.File) { files++; bytes += item.LogicalSizeBytes; } else folders++;
                if ((items.Count & 4095) == 0) progress?.Report(new ScanProgress { CurrentPath = item.FullPath,
                    FilesScanned = files, FoldersScanned = folders, BytesScanned = bytes });
            }
        }
        if (items.Count == 0) return null;
        foreach (var pair in parents) if (pair.Value is { } parent) items[parent].Children.Add(items[pair.Key]);
        await LoadErrors(connection, metadata.CacheId, items, cancellationToken).ConfigureAwait(false);
        var root = items[parents.Single(pair => pair.Value is null).Key];
        progress?.Report(new ScanProgress { CurrentPath = root.FullPath, FilesScanned = root.FileCount,
            FoldersScanned = root.FolderCount, BytesScanned = root.LogicalSizeBytes, ErrorsEncountered = root.SubtreeErrorCount });
        return new CachedScan(metadata, root);
    }

    public async IAsyncEnumerable<CachedEntry> ReadChildrenAsync(long cacheId, long? parentEntryId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = parentEntryId.HasValue
            ? "SELECT id,parent_id,name,full_path,item_type,logical_size,allocated_size,file_count,folder_count,modified_utc,attributes,is_reparse,file_id,parent_file_id,subtree_error_count FROM entries WHERE scan_id=$scan AND parent_id=$parent ORDER BY logical_size DESC"
            : "SELECT id,parent_id,name,full_path,item_type,logical_size,allocated_size,file_count,folder_count,modified_utc,attributes,is_reparse,file_id,parent_file_id,subtree_error_count FROM entries WHERE scan_id=$scan AND parent_id IS NULL";
        command.Parameters.AddWithValue("$scan", cacheId);
        if (parentEntryId.HasValue) command.Parameters.AddWithValue("$parent", parentEntryId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            yield return new CachedEntry(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), ReadItem(reader, 2));
    }

    public async Task ReplaceAsync(DiskItem root, ScanOptions options, VolumeIdentity? volume, JournalCheckpoint? journal,
        DateTimeOffset completedAt, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var scanId = await InsertScan(connection, transaction, root.FullPath, options, volume, journal, completedAt, cancellationToken).ConfigureAwait(false);
            var ids = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            long count = 0;
            foreach (var item in DiskTree.Enumerate(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parentPath = item == root ? null : Path.GetDirectoryName(item.FullPath);
                long? parent = parentPath is null ? null : ids[parentPath];
                var id = await InsertEntry(connection, transaction, scanId, parent, item, cancellationToken).ConfigureAwait(false);
                ids[item.FullPath] = id;
                foreach (var error in item.Errors) await InsertError(connection, transaction, id, error, cancellationToken).ConfigureAwait(false);
                if ((++count & 4095) == 0) progress?.Report(new ScanProgress { CurrentPath = item.FullPath,
                    FilesScanned = root.FileCount, FoldersScanned = root.FolderCount, BytesScanned = root.LogicalSizeBytes,
                    ErrorsEncountered = root.SubtreeErrorCount });
            }
            await using var cleanup = connection.CreateCommand();
            cleanup.Transaction = transaction;
            cleanup.CommandText = "DELETE FROM scans WHERE root_path=$root AND id<>$id";
            cleanup.Parameters.AddWithValue("$root", Normalize(root.FullPath)); cleanup.Parameters.AddWithValue("$id", scanId);
            await cleanup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); throw; }
    }

    public async Task DeleteAsync(string rootPath, CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM scans WHERE root_path=$root";
        command.Parameters.AddWithValue("$root", Normalize(rootPath));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(CachedScanMetadata metadata, DiskItem root, JournalCheckpoint? journal,
        DateTimeOffset completedAt, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stored = new Dictionary<string, StoredEntry>(StringComparer.OrdinalIgnoreCase);
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT id,parent_id,name,full_path,item_type,logical_size,allocated_size,file_count,folder_count,modified_utc,attributes,is_reparse,file_id,parent_file_id,subtree_error_count FROM entries WHERE scan_id=$scan";
                select.Parameters.AddWithValue("$scan", metadata.CacheId);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    stored.Add(reader.GetString(3), new StoredEntry(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1),
                        reader.GetString(2), reader.GetString(3), (DiskItemType)reader.GetInt32(4), reader.GetInt64(5),
                        reader.IsDBNull(6) ? null : reader.GetInt64(6), reader.GetInt32(7), reader.GetInt32(8),
                        reader.IsDBNull(9) ? null : reader.GetString(9), (FileAttributes)reader.GetInt64(10), reader.GetBoolean(11),
                        reader.IsDBNull(12) ? null : FromDb(reader.GetInt64(12)), reader.IsDBNull(13) ? null : FromDb(reader.GetInt64(13)), reader.GetInt64(14)));
            }

            var ids = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long count = 0;
            foreach (var item in DiskTree.Enumerate(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parentPath = item == root ? null : Path.GetDirectoryName(item.FullPath);
                long? parentId = parentPath is null ? null : ids[parentPath];
                if (!stored.TryGetValue(item.FullPath, out var existing))
                {
                    var inserted = await InsertEntry(connection, transaction, metadata.CacheId, parentId, item, cancellationToken).ConfigureAwait(false);
                    ids[item.FullPath] = inserted;
                    foreach (var error in item.Errors) await InsertError(connection, transaction, inserted, error, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    ids[item.FullPath] = existing.Id;
                    if (!existing.Matches(parentId, item))
                    {
                        await UpdateEntry(connection, transaction, existing.Id, parentId, item, cancellationToken).ConfigureAwait(false);
                        await ReplaceErrors(connection, transaction, existing.Id, item.Errors, cancellationToken).ConfigureAwait(false);
                    }
                }
                visited.Add(item.FullPath);
                if ((++count & 4095) == 0) progress?.Report(new ScanProgress { CurrentPath = item.FullPath,
                    FilesScanned = root.FileCount, FoldersScanned = root.FolderCount, BytesScanned = root.LogicalSizeBytes,
                    ErrorsEncountered = root.SubtreeErrorCount });
            }

            foreach (var missing in stored.Where(pair => !visited.Contains(pair.Key)).OrderBy(pair => pair.Key.Length))
            {
                await using var delete = connection.CreateCommand(); delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM entries WHERE id=$id"; delete.Parameters.AddWithValue("$id", missing.Value.Id);
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var updateScan = connection.CreateCommand())
            {
                updateScan.Transaction = transaction;
                updateScan.CommandText = "UPDATE scans SET completed_utc=$completed,journal_id=$journal,next_usn=$next,first_usn=$first WHERE id=$id";
                updateScan.Parameters.AddWithValue("$completed", completedAt.ToString("O"));
                updateScan.Parameters.AddWithValue("$journal", ToDb(journal?.JournalId));
                updateScan.Parameters.AddWithValue("$next", (object?)journal?.NextUsn ?? DBNull.Value);
                updateScan.Parameters.AddWithValue("$first", (object?)journal?.FirstUsn ?? DBNull.Value);
                updateScan.Parameters.AddWithValue("$id", metadata.CacheId);
                if (await updateScan.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("The cached scan was replaced while it was being refreshed.");
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); throw; }
    }

    private static async Task UpdateEntry(SqliteConnection c, SqliteTransaction t, long id, long? parent, DiskItem i, CancellationToken token)
    {
        await using var cmd = c.CreateCommand(); cmd.Transaction = t;
        cmd.CommandText = "UPDATE entries SET parent_id=$p,name=$n,full_path=$f,item_type=$t,logical_size=$l,allocated_size=$a,file_count=$fc,folder_count=$dc,modified_utc=$m,attributes=$at,is_reparse=$r,file_id=$fi,parent_file_id=$pi,subtree_error_count=$ec WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$p", (object?)parent ?? DBNull.Value); cmd.Parameters.AddWithValue("$n", i.Name); cmd.Parameters.AddWithValue("$f", i.FullPath);
        cmd.Parameters.AddWithValue("$t", (int)i.ItemType); cmd.Parameters.AddWithValue("$l", i.LogicalSizeBytes); cmd.Parameters.AddWithValue("$a", (object?)i.AllocatedSizeBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fc", i.FileCount); cmd.Parameters.AddWithValue("$dc", i.FolderCount); cmd.Parameters.AddWithValue("$m", i.LastModified?.ToString("O") is { } m ? m : DBNull.Value);
        cmd.Parameters.AddWithValue("$at", (long)i.Attributes); cmd.Parameters.AddWithValue("$r", i.IsReparsePoint); cmd.Parameters.AddWithValue("$fi", ToDb(i.FileId)); cmd.Parameters.AddWithValue("$pi", ToDb(i.ParentFileId)); cmd.Parameters.AddWithValue("$ec", i.SubtreeErrorCount);
        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task ReplaceErrors(SqliteConnection c, SqliteTransaction t, long entryId, IReadOnlyCollection<ScanError> errors, CancellationToken token)
    {
        await using (var delete = c.CreateCommand()) { delete.Transaction = t; delete.CommandText = "DELETE FROM errors WHERE entry_id=$id";
            delete.Parameters.AddWithValue("$id", entryId); await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
        foreach (var error in errors) await InsertError(c, t, entryId, error, token).ConfigureAwait(false);
    }

    private static async Task<long> InsertScan(SqliteConnection c, SqliteTransaction t, string root, ScanOptions o,
        VolumeIdentity? v, JournalCheckpoint? j, DateTimeOffset completed, CancellationToken token)
    {
        await using var cmd = c.CreateCommand(); cmd.Transaction = t;
        cmd.CommandText = "INSERT INTO scans(root_path,completed_utc,include_hidden,include_system,calculate_allocated,volume_root,volume_serial,file_system,journal_id,next_usn,first_usn) VALUES($r,$d,$h,$s,$a,$vr,$vs,$fs,$ji,$nu,$fu); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$r", Normalize(root)); cmd.Parameters.AddWithValue("$d", completed.ToString("O"));
        cmd.Parameters.AddWithValue("$h", o.IncludeHiddenItems); cmd.Parameters.AddWithValue("$s", o.IncludeSystemItems); cmd.Parameters.AddWithValue("$a", o.CalculateAllocatedSize);
        cmd.Parameters.AddWithValue("$vr", (object?)v?.VolumeRoot ?? DBNull.Value); cmd.Parameters.AddWithValue("$vs", (object?)v?.SerialNumber ?? DBNull.Value); cmd.Parameters.AddWithValue("$fs", (object?)v?.FileSystemName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ji", ToDb(j?.JournalId)); cmd.Parameters.AddWithValue("$nu", (object?)j?.NextUsn ?? DBNull.Value); cmd.Parameters.AddWithValue("$fu", (object?)j?.FirstUsn ?? DBNull.Value);
        return (long)(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false))!;
    }

    private static async Task<long> InsertEntry(SqliteConnection c, SqliteTransaction t, long scan, long? parent, DiskItem i, CancellationToken token)
    {
        await using var cmd = c.CreateCommand(); cmd.Transaction = t;
        cmd.CommandText = "INSERT INTO entries(scan_id,parent_id,name,full_path,item_type,logical_size,allocated_size,file_count,folder_count,modified_utc,attributes,is_reparse,file_id,parent_file_id,subtree_error_count) VALUES($s,$p,$n,$f,$t,$l,$a,$fc,$dc,$m,$at,$r,$fi,$pi,$ec); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$s", scan); cmd.Parameters.AddWithValue("$p", (object?)parent ?? DBNull.Value); cmd.Parameters.AddWithValue("$n", i.Name); cmd.Parameters.AddWithValue("$f", i.FullPath);
        cmd.Parameters.AddWithValue("$t", (int)i.ItemType); cmd.Parameters.AddWithValue("$l", i.LogicalSizeBytes); cmd.Parameters.AddWithValue("$a", (object?)i.AllocatedSizeBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fc", i.FileCount); cmd.Parameters.AddWithValue("$dc", i.FolderCount); cmd.Parameters.AddWithValue("$m", i.LastModified?.ToString("O") is { } m ? m : DBNull.Value);
        cmd.Parameters.AddWithValue("$at", (long)i.Attributes); cmd.Parameters.AddWithValue("$r", i.IsReparsePoint); cmd.Parameters.AddWithValue("$fi", ToDb(i.FileId)); cmd.Parameters.AddWithValue("$pi", ToDb(i.ParentFileId)); cmd.Parameters.AddWithValue("$ec", i.SubtreeErrorCount);
        return (long)(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false))!;
    }

    private static async Task InsertError(SqliteConnection c, SqliteTransaction t, long entry, ScanError e, CancellationToken token)
    {
        await using var cmd = c.CreateCommand(); cmd.Transaction = t;
        cmd.CommandText = "INSERT INTO errors(entry_id,path,message,exception_type) VALUES($e,$p,$m,$t)";
        cmd.Parameters.AddWithValue("$e", entry); cmd.Parameters.AddWithValue("$p", e.Path); cmd.Parameters.AddWithValue("$m", e.Message); cmd.Parameters.AddWithValue("$t", (object?)e.ExceptionType ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task LoadErrors(SqliteConnection c, long scan, Dictionary<long, DiskItem> items, CancellationToken token)
    {
        await using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT e.entry_id,e.path,e.message,e.exception_type FROM errors e JOIN entries i ON i.id=e.entry_id WHERE i.scan_id=$scan"; cmd.Parameters.AddWithValue("$scan", scan);
        await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false)) items[reader.GetInt64(0)].Errors.Add(new ScanError { Path = reader.GetString(1), Message = reader.GetString(2), ExceptionType = reader.IsDBNull(3) ? null : reader.GetString(3) });
    }

    private static CachedScanMetadata ReadMetadata(SqliteDataReader r)
    {
        var options = new ScanOptions { RootPath = r.GetString(1), IncludeHiddenItems = r.GetBoolean(3), IncludeSystemItems = r.GetBoolean(4), CalculateAllocatedSize = r.GetBoolean(5) };
        VolumeIdentity? volume = r.IsDBNull(6) ? null : new(r.GetString(6), checked((uint)r.GetInt64(7)), r.GetString(8));
        JournalCheckpoint? journal = r.IsDBNull(9) ? null : new(FromDb(r.GetInt64(9)), r.GetInt64(10), r.GetInt64(11));
        return new(r.GetInt64(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2)), options, volume, journal);
    }

    private static DiskItem ReadItem(SqliteDataReader r, int x) => new() { Name = r.GetString(x), FullPath = r.GetString(x+1), ItemType = (DiskItemType)r.GetInt32(x+2), LogicalSizeBytes = r.GetInt64(x+3), AllocatedSizeBytes = r.IsDBNull(x+4) ? null : r.GetInt64(x+4), FileCount = r.GetInt32(x+5), FolderCount = r.GetInt32(x+6), LastModified = r.IsDBNull(x+7) ? null : DateTimeOffset.Parse(r.GetString(x+7)), Attributes = (FileAttributes)r.GetInt64(x+8), IsReparsePoint = r.GetBoolean(x+9), FileId = r.IsDBNull(x+10) ? null : FromDb(r.GetInt64(x+10)), ParentFileId = r.IsDBNull(x+11) ? null : FromDb(r.GetInt64(x+11)), SubtreeErrorCount = r.GetInt64(x+12) };
    private static object ToDb(ulong? value) => value.HasValue ? unchecked((long)value.Value) : DBNull.Value;
    private static ulong FromDb(long value) => unchecked((ulong)value);
    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private sealed record StoredEntry(long Id, long? ParentId, string Name, string FullPath, DiskItemType Type, long LogicalSize, long? AllocatedSize,
        int FileCount, int FolderCount, string? Modified, FileAttributes Attributes, bool IsReparsePoint,
        ulong? FileId, ulong? ParentFileId, long ErrorCount)
    {
        public bool Matches(long? parentId, DiskItem item) => ParentId == parentId && Name == item.Name && FullPath == item.FullPath
            && Type == item.ItemType
            && LogicalSize == item.LogicalSizeBytes && AllocatedSize == item.AllocatedSizeBytes
            && FileCount == item.FileCount && FolderCount == item.FolderCount
            && string.Equals(Modified, item.LastModified?.ToString("O"), StringComparison.Ordinal)
            && Attributes == item.Attributes && IsReparsePoint == item.IsReparsePoint
            && FileId == item.FileId && ParentFileId == item.ParentFileId && ErrorCount == item.SubtreeErrorCount;
    }
}
