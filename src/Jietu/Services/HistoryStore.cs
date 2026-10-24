using Microsoft.Data.Sqlite;

namespace Jietu;

public sealed class HistoryStore : IDisposable
{
    // 数据库损坏恢复需要关闭后重新打开，因此不能是 readonly。
    private SqliteConnection _connection;
    private readonly string _imageDirectory;
    private readonly Func<string, bool>? _isFileInUse;
    private readonly object _gate = new();

    /// <summary>历史图片的落盘目录（构造时指定），入库方写文件时从这里取路径。</summary>
    public string ImageDirectory => _imageDirectory;
    private bool _fts;
    // Dispose 后后台 FTS 校验任务不得再触碰连接：进 _gate 后先看这个标记。
    private volatile bool _disposed;
    // 本次构造刚从损坏中重建过：会话内先别做孤儿清理，避免把用户原来的截图当孤儿删掉。
    private bool _recoveredFromCorruption;
    // FTS5 integrity-check 在当前 SQLite 版本不可用时只记一次日志，避免每次启动刷屏。
    private static bool _ftsIntegrityCheckUnavailable;
    // “先写图片文件、再插数据库行”之间存在窗口：2 分钟内新落盘的 PNG 不参与孤儿清理。
    private static readonly TimeSpan OrphanGracePeriod = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 最近一次构造时的数据库恢复说明（如「历史数据库损坏，已备份并重建」），无恢复时为 null。
    /// 供 AppHost 启动后给用户可见提示。
    /// </summary>
    public static string? LastRecoveryMessage { get; private set; }

