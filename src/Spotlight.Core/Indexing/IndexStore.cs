using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using Spotlight.Core.Extraction;
using Spotlight.Core.Search;

namespace Spotlight.Core.Indexing;

public readonly record struct PendingContent(long Id, string Path, string Ext, long Size, long MTime);

/// <summary>
/// The on-disk index: one SQLite file (WAL mode) holding
///  - files: one row per indexed file/folder (metadata + usage statistics),
///  - names_fts: FTS5 over tokenized file names,
///  - content_fts: FTS5 over tokenized document text,
///  - texts: Brotli-compressed original text, used only to render result snippets,
///  - picks: which result the user opened for which query (learned ranking).
/// All writes go through this class on the indexer thread; searches use their own read connection.
/// </summary>
public sealed class IndexStore : IDisposable
{
    public const int SchemaVersion = 2;

    private readonly SqliteConnection _conn;
    private readonly object _lock = new();
    private SqliteTransaction? _tx;
    private readonly Settings _settings;
    private readonly HashSet<string> _appRoots;

    public string DbPath { get; }

    public IndexStore(string dbPath, Settings settings)
    {
        DbPath = dbPath;
        _settings = settings;
        _appRoots = new HashSet<string>(Settings.AppRoots(), StringComparer.OrdinalIgnoreCase);
        // First start on a new PC: the index folder (e.g. D:\SpotlightWinData) doesn't exist yet.
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _conn = Open(dbPath, readOnly: false);
        EnsureSchema();
    }

    internal static SqliteConnection Open(string dbPath, bool readOnly)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();
        var c = new SqliteConnection(cs);
        c.Open();
        Exec(c, "PRAGMA busy_timeout=5000;");
        if (!readOnly) Exec(c, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY;");
        Exec(c, "PRAGMA cache_size=-4000;"); // 4 MB page cache; the OS file cache does the rest
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void EnsureSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(cmd.ExecuteScalar());
        if (version == 1)
        {
            // 0.1.0 → 0.2.0: record why a document has little or no text, and re-read every PDF with the
            // new extractor (0.1.0 cut long PDFs short and dropped them entirely on timeout).
            Exec(_conn, """
                ALTER TABLE files ADD COLUMN content_chars INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE files ADD COLUMN content_note TEXT;
                UPDATE files SET content_mtime = 0 WHERE ext = '.pdf';
                """);
        }
        else if (version != 0 && version != SchemaVersion)
        {
            // Schema changed between releases: the index is a cache, so rebuild it from scratch.
            Exec(_conn, "DROP TABLE IF EXISTS files; DROP TABLE IF EXISTS names_fts; DROP TABLE IF EXISTS content_fts; DROP TABLE IF EXISTS texts; DROP TABLE IF EXISTS meta;");
        }
        // New indexes can hand freed pages back to the file system (see ReleaseSpace).
        if (version == 0) Exec(_conn, "PRAGMA auto_vacuum = INCREMENTAL;");
        Exec(_conn, $"""
            CREATE TABLE IF NOT EXISTS files(
                id INTEGER PRIMARY KEY,
                path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                name TEXT NOT NULL,
                name_lower TEXT NOT NULL,
                ext TEXT NOT NULL,
                kind INTEGER NOT NULL,
                size INTEGER NOT NULL,
                mtime INTEGER NOT NULL,
                gen INTEGER NOT NULL DEFAULT 0,
                wants_content INTEGER NOT NULL DEFAULT 0,
                content_mtime INTEGER NOT NULL DEFAULT 0,
                has_content INTEGER NOT NULL DEFAULT 0,
                content_chars INTEGER NOT NULL DEFAULT 0,
                content_note TEXT,
                launch_count INTEGER NOT NULL DEFAULT 0,
                last_launch INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_files_name ON files(name_lower);
            CREATE INDEX IF NOT EXISTS ix_files_pending ON files(id) WHERE wants_content = 1 AND content_mtime <> mtime;
            CREATE VIRTUAL TABLE IF NOT EXISTS names_fts USING fts5(tokens, tokenize='unicode61 remove_diacritics 2', content='', contentless_delete=1);
            CREATE VIRTUAL TABLE IF NOT EXISTS content_fts USING fts5(tokens, tokenize='unicode61 remove_diacritics 2', content='', contentless_delete=1);
            CREATE TABLE IF NOT EXISTS texts(id INTEGER PRIMARY KEY, data BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS picks(query TEXT NOT NULL, file_id INTEGER NOT NULL, count INTEGER NOT NULL, last INTEGER NOT NULL, PRIMARY KEY(query, file_id)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);
            PRAGMA user_version={SchemaVersion};
            """);
        // 0.8.0: the NTFS file id of each row, so a journal record (which names files by id) finds it.
        bool hasFid = false;
        using (var info = _conn.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(files);";
            using var r = info.ExecuteReader();
            while (r.Read()) hasFid |= r.GetString(1) == "fid";
        }
        if (!hasFid) Exec(_conn, "ALTER TABLE files ADD COLUMN fid INTEGER;");
        Exec(_conn, "CREATE INDEX IF NOT EXISTS ix_files_fid ON files(fid) WHERE fid IS NOT NULL;");
    }

