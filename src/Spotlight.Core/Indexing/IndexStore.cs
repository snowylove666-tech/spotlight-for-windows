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

    public long NextGeneration()
    {
        lock (_lock)
        {
            using var c = Cmd("SELECT COALESCE(MAX(gen), 0) + 1 FROM files;");
            return Convert.ToInt64(c.ExecuteScalar());
        }
    }

    // ---------------------------------------------------------------- upserts (call inside a batch)

    /// <summary>Inserts or refreshes one entry. Must be called between BeginBatch and CommitBatch.</summary>
    public void Upsert(FileSystemInfo info, long gen)
    {
        bool isDir = info is DirectoryInfo;
        string path = info.FullName;
        string ext = isDir ? "" : info.Extension.ToLowerInvariant();
        long size = isDir ? 0 : ((FileInfo)info).Length;
        long mtime = info.LastWriteTimeUtc.Ticks;
        var kind = isDir ? FileKind.Folder : ClassifyFile(path, ext);
        string display = DisplayName(info.Name, ext, kind);
        bool wants = WantsContent(ext, size, kind) && !IsCloudOnly(info.Attributes);

        using (var sel = Cmd("SELECT id, mtime, size FROM files WHERE path = $p;"))
        {
            sel.Parameters.AddWithValue("$p", path);
            using var r = sel.ExecuteReader();
            if (r.Read())
            {
                long id = r.GetInt64(0);
                bool changed = r.GetInt64(1) != mtime || r.GetInt64(2) != size;
                r.Close();
                using var upd = Cmd(changed
                    ? "UPDATE files SET gen = $g, mtime = $m, size = $s, wants_content = $w WHERE id = $id;"
                    // wants_content is refreshed too: a cloud placeholder may have been downloaded since.
                    : "UPDATE files SET gen = $g, wants_content = $w WHERE id = $id;");
                upd.Parameters.AddWithValue("$g", gen);
                upd.Parameters.AddWithValue("$id", id);
                upd.Parameters.AddWithValue("$w", wants ? 1 : 0);
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
            INSERT INTO files(path, name, name_lower, ext, kind, size, mtime, gen, wants_content)
            VALUES($p, $n, $nl, $e, $k, $s, $m, $g, $w) RETURNING id;
            """))
        {
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
        if (kind == FileKind.App && ext == ".exe" && !_appRoots.Any(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
            return FileKind.Other;
        return kind;
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

    private bool WantsContent(string ext, long size, FileKind kind)
    {
        if (!_settings.IndexContents || kind == FileKind.Folder || ext.Length == 0) return false;
        if (!ContentExtractors.Default.CanExtract(ext)) return false;
        long limit = (ext == ".pdf" ? _settings.MaxPdfFileSizeMB : _settings.MaxContentFileSizeMB) * 1024L * 1024L;
        return size > 0 && size <= limit;
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
                DELETE FROM names_fts WHERE rowid IN (SELECT id FROM doomed);
                DELETE FROM content_fts WHERE rowid IN (SELECT id FROM doomed);
                DELETE FROM texts WHERE id IN (SELECT id FROM doomed);
                DELETE FROM files WHERE id IN (SELECT id FROM doomed);
                """;
            using var c = Cmd(sql);
            foreach (var (k, v) in args) c.Parameters.AddWithValue(k, v);
            c.ExecuteNonQuery();
            using var n = Cmd("SELECT COUNT(*) FROM doomed;");
            return Convert.ToInt32(n.ExecuteScalar());
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

    public (long files, long withContent) Counts()
    {
        lock (_lock)
        {
            using var c = Cmd("SELECT COUNT(*), COALESCE(SUM(has_content), 0) FROM files;");
            using var r = c.ExecuteReader();
            r.Read();
            return (r.GetInt64(0), r.GetInt64(1));
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