    static HistoryStore()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    public HistoryStore(string databasePath, string imageDirectory, Func<string, bool>? isFileInUse = null)
    {
        _imageDirectory = imageDirectory;
        _isFileInUse = isFileInUse;
        LastRecoveryMessage = null;
        try
        {
            Directory.CreateDirectory(imageDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        }
        catch (Exception ex)
        {
            // 建目录失败也要让应用能启动（后续写盘会各自失败并记日志）。
            Log.Error("创建历史数据目录失败：" + ex.Message);
        }

        _connection = CreateConnection(databasePath);
        try
        {
            OpenAndInitialize();
        }
        catch (Exception ex)
        {
            _recoveredFromCorruption = true;
            Log.Error("历史数据库初始化失败，尝试备份并重建：" + ex.Message);
            try
            {
                _connection.Dispose();
            }
            catch
            {
                // 关闭失败不影响后续备份重建。
            }

            // 连接池可能仍持有底层 sqlite3 句柄，导致 Windows 上数据库文件无法改名；恢复前清一次池。
            try
            {
                SqliteConnection.ClearAllPools();
            }
            catch
            {
                // 清池失败不影响后续流程（改名失败会记日志）。
            }

            // 坏库改名备份（history.db → history.db.corrupt-yyyyMMdd-HHmmss），再建一份新的。
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var hadDatabaseFile = File.Exists(databasePath);
            var backedUp = BackupDatabase(databasePath, stamp);
            if (hadDatabaseFile && !backedUp)
            {
                // 主库还在但改不了名（多半被其它进程/杀软占用）：不能在残留文件上重建，
                // 否则可能把 -wal 里未落盘的旧数据丢掉；把刚挪走的日志文件挪回去，留给下次启动恢复。
                RestoreBackup(databasePath, stamp);
                LastRecoveryMessage = "历史数据库损坏且文件被占用，未能备份重建，本次历史记录不可用";
                Log.Error(LastRecoveryMessage);
                _connection = CreateConnection(databasePath);
                return;
            }

            _connection = CreateConnection(databasePath);
            try
            {
                OpenAndInitialize();
                LastRecoveryMessage = backedUp
                    ? $"历史数据库损坏，已备份为 {Path.GetFileName(databasePath)}.corrupt-{stamp} 并重建"
                    : "历史数据库损坏，已重建（旧文件备份失败）";
                Log.Info(LastRecoveryMessage);
            }
            catch (Exception retry)
            {
                // 构造函数不能把异常抛到 AppHost，否则会变成“无托盘、无热键、无报错”的沉默启动。
                // 留一个未打开的连接占位：后续查询各自失败并记日志，但应用仍能启动。
                LastRecoveryMessage = "历史数据库损坏且重建失败，本次历史记录不可用（旧文件未删除）";
                Log.Error("历史数据库重建失败：" + retry.Message);
                try
                {
                    _connection.Dispose();
                }
                catch
                {
                    // 占位连接关闭失败可忽略。
                }

                _connection = CreateConnection(databasePath);
            }
        }
    }

    public bool IsLatest(string contentHash)
    {
        if (string.IsNullOrWhiteSpace(contentHash))
        {
            return false;
        }

        lock (_gate)
        {
            return LatestHash() == contentHash;
        }
    }

    public bool ContainsHash(string contentHash)
    {
        if (string.IsNullOrWhiteSpace(contentHash))
        {
            return false;
        }

        lock (_gate)
        {
            return FindHashUnlocked(contentHash) != null;
        }
    }

    public ClipRecord? TryAdd(string kind, string? text, string? imagePath, string contentHash, string source, DateTimeOffset? created = null, byte[]? html = null, byte[]? rtf = null)
    {
        if (string.IsNullOrWhiteSpace(contentHash))
        {
            return null;
        }

        lock (_gate)
        {
            var createdMs = (created ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
            var existing = FindHashUnlocked(contentHash);
            if (existing != null)
            {
                if (IsNewestUnlocked(existing))
                {
                    return null;
                }

                using var update = _connection.CreateCommand();
                update.CommandText = "UPDATE items SET created_utc = $created WHERE id = $id;";
                update.Parameters.AddWithValue("$created", createdMs);
                update.Parameters.AddWithValue("$id", existing);
                update.ExecuteNonQuery();
                return ReadByIdUnlocked(existing);
            }

            var id = Guid.NewGuid().ToString("N");
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO items (id, kind, text, image_path, content_hash, created_utc, source, html, rtf)
                VALUES ($id, $kind, $text, $image, $hash, $created, $source, $html, $rtf);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$text", (object?)text ?? DBNull.Value);
            command.Parameters.AddWithValue("$image", (object?)imagePath ?? DBNull.Value);
            command.Parameters.AddWithValue("$hash", contentHash);
            command.Parameters.AddWithValue("$created", createdMs);
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$html", (object?)html ?? DBNull.Value);
            command.Parameters.AddWithValue("$rtf", (object?)rtf ?? DBNull.Value);
            command.ExecuteNonQuery();
            return new ClipRecord
            {
                Id = id,
                Kind = kind,
                Text = PreviewOf(text),
                ImagePath = imagePath,
                ContentHash = contentHash,
                CreatedUtc = createdMs,
                Source = source,
            };
        }
    }

    public const int PreviewChars = 2000;

    public string? GetText(string id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT text FROM items WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteScalar() as string;
        }
    }

    /// <summary>
    /// 找出还带着老式识别文字（中文之间被 OCR 分词空格隔开）的图片条目，
    /// 交给新管线重跑一遍。识别结果不再符合该特征，所以不会反复被选中。
    /// </summary>
    public IReadOnlyList<(string Id, string ImagePath)> GetLegacyOcrImages(int limit)
    {
        var result = new List<(string, string)>(limit);
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, image_path, text
                FROM items
                WHERE kind = 'image' AND image_path IS NOT NULL AND text IS NOT NULL AND length(text) > 0
                ORDER BY created_utc DESC
                LIMIT 400;
                """;
            using var reader = command.ExecuteReader();
            while (result.Count < limit && reader.Read())
            {
                var path = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                if (HasLegacySpacing(reader.GetString(2)))
                {
                    result.Add((reader.GetString(0), path));
                }
            }
        }

        return result;
    }

    /// <summary>老管线会在每个中文词之间留一个空格，例如「全 部 文 本 图 片」。</summary>
    private static bool HasLegacySpacing(string text)
    {
        for (var i = 1; i < text.Length - 1; i++)
        {
            if (text[i] != ' ')
            {
                continue;
            }

            if (IsCjk(text[i - 1]) && IsCjk(text[i + 1]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCjk(char ch) => ch is >= '\u4E00' and <= '\u9FFF';

    public IReadOnlyList<ClipRecord> Query(string? search, int limit = 5000)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            if (string.IsNullOrWhiteSpace(search))
            {
                command.CommandText = """
                    SELECT id, kind, substr(text, 1, $preview), image_path, content_hash, created_utc, source, pinned
                    FROM items
                    ORDER BY pinned DESC, created_utc DESC
                    LIMIT $limit;
                    """;
            }
            else
            {
                command.CommandText = """
                    SELECT id, kind, substr(text, 1, $preview), image_path, content_hash, created_utc, source, pinned
                    FROM items
                    WHERE 
                    """ + SearchClause(search, command) + """
                    ORDER BY pinned DESC, created_utc DESC
                    LIMIT $limit;
                    """;
            }

            command.Parameters.AddWithValue("$preview", PreviewChars);

            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var list = new List<ClipRecord>();
            while (reader.Read())
            {
                list.Add(Read(reader));
            }

            return list;
        }
    }

    public IReadOnlyList<ClipRecord> QueryPage(string? search, string filter, int limit, HistoryCursor? cursor)
    {
        limit = Math.Clamp(limit, 1, 200);
        filter = filter is "text" or "image" or "shot" or "pinned" ? filter : "all";
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            var sql = """
                SELECT id, kind, substr(text, 1, $preview), image_path, content_hash, created_utc, source, pinned
                FROM items
                WHERE 1 = 1
                """;
            if (filter == "text")
            {
                sql += " AND kind IN ('text', 'rich', 'file')";
            }
            else if (filter == "image")
            {
                sql += " AND kind = 'image' AND source != 'screenshot'";
            }
            else if (filter == "shot")
            {
                sql += " AND source = 'screenshot'";
            }
            else if (filter == "pinned")
            {
                sql += " AND pinned = 1";
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                sql += " AND " + SearchClause(search, command);
            }

            if (cursor is HistoryCursor position)
            {
                // 行值比较让 (pinned, created_utc, id) 三列的索引前缀被完整利用。
                sql += " AND (pinned, created_utc, id) < ($pin, $created, $id)";
                command.Parameters.AddWithValue("$pin", position.Pinned);
                command.Parameters.AddWithValue("$created", position.CreatedUtc);
                command.Parameters.AddWithValue("$id", position.Id);
            }

            sql += " ORDER BY pinned DESC, created_utc DESC, id DESC LIMIT $limit;";
            command.CommandText = sql;
            command.Parameters.AddWithValue("$preview", PreviewChars);
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var list = new List<ClipRecord>();
            while (reader.Read())
            {
                list.Add(Read(reader));
            }

            return list;
        }
    }

    public (byte[]? Html, byte[]? Rtf) GetRich(string id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT html, rtf FROM items WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return (null, null);
            }

            return (Blob(reader, 0), Blob(reader, 1));
        }
    }

    public string? ImagePathForHash(string contentHash)
    {
        if (string.IsNullOrWhiteSpace(contentHash))
        {
            return null;
        }

        lock (_gate)
        {
            var id = FindHashUnlocked(contentHash);
            return id == null ? null : ImagePathOf(id);
        }
    }

    public void SetRecognizedText(string id, string text, bool overwrite = false)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            // 默认只补空记录，避免后台识别覆盖用户已经拿到的文字；
            // 用户主动“重新识别”时传 overwrite 才会覆盖旧结果。
            command.CommandText = overwrite
                ? "UPDATE items SET text = $text WHERE id = $id;"
                : """
                    UPDATE items
                    SET text = $text
                    WHERE id = $id AND (text IS NULL OR text = '');
                    """;
            command.Parameters.AddWithValue("$text", text);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            var path = ImagePathOf(id);
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM items WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
            DeleteFile(path);
        }
    }

    public void SetPinned(string id, bool pinned)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE items SET pinned = $pinned WHERE id = $id;";
            command.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    public void Clear()
    {
        List<string> paths;
        lock (_gate)
        {
            paths = ImagePaths("pinned = 0");
            Execute("DELETE FROM items WHERE pinned = 0;");
        }

        foreach (var path in paths)
        {
            DeleteFile(path);
        }
    }

    public int Purge(int retentionDays)
    {
        var days = Math.Clamp(retentionDays, AppSettings.MinRetentionDays, AppSettings.MaxRetentionDays);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds();
        List<string> paths;
        int deleted;
        lock (_gate)
        {
            paths = ImagePaths("created_utc < $cutoff AND pinned = 0", ("$cutoff", cutoff));
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM items WHERE created_utc < $cutoff AND pinned = 0;";
            command.Parameters.AddWithValue("$cutoff", cutoff);
            deleted = command.ExecuteNonQuery();
        }

        foreach (var path in paths)
        {
            DeleteFile(path);
        }

        // 本次会话刚从损坏中重建（库里没有旧记录）时先不清理孤儿图片：
        // 否则会把用户原来的截图全当孤儿删掉；等下次正常启动再清理。
        if (!_recoveredFromCorruption)
        {
            DeleteOrphanFiles();
        }

        return deleted;
    }

    public void Dispose()
    {
        // 与其他操作一致持 _gate：防止后台 OCR 回写、后台 FTS 校验等还在执行 SQL 时连接被中途关掉。
        lock (_gate)
        {
            _disposed = true;
            _connection.Dispose();
        }
    }

    /// <summary>打开连接并完成 PRAGMA、自检与建表；任一步失败都抛给构造函数走备份重建流程。</summary>
    private void OpenAndInitialize()
    {
        _connection.Open();
        // busy_timeout 必须先于 journal_mode：切换 WAL/建表撞上锁时才有等待窗口，
        // 否则会直接抛 SQLITE_BUSY，被误判成数据库损坏而触发备份重建。
        Execute("PRAGMA busy_timeout=2000;");
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=NORMAL;");
        Execute("PRAGMA cache_size=-8000;");
        Execute("PRAGMA temp_store=MEMORY;");
        Execute("PRAGMA mmap_size=268435456;");
        QuickCheck();
        CreateSchema();
        _fts = TryEnableSearch();
    }

    /// <summary>启动时做一次低成本自检；结果不是 ok 时抛异常，由构造函数备份重建。</summary>
    private void QuickCheck()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        var result = command.ExecuteScalar() as string;
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("历史数据库自检失败：" + (result ?? "未知结果"));
        }
    }

    private void CreateSchema()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS items (
              id TEXT PRIMARY KEY,
              kind TEXT NOT NULL,
              text TEXT,
              image_path TEXT,
              content_hash TEXT NOT NULL,
              created_utc INTEGER NOT NULL,
              source TEXT NOT NULL
            );
            """);
        Execute("CREATE INDEX IF NOT EXISTS ix_items_created ON items(created_utc DESC);");
        EnsureColumn("pinned", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("html", "BLOB");
        EnsureColumn("rtf", "BLOB");
        Execute("CREATE INDEX IF NOT EXISTS ix_items_list ON items(pinned DESC, created_utc DESC, id DESC);");
        Execute("CREATE INDEX IF NOT EXISTS ix_items_source_list ON items(source, pinned DESC, created_utc DESC, id DESC);");
        Execute("CREATE INDEX IF NOT EXISTS ix_items_kind_list ON items(kind, pinned DESC, created_utc DESC, id DESC);");
        Execute("CREATE INDEX IF NOT EXISTS ix_items_hash ON items(content_hash);");
        // 单独按 created_utc + id 取最新一条时需要复合索引，否则会退化成全表扫描 + 临时排序。
        Execute("CREATE INDEX IF NOT EXISTS ix_items_newest ON items(created_utc DESC, id DESC);");
    }

    /// <summary>创建连接。关闭连接池：恢复流程要在 Dispose 之后立刻给数据库文件改名备份，
    /// 池化会让底层 sqlite3 句柄继续占着文件，Windows 上改不了名。</summary>
    private static SqliteConnection CreateConnection(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        };
        return new SqliteConnection(builder.ToString());
    }