    // ---------------------------------------------------------------- meta

    public string? GetMeta(string key)
    {
        lock (_lock)
        {
            using var c = Cmd("SELECT value FROM meta WHERE key = $k;");
            c.Parameters.AddWithValue("$k", key);
            return c.ExecuteScalar() as string;
        }
    }

    public void SetMeta(string key, string? value)
    {
        lock (_lock)
        {
            using var c = Cmd(value == null ? "DELETE FROM meta WHERE key = $k;" : "INSERT OR REPLACE INTO meta(key, value) VALUES($k, $v);");
            c.Parameters.AddWithValue("$k", key);
            if (value != null) c.Parameters.AddWithValue("$v", value);
            c.ExecuteNonQuery();
        }
    }

    /// <summary>The row with this file id on the volume <paramref name="volume"/> ("C:"), if any.</summary>
    public (string path, bool isDir)? FindByFid(string volume, long fid)
    {
        lock (_lock)
        {
            var (lo, hi) = PrefixRange(volume);
            using var c = Cmd("SELECT path, kind FROM files WHERE fid = $f AND path >= $lo AND path < $hi LIMIT 1;");
            c.Parameters.AddWithValue("$f", fid);
            c.Parameters.AddWithValue("$lo", lo);
            c.Parameters.AddWithValue("$hi", hi);
            using var r = c.ExecuteReader();
            return r.Read() ? (r.GetString(0), r.GetInt32(1) == (int)FileKind.Folder) : null;
        }
    }

    /// <summary>Whether this path is indexed (a folder the journal's changes inside it are kept for).</summary>
    public bool Contains(string path)
    {
        lock (_lock)
        {
            using var c = Cmd("SELECT 1 FROM files WHERE path = $p;");
            c.Parameters.AddWithValue("$p", path);
            return c.ExecuteScalar() != null;
        }
    }

    // ---------------------------------------------------------------- batches

    public void BeginBatch()
    {
        Monitor.Enter(_lock);
        _tx = _conn.BeginTransaction();
    }

    public void CommitBatch()
    {
        try { _tx?.Commit(); }
        finally
        {
            _tx?.Dispose();
            _tx = null;
            Monitor.Exit(_lock);
        }
    }

    private SqliteCommand Cmd(string sql)
    {
        var c = _conn.CreateCommand();
        c.CommandText = sql;
        c.Transaction = _tx;
        return c;
    }

    // ---------------------------------------------------------------- generation

    private long _gen = -1;

    /// <summary>
    /// A number above every generation in the table. gen has no index, so MAX(gen) reads the whole
    /// files table (~150 ms, ~100 MB on 400k files); it ran for every changed file, so a burst of
    /// changes (a download, a sync) delayed new files by minutes and kept the disk busy. Now the
    /// table is read once and the counter goes on in memory.
    /// </summary>
    public long NextGeneration()
    {
        lock (_lock)
        {
            if (_gen < 0)
            {
                using var c = Cmd("SELECT COALESCE(MAX(gen), 0) FROM files;");
                _gen = Convert.ToInt64(c.ExecuteScalar());
            }
            return ++_gen;
        }
    }