    /// <summary>
    /// 把损坏的库文件改名备份：主库 → 原名.corrupt-yyyyMMdd-HHmmss，-wal/-shm 用同前缀加后缀。
    /// 尽力而为，单个文件失败只记日志；返回主库是否备份成功。
    /// </summary>
    private static bool BackupDatabase(string databasePath, string stamp)
    {
        var suffix = ".corrupt-" + stamp;
        // 先挪 -wal/-shm 再挪主库：主库改名后若 -wal 残留，重新打开会读到与库不匹配的日志。
        MoveFileIfExists(databasePath + "-wal", databasePath + suffix + "-wal");
        MoveFileIfExists(databasePath + "-shm", databasePath + suffix + "-shm");
        return MoveFileIfExists(databasePath, databasePath + suffix);
    }

    /// <summary>放弃恢复时把已经挪走的 -wal/-shm 挪回原名，尽量保持文件原状。</summary>
    private static void RestoreBackup(string databasePath, string stamp)
    {
        var suffix = ".corrupt-" + stamp;
        MoveFileIfExists(databasePath + suffix + "-wal", databasePath + "-wal");
        MoveFileIfExists(databasePath + suffix + "-shm", databasePath + "-shm");
    }

    private static bool MoveFileIfExists(string source, string target)
    {
        try
        {
            if (!File.Exists(source))
            {
                return false;
            }

            File.Move(source, target, overwrite: false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("备份损坏的数据库文件失败：" + source + " → " + target + "，" + ex.Message);
            return false;
        }
    }

    private string? LatestHash()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT content_hash FROM items ORDER BY created_utc DESC, id DESC LIMIT 1;";
        return command.ExecuteScalar() as string;
    }