    // ---------------------------------------------------------------- upserts (call inside a batch)

    /// <summary>Inserts or refreshes one entry. Must be called between BeginBatch and CommitBatch.</summary>
    public void Upsert(FileSystemInfo info, long gen)
    {
        bool isDir = info is DirectoryInfo;
        Upsert(new FsEntry(info.FullName, info.Name, isDir, info.Attributes, isDir ? 0 : ((FileInfo)info).Length, info.LastWriteTimeUtc.Ticks, 0), gen);
    }

    /// <summary>Inserts or refreshes one entry. Must be called between BeginBatch and CommitBatch.</summary>
    public void Upsert(FsEntry e, long gen)
    {
        bool isDir = e.IsDir;
        string path = e.Path;
        string ext = isDir ? "" : e.Extension.ToLowerInvariant();
        long size = isDir ? 0 : e.Size;
        long mtime = e.MTime;
        var kind = isDir ? FileKind.Folder : ClassifyFile(path, ext);
        string display = DisplayName(e.Name, ext, kind);
        bool wants = WantsContent(path, ext, size, kind) && !IsCloudOnly(e.Attributes);
        object fid = e.Fid != 0 ? e.Fid : DBNull.Value;

        using (var sel = Cmd("SELECT id, mtime, size, fid FROM files WHERE path = $p;"))
        {
            sel.Parameters.AddWithValue("$p", path);
            using var r = sel.ExecuteReader();
            if (r.Read())
            {
                long id = r.GetInt64(0);
                bool changed = r.GetInt64(1) != mtime || r.GetInt64(2) != size;
                bool newFid = e.Fid != 0 && (r.IsDBNull(3) || r.GetInt64(3) != e.Fid);
                r.Close();
                using var upd = Cmd(changed
                    ? "UPDATE files SET gen = $g, mtime = $m, size = $s, wants_content = $w, fid = COALESCE($f, fid) WHERE id = $id;"
                    // wants_content is refreshed too: a cloud placeholder may have been downloaded since.
                    : newFid ? "UPDATE files SET gen = $g, wants_content = $w, fid = $f WHERE id = $id;"
                    : "UPDATE files SET gen = $g, wants_content = $w WHERE id = $id;");
                upd.Parameters.AddWithValue("$g", gen);
                upd.Parameters.AddWithValue("$id", id);
                upd.Parameters.AddWithValue("$w", wants ? 1 : 0);
                if (changed || newFid) upd.Parameters.AddWithValue("$f", fid);
                if (changed)
                {
                    upd.Parameters.AddWithValue("$m", mtime);
                    upd.Parameters.AddWithValue("$s", size);
                }
                upd.ExecuteNonQuery();
                return;
            }
        }

        using (var ins = Cmd("""
            INSERT INTO files(path, name, name_lower, ext, kind, size, mtime, gen, wants_content, fid)
            VALUES($p, $n, $nl, $e, $k, $s, $m, $g, $w, $f) RETURNING id;
            """))
        {
            ins.Parameters.AddWithValue("$f", fid);
            ins.Parameters.AddWithValue("$p", path);
            ins.Parameters.AddWithValue("$n", display);
            ins.Parameters.AddWithValue("$nl", display.ToLowerInvariant());
            ins.Parameters.AddWithValue("$e", ext);
            ins.Parameters.AddWithValue("$k", (int)kind);
            ins.Parameters.AddWithValue("$s", size);
            ins.Parameters.AddWithValue("$m", mtime);
            ins.Parameters.AddWithValue("$g", gen);
            ins.Parameters.AddWithValue("$w", wants ? 1 : 0);
            long id = Convert.ToInt64(ins.ExecuteScalar());
            Inserted++;

            using var fts = Cmd("INSERT INTO names_fts(rowid, tokens) VALUES($id, $t);");
            fts.Parameters.AddWithValue("$id", id);
            fts.Parameters.AddWithValue("$t", NameTokens(display, ext));
            fts.ExecuteNonQuery();
        }
    }

    internal static string NameTokens(string display, string ext)
    {
        var tokens = TextTokenizer.ForIndex(display, splitWords: true);
        return ext.Length > 1 ? tokens + " " + ext[1..] : tokens;
    }

    private FileKind ClassifyFile(string path, string ext)
    {
        var kind = FileKinds.FromExtension(ext);
        // Only shortcuts count as "apps" everywhere; a bare .exe is an app only inside the Start Menu,
        // otherwise every installer in Downloads would be ranked like an application.
        if (kind == FileKind.App && ext == ".exe" && !_appRoots.Any(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase))
            && !LooksLikeAppExe(path))
            return FileKind.Other;
        return kind;
    }

    private static readonly string[] NotAppWords =
        { "setup", "install", "uninst", "unins0", "update", "upgrade", "patch", "redist", "crash", "report", "helper", "service", "elevat", "debug", "test" };
    private static readonly string[] NotAppFolders = { "downloads", "temp", "tmp", "cache", "updates", "pending", "backup", "$recycle.bin", "installer" };

    /// <summary>
    /// A portable or self-built program outside the Start Menu (e.g. D:\tools\lightodo-v0.4\dist\Lightodo.exe):
    /// the exe is named like one of its three nearest folders. That picks the main program of a folder
    /// and skips its helpers (crash reporters, updaters) as well as installers lying in Downloads.
    /// </summary>
    internal static bool LooksLikeAppExe(string path)
    {
        string stem = Letters(Path.GetFileNameWithoutExtension(path));
        if (stem.Length < 3 || NotAppWords.Any(stem.Contains)) return false;
        var dir = Path.GetDirectoryName(path);
        for (int i = 0; i < 3 && !string.IsNullOrEmpty(dir); i++)
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name)) break;
            if (NotAppFolders.Contains(name.ToLowerInvariant())) return false;
            var folder = Letters(name);
            if (folder.Length >= 3 && (folder.Contains(stem) || stem.Contains(folder))) return true;
            dir = Path.GetDirectoryName(dir);
        }
        return false;
    }

    private static string Letters(string s) => new(s.ToLowerInvariant().Where(char.IsLetter).ToArray());

    /// <summary>Re-applies the app rules to every indexed .exe (they changed in 0.7.0; rows are
    /// otherwise only classified when first inserted).</summary>
    public int ReclassifyExecutables()
    {
        var changes = new List<(long id, FileKind kind)>();
        lock (_lock)
        {
            using (var c = Cmd("SELECT id, path, kind FROM files WHERE ext = '.exe';"))
            using (var r = c.ExecuteReader())
                while (r.Read())
                {
                    var kind = ClassifyFile(r.GetString(1), ".exe");
                    if ((int)kind != r.GetInt32(2)) changes.Add((r.GetInt64(0), kind));
                }
            if (changes.Count == 0) return 0;
            using var tx = _conn.BeginTransaction();
            foreach (var (id, kind) in changes)
            {
                using var u = _conn.CreateCommand();
                u.Transaction = tx;
                u.CommandText = "UPDATE files SET kind = $k WHERE id = $id;";
                u.Parameters.AddWithValue("$k", (int)kind);
                u.Parameters.AddWithValue("$id", id);
                u.ExecuteNonQuery();
            }
            tx.Commit();
        }
        return changes.Count;
    }

    private static string DisplayName(string fileName, string ext, FileKind kind) =>
        kind == FileKind.App && ext is ".lnk" or ".url" or ".appref-ms" ? fileName[..^ext.Length] : fileName;

    /// <summary>OneDrive/iCloud "online-only" placeholders: reading them would download the file,
    /// so only their names are indexed.</summary>
    private static bool IsCloudOnly(FileAttributes a)
    {
        const FileAttributes RecallOnOpen = (FileAttributes)0x00040000, RecallOnDataAccess = (FileAttributes)0x00400000;
        return (a & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;
    }

    private bool WantsContent(string path, string ext, long size, FileKind kind)
    {
        if (kind == FileKind.Folder || ext.Length == 0) return false;
        if (!ContentExtractors.Default.CanExtract(ext) || !_settings.WantsContentFor(ext, path)) return false;
        long limit = (ext == ".pdf" ? _settings.MaxPdfFileSizeMB : _settings.MaxContentFileSizeMB) * 1024L * 1024L;
        return size > 0 && size <= limit;
    }

    // ---------------------------------------------------------------- space

    /// <summary>Drops the text of files whose text is no longer wanted (a file type or folder switched
    /// to name-only in Settings, or an index built by an older version that read everything). Their
    /// names stay searchable. Returns how many files lost their text.</summary>
    public int PurgeIneligibleContent()
    {
        var doomed = new List<long>();
        lock (_lock)
        {
            using (var c = Cmd("SELECT id, path, ext, size, kind FROM files WHERE wants_content = 1 OR has_content = 1 OR content_mtime <> 0;"))
            using (var r = c.ExecuteReader())
                while (r.Read())
                    if (!WantsContent(r.GetString(1), r.GetString(2), r.GetInt64(3), (FileKind)r.GetInt32(4)))
                        doomed.Add(r.GetInt64(0));
            if (doomed.Count == 0) return 0;
            using var tx = _conn.BeginTransaction();
            foreach (var chunk in doomed.Chunk(500))
            {
                var ids = string.Join(',', chunk);
                using var d = _conn.CreateCommand();
                d.Transaction = tx;
                d.CommandText = $"""
                    DELETE FROM content_fts WHERE rowid IN ({ids});
                    DELETE FROM texts WHERE id IN ({ids});
                    UPDATE files SET wants_content = 0, has_content = 0, content_chars = 0, content_note = NULL, content_mtime = 0 WHERE id IN ({ids});
                    """;
                d.ExecuteNonQuery();
            }
            tx.Commit();
            _counted = null; // many documents lost their text at once: count again
        }
        return doomed.Count;
    }

    /// <summary>Merges the full-text indexes into one segment each. Deleted documents only leave
    /// tombstones in FTS5 until segments are merged, so after dropping a lot of text this is what
    /// actually frees the space. Rewrites the whole index: run it rarely.</summary>
    public void Optimize()
    {
        lock (_lock)
        {
            Exec(_conn, "INSERT INTO content_fts(content_fts) VALUES('optimize'); INSERT INTO names_fts(names_fts) VALUES('optimize');");
        }
    }

    /// <summary>Gives free pages back to the file system (the index is created with incremental
    /// auto-vacuum; older indexes get it when they are moved with <see cref="Relocate"/>).</summary>
    public void ReleaseSpace()
    {
        lock (_lock)
        {
            Exec(_conn, "PRAGMA incremental_vacuum; PRAGMA wal_checkpoint(TRUNCATE);");
        }
    }

    /// <summary>
    /// Moves the index to <paramref name="toDb"/>: drops text that is no longer wanted, then writes a
    /// compacted copy (VACUUM INTO, so freed space is really gone) and deletes the old files. Nothing
    /// may have the old index open. If the copy fails the old index is left as it was.
    /// </summary>
    public static void Relocate(string fromDb, string toDb, Settings settings, Action<string>? progress = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(toDb))!);
        var tmp = toDb + ".moving";
        File.Delete(tmp);
        using (var store = new IndexStore(fromDb, settings))
        {
            progress?.Invoke("正在清理不需要的正文…");
            store.PurgeIneligibleContent();
            progress?.Invoke("正在合并全文索引…");
            store.Optimize();
            progress?.Invoke("正在写入新位置并压缩…");
            lock (store._lock)
            {
                using var c = store._conn.CreateCommand();
                c.CommandText = "PRAGMA auto_vacuum = INCREMENTAL; VACUUM INTO $p;";
                c.Parameters.AddWithValue("$p", tmp);
                c.ExecuteNonQuery();
            }
        }
        SqliteConnection.ClearAllPools();
        File.Move(tmp, toDb, overwrite: true);
        foreach (var f in new[] { fromDb, fromDb + "-wal", fromDb + "-shm" })
            try { File.Delete(f); } catch (IOException) { }
    }

    // ---------------------------------------------------------------- deletes

    /// <summary>Removes rows under <paramref name="root"/> that were not seen in generation <paramref name="gen"/>.</summary>
    public int DeleteStale(string root, long gen)
    {
        var (lo, hi) = PrefixRange(root);
        return DeleteWhere("(path = $root OR (path >= $lo AND path < $hi)) AND gen < $g",
            ("$root", root), ("$lo", lo), ("$hi", hi), ("$g", gen));
    }

    /// <summary>Removes every row last seen before generation <paramref name="gen"/>.</summary>
    public int DeleteOlderThan(long gen) => DeleteWhere("gen < $g", ("$g", gen));

    /// <summary>Removes a path and, if it was a folder, everything under it.</summary>
    public int DeletePath(string path)
    {
        var (lo, hi) = PrefixRange(path);
        return DeleteWhere("path = $root OR (path >= $lo AND path < $hi)", ("$root", path), ("$lo", lo), ("$hi", hi));
    }

    private int DeleteWhere(string where, params (string, object)[] args)
    {
        bool own = _tx == null;
        if (own) BeginBatch();
        try
        {
            var sql = $"""
                CREATE TEMP TABLE IF NOT EXISTS doomed(id INTEGER PRIMARY KEY);
                DELETE FROM doomed;
                INSERT INTO doomed SELECT id FROM files WHERE {where};
                """;
            using (var pick = Cmd(sql))
            {
                foreach (var (k, v) in args) pick.Parameters.AddWithValue(k, v);
                pick.ExecuteNonQuery();
            }
            using (var lost = Cmd("SELECT COALESCE(SUM(has_content), 0) FROM files WHERE id IN (SELECT id FROM doomed);"))
                _withContentDelta -= Convert.ToInt64(lost.ExecuteScalar());
            sql = """
                DELETE FROM names_fts WHERE rowid IN (SELECT id FROM doomed);
                DELETE FROM content_fts WHERE rowid IN (SELECT id FROM doomed);
                DELETE FROM texts WHERE id IN (SELECT id FROM doomed);
                DELETE FROM files WHERE id IN (SELECT id FROM doomed);
                """;
            using var c = Cmd(sql);
            c.ExecuteNonQuery();
            using var n = Cmd("SELECT COUNT(*) FROM doomed;");
            int count = Convert.ToInt32(n.ExecuteScalar());
            Deleted += count;
            return count;
        }
        finally { if (own) CommitBatch(); }
    }

    /// <summary>Range covering every path strictly inside <paramref name="dir"/>. The separator's
    /// successor character closes the range, so "C:\a" doesn't also match "C:\ab".</summary>
    internal static (string lo, string hi) PrefixRange(string dir)
    {
        // Drive roots ("D:\\") keep their separator after trimming, so add one only when missing.
        string lo = Settings.WithSeparator(Path.TrimEndingDirectorySeparator(dir));
        return (lo, lo[..^1] + (char)(lo[^1] + 1));
    }

    // ---------------------------------------------------------------- contents

    public List<PendingContent> PendingContent(int limit)
    {
        lock (_lock)
        {
            // Documents people actually search for come first; plain text and code last.
            using var c = Cmd("""
                SELECT id, path, ext, size, mtime FROM files WHERE wants_content = 1 AND content_mtime <> mtime
                ORDER BY CASE
                    WHEN ext IN ('.pdf', '.docx', '.docm', '.pptx', '.xlsx', '.xlsm', '.odt', '.odp', '.ods') THEN 0
                    WHEN ext IN ('.md', '.markdown', '.txt', '.rst', '.tex') THEN 1
                    WHEN ext IN ('.html', '.htm') THEN 2
                    ELSE 3 END,
                  mtime DESC
                LIMIT $l;
                """);
            c.Parameters.AddWithValue("$l", limit);
            using var r = c.ExecuteReader();
            var list = new List<PendingContent>();
            while (r.Read()) list.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4)));
            return list;
        }
    }

    /// <summary>The documents among <paramref name="ids"/> whose text still has to be read.</summary>
    public List<PendingContent> PendingContentByIds(IEnumerable<long> ids)
    {
        var list = new List<PendingContent>();
        var all = ids.Distinct().ToList();
        if (all.Count == 0) return list;
        lock (_lock)
        {
            using var c = Cmd($"SELECT id, path, ext, size, mtime FROM files WHERE id IN ({string.Join(',', all)}) AND wants_content = 1 AND content_mtime <> mtime;");
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4)));
        }
        return list;
    }

    public int PendingContentCount()
    {
        lock (_lock)
        {
            using var c = Cmd("SELECT COUNT(*) FROM files WHERE wants_content = 1 AND content_mtime <> mtime;");
            return Convert.ToInt32(c.ExecuteScalar());
        }
    }

    /// <summary>Stores extracted text for a file. Pass null or empty text to mark it done with no content;
    /// <paramref name="note"/> says why text is missing or partial (shown by the file check).</summary>
    public void SetContent(long id, long mtime, string? text, string? note = null) =>
        SetContents(new[] { (id, mtime, text, note) });

    /// <summary>Stores several extraction results in one transaction (much cheaper than one each).</summary>
    public void SetContents(IEnumerable<(long id, long mtime, string? text, string? note)> items)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            void Run(string sql, params (string, object)[] args)
            {
                using var c = _conn.CreateCommand();
                c.Transaction = tx;
                c.CommandText = sql;
                foreach (var (k, v) in args) c.Parameters.AddWithValue(k, v);
                c.ExecuteNonQuery();
            }
            foreach (var (id, mtime, raw, note) in items)
            {
                Run("DELETE FROM content_fts WHERE rowid = $id;", ("$id", id));
                bool has = !string.IsNullOrWhiteSpace(raw);
                using (var was = _conn.CreateCommand())
                {
                    was.Transaction = tx;
                    was.CommandText = "SELECT has_content FROM files WHERE id = $id;";
                    was.Parameters.AddWithValue("$id", id);
                    _withContentDelta += (has ? 1 : 0) - Convert.ToInt64(was.ExecuteScalar() ?? 0L);
                }
                string text = has ? NormalizeWhitespace(raw!) : "";
                if (has)
                {
                    Run("INSERT INTO content_fts(rowid, tokens) VALUES($id, $t);", ("$id", id), ("$t", TextTokenizer.ForIndex(text)));
                    Run("INSERT OR REPLACE INTO texts(id, data) VALUES($id, $d);", ("$id", id), ("$d", Compress(text)));
                }
                else Run("DELETE FROM texts WHERE id = $id;", ("$id", id));
                Run("UPDATE files SET content_mtime = $m, has_content = $h, content_chars = $c, content_note = $n WHERE id = $id;",
                    ("$m", mtime), ("$h", has ? 1 : 0), ("$c", text.Length), ("$n", (object?)note ?? DBNull.Value), ("$id", id));
            }
            tx.Commit();
        }
    }

    internal static string NormalizeWhitespace(string s)
    {
        // Every newline is kept: in PDF text each one ends a page, so a hit's page is the number of
        // newlines before it plus one. Other whitespace runs become a single space.
        var sb = new StringBuilder(s.Length);
        bool ws = false;
        int newlines = 0;
        foreach (var ch in s)
        {
            if (ch == '\n') { newlines++; continue; }
            if (char.IsWhiteSpace(ch) || char.IsControl(ch)) { ws = true; continue; }
            if (newlines > 0) sb.Append('\n', newlines);
            else if (ws && sb.Length > 0) sb.Append(' ');
            ws = false;
            newlines = 0;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    internal static byte[] Compress(string text)
    {
        using var ms = new MemoryStream();
        using (var b = new BrotliStream(ms, CompressionLevel.Fastest))
            b.Write(Encoding.UTF8.GetBytes(text));
        return ms.ToArray();
    }

    internal static string Decompress(byte[] data)
    {
        using var b = new BrotliStream(new MemoryStream(data), CompressionMode.Decompress);
        using var sr = new StreamReader(b, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    // ---------------------------------------------------------------- usage learning

    /// <summary>Remembers that the user opened <paramref name="fileId"/> after typing <paramref name="query"/>.</summary>
    public void RecordLaunch(long fileId, string query)
    {
        lock (_lock)
        {
            long now = DateTime.UtcNow.Ticks;
            using var c = _conn.CreateCommand();
            c.CommandText = """
                UPDATE files SET launch_count = launch_count + 1, last_launch = $now WHERE id = $id;
                INSERT INTO picks(query, file_id, count, last) VALUES($q, $id, 1, $now)
                  ON CONFLICT(query, file_id) DO UPDATE SET count = count + 1, last = $now;
                """;
            c.Parameters.AddWithValue("$id", fileId);
            c.Parameters.AddWithValue("$now", now);
            c.Parameters.AddWithValue("$q", NormalizeQuery(query));
            c.ExecuteNonQuery();
        }
    }

    internal static string NormalizeQuery(string q) => string.Join(' ', TextTokenizer.QueryTerms(q));

    // ---------------------------------------------------------------- diagnostics

    public sealed record FileRecord(long Id, string Path, bool WantsContent, bool ContentRead, bool HasContent, int ContentChars, string? Note, long Size);

    public FileRecord? Find(string path)
    {
        lock (_lock)
        {
            using var c = Cmd("SELECT id, path, wants_content, content_mtime = mtime, has_content, content_chars, content_note, size FROM files WHERE path = $p;");
            c.Parameters.AddWithValue("$p", Path.GetFullPath(path));
            using var r = c.ExecuteReader();
            if (!r.Read()) return null;
            return new FileRecord(r.GetInt64(0), r.GetString(1), r.GetInt64(2) == 1, r.GetInt64(3) == 1, r.GetInt64(4) == 1,
                r.GetInt32(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetInt64(7));
        }
    }

    /// <summary>Whether the stored text of a file contains <paramref name="word"/> (case-insensitive).</summary>
    public bool StoredTextContains(long id, string word)
    {
        lock (_lock)
        {
            using var c = Cmd("SELECT data FROM texts WHERE id = $id;");
            c.Parameters.AddWithValue("$id", id);
            return c.ExecuteScalar() is byte[] d && Decompress(d).Contains(word, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------------------------------------------------------------- stats

    /// <summary>Rows added since the store was opened (for "新增 n 个" after a scan).</summary>
    public long Inserted { get; private set; }

    /// <summary>Rows removed since the store was opened (for "删除 n 个" after a scan).</summary>
    public long Deleted { get; private set; }

    /// <summary>Bytes the index takes on disk (database plus its write-ahead log).</summary>
    public long SizeBytes()
    {
        long total = 0;
        foreach (var f in new[] { DbPath, DbPath + "-wal" })
            try { var i = new FileInfo(f); if (i.Exists) total += i.Length; } catch (IOException) { }
        return total;
    }

    private (long files, long withContent, long inserted, long deleted, long contentDelta, DateTime at)? _counted;
    private long _withContentDelta; // documents gaining text minus losing it, for Counts()

    /// <summary>
    /// Entries and documents with text. Counting reads the whole files table (~150 ms, ~100 MB on a
    /// 400k-file index); the progress line asked twice a second while busy, which added up to
    /// hundreds of GB read per hour. Between real counts (every 10 minutes at most) both numbers
    /// follow inserts, deletes and stored text. <paramref name="exact"/> counts now (before/after a scan).
    /// </summary>
    public (long files, long withContent) Counts(bool exact = false)
    {
        lock (_lock)
        {
            if (!exact && _counted is { } k && DateTime.UtcNow - k.at < TimeSpan.FromMinutes(10))
                return (k.files + (Inserted - k.inserted) - (Deleted - k.deleted), k.withContent + (_withContentDelta - k.contentDelta));
            using var c = Cmd("SELECT COUNT(*), COALESCE(SUM(has_content), 0) FROM files;");
            using var r = c.ExecuteReader();
            r.Read();
            _counted = (r.GetInt64(0), r.GetInt64(1), Inserted, Deleted, _withContentDelta, DateTime.UtcNow);
            return (_counted.Value.files, _counted.Value.withContent);
        }
    }

    public void Clear()
    {
        lock (_lock)
            Exec(_conn, "DELETE FROM names_fts; DELETE FROM content_fts; DELETE FROM texts; DELETE FROM files; DELETE FROM picks;");
    }

    /// <summary>Returns SQLite's page cache and scratch memory to the OS; called when the indexer goes idle.</summary>
    public void ReleaseMemory()
    {
        lock (_lock) Exec(_conn, "PRAGMA shrink_memory;");
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { Exec(_conn, "PRAGMA optimize;"); } catch (SqliteException) { }
            _conn.Dispose();
        }
    }
}