    private string? FindHashUnlocked(string contentHash)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id FROM items WHERE content_hash = $hash ORDER BY created_utc DESC, id DESC LIMIT 1;";
        command.Parameters.AddWithValue("$hash", contentHash);
        return command.ExecuteScalar() as string;
    }

    private bool IsNewestUnlocked(string id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id FROM items ORDER BY created_utc DESC, id DESC LIMIT 1;";
        return (command.ExecuteScalar() as string) == id;
    }

    private ClipRecord? ReadByIdUnlocked(string id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT id, kind, substr(text, 1, $preview), image_path, content_hash, created_utc, source, pinned
            FROM items
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$preview", PreviewChars);
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private string? ImagePathOf(string id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT image_path FROM items WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string;
    }

    private List<string> ImagePaths(string? where = null, params (string Name, object Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT image_path FROM items WHERE image_path IS NOT NULL";
        if (!string.IsNullOrEmpty(where))
        {
            command.CommandText += " AND " + where;
        }

        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        using var reader = command.ExecuteReader();
        var paths = new List<string>();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                paths.Add(reader.GetString(0));
            }
        }

        return paths;
    }

    private void DeleteOrphanFiles()
    {
        if (!Directory.Exists(_imageDirectory))
        {
            return;
        }

        HashSet<string> known;
        lock (_gate)
        {
            known = ImagePaths().ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var cutoff = DateTime.UtcNow - OrphanGracePeriod;
            foreach (var file in Directory.EnumerateFiles(_imageDirectory, "*.png"))
            {
                if (known.Contains(file))
                {
                    continue;
                }

                // 文件太新说明可能正处在「已写盘、还没插行」的窗口里，这次先跳过，下一轮再判断。
                try
                {
                    if (File.GetLastWriteTimeUtc(file) > cutoff)
                    {
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    Log.Info("读取图片写入时间失败，跳过清理：" + ex.Message);
                    continue;
                }

                if (_isFileInUse?.Invoke(file) == true)
                {
                    continue;
                }

                DeleteFile(file);
            }
        }
        catch (Exception ex)
        {
            Log.Info("清理孤儿图片失败：" + ex.Message);
        }
    }

    private static ClipRecord Read(SqliteDataReader reader)
    {
        return new ClipRecord
        {
            Id = reader.GetString(0),
            Kind = reader.GetString(1),
            Text = reader.IsDBNull(2) ? null : reader.GetString(2),
            ImagePath = reader.IsDBNull(3) ? null : reader.GetString(3),
            ContentHash = reader.GetString(4),
            CreatedUtc = reader.GetInt64(5),
            Source = reader.GetString(6),
            Pinned = !reader.IsDBNull(7) && reader.GetInt64(7) != 0,
        };
    }

    private void EnsureColumn(string name, string definition)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('items') WHERE name = $name;";
        command.Parameters.AddWithValue("$name", name);
        var exists = Convert.ToInt32(command.ExecuteScalar()) > 0;
        if (!exists)
        {
            Execute("ALTER TABLE items ADD COLUMN " + name + " " + definition + ";");
        }
    }

    private static byte[]? Blob(SqliteDataReader reader, int index)
    {
        return reader.IsDBNull(index) ? null : (byte[])reader.GetValue(index);
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string? PreviewOf(string? text)
    {
        if (text == null || text.Length <= PreviewChars)
        {
            return text;
        }

        return text[..PreviewChars];
    }

    /// <summary>
    /// 建立全文索引。
    /// 外部内容表（content='items'）+ 触发器方案：索引与主表由数据库保证同步，
    /// 不再依赖应用层手工维护，既避免索引与主表不同步，也省掉一份正文副本。
    /// </summary>
    private bool TryEnableSearch()
    {
        try
        {
            // 两种历史结构都要重建：
            //   1) 老版本是独立复制表（带 id 列），不是外部内容表；
            //   2) 上一版把 FTS 列名写成了 body，而主表列名是 text —— 外部内容表会去
            //      主表里找 items.body，于是每次查询都报 "no such column: T.body"。
            var migrated = false;
            var existing = FtsTableSql();
            if (existing != null
                && (!existing.Contains("content='items'", StringComparison.OrdinalIgnoreCase)
                    || existing.Contains("body", StringComparison.OrdinalIgnoreCase)))
            {
                Execute("DROP TABLE IF EXISTS items_fts;");
                migrated = true;
            }

            // 列名必须与主表一致，外部内容表才能按 rowid 回主表取值。
            Execute("""
                CREATE VIRTUAL TABLE IF NOT EXISTS items_fts
                USING fts5("text", content='items', content_rowid='rowid', tokenize = 'trigram');
                """);

            // 触发器必须随表一起重建：旧库的触发器里写的是 items_fts(rowid, body)，
            // 若沿用 IF NOT EXISTS 保留下来，之后每条剪贴板写入都会报 "no column named body"。
            Execute("DROP TRIGGER IF EXISTS items_fts_ai;");
            Execute("DROP TRIGGER IF EXISTS items_fts_ad;");
            Execute("DROP TRIGGER IF EXISTS items_fts_au;");

            // 所有行都要进索引（包括 text 为 NULL 的图片行）：外部内容表的深度 integrity-check
            // 要求内容表每一行都在索引里，只灌非 NULL 行会让校验永远失败、每次启动都重建。
            Execute("""
                CREATE TRIGGER items_fts_ai AFTER INSERT ON items BEGIN
                  INSERT INTO items_fts(rowid, "text") VALUES (new.rowid, new.text);
                END;
                """);
            Execute("""
                CREATE TRIGGER items_fts_ad AFTER DELETE ON items BEGIN
                  INSERT INTO items_fts(items_fts, rowid, "text") VALUES ('delete', old.rowid, old.text);
                END;
                """);
            Execute("""
                CREATE TRIGGER items_fts_au AFTER UPDATE OF text ON items BEGIN
                  INSERT INTO items_fts(items_fts, rowid, "text") VALUES ('delete', old.rowid, old.text);
                  INSERT INTO items_fts(rowid, "text") VALUES (new.rowid, new.text);
                END;
                """);

            // 迁移过才同步重建：刚删掉旧结构索引，重建完成前任何搜索都是空结果，
            // 且旧库升级的回归自测要求“构造完立刻可搜”，必须在构造线程做完。
            if (migrated)
            {
                RebuildFtsIndex();
            }
            else
            {
                // 冷启动提速：深度 integrity-check 是 O(整个索引) 的扫描，历史多时要几百毫秒，
                // 不能卡在构造线程上。先信任索引可用（触发器一直在维护它），校验与按需重建放后台。
                VerifyFtsInBackground();
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Info("全文搜索不可用，改用普通搜索：" + ex.Message);
            // 索引维护/重建失败时摘掉触发器：否则后续写入会因为索引与主表不一致，
            // 在触发器里的 'delete' 命令上报错，连带剪贴板入库一起失败。
            TryDropSearchTriggers();
            return false;
        }
    }

    private void TryDropSearchTriggers()
    {
        foreach (var name in new[] { "items_fts_ai", "items_fts_ad", "items_fts_au" })
        {
            try
            {
                Execute("DROP TRIGGER IF EXISTS " + name + ";");
            }
            catch
            {
                // 清触发器失败不影响主流程（下次启动会再尝试重建）。
            }
        }
    }

    /// <summary>按内容表重建整份 FTS 索引（含 text 为 NULL 的行，口径与深度校验一致）。</summary>
    private void RebuildFtsIndex()
    {
        // 外部内容表支持 'rebuild'：不要用 delete-all + 手工 INSERT 非 NULL 行，
        // 那样会与深度校验的口径不一致。
        Execute("INSERT INTO items_fts(items_fts) VALUES('rebuild');");
    }

    /// <summary>
    /// 在后台线程校验 FTS 索引，发现不同步就在后台重建；重建也失败才摘触发器退回 LIKE。
    /// 所有 SQL 都持 _gate：与用户查询/写入串行执行，不存在“后台校验与用户查询并发访问连接”
    /// 的崩溃面。代价最坏是某次用户查询多等几百毫秒（校验期间锁被持有）。
    /// 启动到后台校验完成之间若索引本来就陈旧（如上次异常退出），搜索可能短暂漏结果，
    /// 重建完成后自动恢复；正常情况下触发器保证索引始终同步，这段窗口没有实际影响。
    /// </summary>
    private void VerifyFtsInBackground()
    {
        _ = Task.Run(() =>
        {
            try
            {
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    if (!FtsInSync())
                    {
                        RebuildFtsIndex();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info("全文索引后台校验/重建失败，改用普通搜索：" + ex.Message);
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _fts = false;
                    TryDropSearchTriggers();
                }
            }
        });
    }

    /// <summary>
    /// 校验索引是否与主表同步。
    /// 不能用行数比较：items_fts 是外部内容表，不带 MATCH 的 COUNT(*) 会直通内容表
    /// （返回 items 总行数，包含 text 为 NULL 的图片行），既不反映索引真实条数，
    /// 又会在存在 NULL 行时永远判成“不同步”，导致每次启动全量重建。
    /// 改用 FTS5 的深度 integrity-check（rank=1）：它逐行比对索引与内容表，
    /// 触发器丢失、索引陈旧或索引损坏都会在这里暴露（实测 SQLite 3.49 抛 "database disk image is malformed"）。
    /// </summary>
    private bool FtsInSync()
    {
        try
        {
            // 空库没有可校验的内容，直接算同步，省掉每次启动的整索引扫描。
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM items;";
                if (Convert.ToInt64(command.ExecuteScalar()) == 0)
                {
                    return true;
                }
            }

            using var check = _connection.CreateCommand();
            check.CommandText = "INSERT INTO items_fts(items_fts, rank) VALUES('integrity-check', 1);";
            check.ExecuteNonQuery();
            return true;
        }
        catch (Exception ex)
        {
            // 该命令在当前 SQLite 版本不可用（或索引确实陈旧/损坏）时按“需要重建”处理，日志只记一次。
            if (!_ftsIntegrityCheckUnavailable)
            {
                _ftsIntegrityCheckUnavailable = true;
                Log.Info("全文索引完整性校验未通过，将重建索引：" + ex.Message);
            }

            return false;
        }
    }

    /// <summary>
    /// 取出现有 items_fts 的建表语句；不存在时返回 null。
    /// </summary>
    private string? FtsTableSql()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'items_fts';";
        return command.ExecuteScalar() as string;
    }

    private string SearchClause(string search, SqliteCommand command)
    {
        var term = search.Trim();
        if (_fts && term.Length >= 3)
        {
            command.Parameters.AddWithValue("$match", "\"" + term.Replace("\"", "\"\"") + "\"");
            // 外部内容表通过 rowid 与主表关联。
            return "rowid IN (SELECT rowid FROM items_fts WHERE items_fts MATCH $match)";
        }

        command.Parameters.AddWithValue("$like", "%" + EscapeLike(term) + "%");
        return "text LIKE $like ESCAPE '\\'";
    }

    private static string EscapeLike(string text)
    {
        return text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    }

    private void DeleteFile(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || _isFileInUse?.Invoke(path) == true)
            {
                return;
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (!string.IsNullOrEmpty(path))
            {
                var preview = ClipRow.PreviewPath(path);
                if (File.Exists(preview))
                {
                    File.Delete(preview);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Info("删除图片失败：" + ex.Message);
        }
    }
}
