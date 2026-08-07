using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ClashTrafficSentinel
{
    internal sealed class TrafficDatabase : IDisposable
    {
        private readonly object sync = new object();
        private NativeSqlite database;

        public TrafficDatabase(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Database path is required.");
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                throw new DirectoryNotFoundException("数据目录不存在");
            database = new NativeSqlite(fullPath);
            Initialize();
        }

        private void Initialize()
        {
            lock (sync)
            {
                database.Execute("PRAGMA journal_mode=WAL;");
                database.Execute("PRAGMA synchronous=NORMAL;");
                database.Execute("PRAGMA busy_timeout=5000;");
                database.Execute(
                    "CREATE TABLE IF NOT EXISTS settings (" +
                    "key TEXT PRIMARY KEY, value TEXT NOT NULL);" +
                    "CREATE TABLE IF NOT EXISTS traffic_daily (" +
                    "local_date TEXT NOT NULL, app_key TEXT NOT NULL, app_name TEXT NOT NULL, " +
                    "app_path TEXT NOT NULL, domain TEXT NOT NULL, upload_bytes INTEGER NOT NULL, " +
                    "download_bytes INTEGER NOT NULL, PRIMARY KEY(local_date, app_key, domain));" +
                    "CREATE INDEX IF NOT EXISTS ix_traffic_daily_date ON traffic_daily(local_date);" +
                    "CREATE TABLE IF NOT EXISTS traffic_minute (" +
                    "minute_key TEXT NOT NULL, app_key TEXT NOT NULL, app_name TEXT NOT NULL, " +
                    "upload_bytes INTEGER NOT NULL, download_bytes INTEGER NOT NULL, " +
                    "PRIMARY KEY(minute_key, app_key));" +
                    "CREATE INDEX IF NOT EXISTS ix_traffic_minute_key ON traffic_minute(minute_key);" +
                    "CREATE TABLE IF NOT EXISTS alert_app (" +
                    "app_key TEXT PRIMARY KEY, last_alert_utc INTEGER NOT NULL);" +
                    "CREATE TABLE IF NOT EXISTS alert_daily (" +
                    "local_date TEXT PRIMARY KEY, milestone INTEGER NOT NULL);"
                );
            }
        }

        public void RecordBatch(IList<TrafficDelta> deltas)
        {
            if (deltas == null || deltas.Count == 0) return;
            lock (sync)
            {
                database.Execute("BEGIN IMMEDIATE;");
                try
                {
                    foreach (TrafficDelta delta in deltas)
                    {
                        if (delta == null || delta.Total <= 0) continue;
                        database.NonQuery(
                            "INSERT INTO traffic_daily(local_date,app_key,app_name,app_path,domain,upload_bytes,download_bytes) " +
                            "VALUES(?,?,?,?,?,?,?) ON CONFLICT(local_date,app_key,domain) DO UPDATE SET " +
                            "app_name=excluded.app_name,app_path=excluded.app_path," +
                            "upload_bytes=upload_bytes+excluded.upload_bytes," +
                            "download_bytes=download_bytes+excluded.download_bytes;",
                            delta.LocalTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            delta.AppKey, delta.AppName, delta.AppPath, delta.Domain,
                            delta.Upload, delta.Download);
                        database.NonQuery(
                            "INSERT INTO traffic_minute(minute_key,app_key,app_name,upload_bytes,download_bytes) " +
                            "VALUES(?,?,?,?,?) ON CONFLICT(minute_key,app_key) DO UPDATE SET " +
                            "app_name=excluded.app_name,upload_bytes=upload_bytes+excluded.upload_bytes," +
                            "download_bytes=download_bytes+excluded.download_bytes;",
                            delta.LocalTime.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture),
                            delta.AppKey, delta.AppName, delta.Upload, delta.Download);
                    }
                    database.Execute("COMMIT;");
                }
                catch
                {
                    try { database.Execute("ROLLBACK;"); } catch { }
                    throw;
                }
            }
        }

        public void PruneOperationalData(DateTime localNow)
        {
            string cutoff = localNow.AddDays(-2).ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
            lock (sync)
            {
                database.NonQuery("DELETE FROM traffic_minute WHERE minute_key < ?;", cutoff);
                long alertCutoff = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();
                database.NonQuery("DELETE FROM alert_app WHERE last_alert_utc < ?;", alertCutoff);
            }
        }

        public TrafficSummary GetSummary(DateTime fromLocal, DateTime toLocal, string dimension, string appKey)
        {
            string from = fromLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string to = toLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            lock (sync)
            {
                TrafficSummary summary = new TrafficSummary();
                List<object[]> totals = database.Query(
                    "SELECT COALESCE(SUM(upload_bytes),0),COALESCE(SUM(download_bytes),0) " +
                    "FROM traffic_daily WHERE local_date>=? AND local_date<?" +
                    (string.IsNullOrEmpty(appKey) ? ";" : " AND app_key=?;"),
                    string.IsNullOrEmpty(appKey) ? new object[] { from, to } : new object[] { from, to, appKey });
                if (totals.Count > 0)
                {
                    summary.Upload = ToLong(totals[0][0]);
                    summary.Download = ToLong(totals[0][1]);
                }

                string sql;
                object[] parameters;
                if (!string.IsNullOrEmpty(appKey))
                {
                    sql = "SELECT domain,domain,SUM(upload_bytes),SUM(download_bytes) FROM traffic_daily " +
                          "WHERE local_date>=? AND local_date<? AND app_key=? GROUP BY domain " +
                          "ORDER BY SUM(upload_bytes+download_bytes) DESC LIMIT 8;";
                    parameters = new object[] { from, to, appKey };
                }
                else if (string.Equals(dimension, "domain", StringComparison.OrdinalIgnoreCase))
                {
                    sql = "SELECT domain,domain,SUM(upload_bytes),SUM(download_bytes) FROM traffic_daily " +
                          "WHERE local_date>=? AND local_date<? GROUP BY domain " +
                          "ORDER BY SUM(upload_bytes+download_bytes) DESC LIMIT 8;";
                    parameters = new object[] { from, to };
                }
                else
                {
                    sql = "SELECT app_key,app_name,SUM(upload_bytes),SUM(download_bytes) FROM traffic_daily " +
                          "WHERE local_date>=? AND local_date<? GROUP BY app_key,app_name " +
                          "ORDER BY SUM(upload_bytes+download_bytes) DESC LIMIT 8;";
                    parameters = new object[] { from, to };
                }

                foreach (object[] row in database.Query(sql, parameters))
                {
                    summary.Rows.Add(new TrafficRow
                    {
                        Key = Convert.ToString(row[0], CultureInfo.InvariantCulture),
                        Label = Convert.ToString(row[1], CultureInfo.InvariantCulture),
                        Secondary = string.Empty,
                        Upload = ToLong(row[2]),
                        Download = ToLong(row[3])
                    });
                }
                return summary;
            }
        }

        public long GetRecentAppBytes(string appKey, DateTime localNow, int minutes)
        {
            DateTime firstMinute = new DateTime(localNow.Year, localNow.Month, localNow.Day,
                localNow.Hour, localNow.Minute, 0).AddMinutes(-(Math.Max(1, minutes) - 1));
            lock (sync)
            {
                object value = database.Scalar(
                    "SELECT COALESCE(SUM(upload_bytes+download_bytes),0) FROM traffic_minute " +
                    "WHERE minute_key>=? AND app_key=?;",
                    firstMinute.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture), appKey);
                return ToLong(value);
            }
        }

        public long GetTodayTotal(DateTime localNow)
        {
            string date = localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            lock (sync)
            {
                return ToLong(database.Scalar(
                    "SELECT COALESCE(SUM(upload_bytes+download_bytes),0) FROM traffic_daily WHERE local_date=?;", date));
            }
        }

        public DateTimeOffset GetLastAppAlert(string appKey)
        {
            lock (sync)
            {
                long value = ToLong(database.Scalar("SELECT last_alert_utc FROM alert_app WHERE app_key=?;", appKey));
                return value > 0 ? DateTimeOffset.FromUnixTimeSeconds(value) : DateTimeOffset.MinValue;
            }
        }

        public void SetLastAppAlert(string appKey, DateTimeOffset value)
        {
            lock (sync)
            {
                database.NonQuery(
                    "INSERT INTO alert_app(app_key,last_alert_utc) VALUES(?,?) " +
                    "ON CONFLICT(app_key) DO UPDATE SET last_alert_utc=excluded.last_alert_utc;",
                    appKey, value.ToUnixTimeSeconds());
            }
        }

        public int GetDailyMilestone(DateTime localNow)
        {
            string date = localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            lock (sync)
            {
                return (int)ToLong(database.Scalar("SELECT milestone FROM alert_daily WHERE local_date=?;", date));
            }
        }

        public void SetDailyMilestone(DateTime localNow, int milestone)
        {
            string date = localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            lock (sync)
            {
                database.NonQuery(
                    "INSERT INTO alert_daily(local_date,milestone) VALUES(?,?) " +
                    "ON CONFLICT(local_date) DO UPDATE SET milestone=excluded.milestone;", date, milestone);
            }
        }

        public MonitorSettings LoadSettings()
        {
            MonitorSettings settings = new MonitorSettings();
            lock (sync)
            {
                settings.BurstMegabytes = ParseInt(GetSetting("burst_mb"), settings.BurstMegabytes);
                settings.DailyGigabytes = ParseDouble(GetSetting("daily_gb"), settings.DailyGigabytes);
                settings.AppCooldownMinutes = ParseInt(GetSetting("cooldown_minutes"), settings.AppCooldownMinutes);
            }
            settings.Clamp();
            return settings;
        }

        public void SaveSettings(MonitorSettings settings)
        {
            if (settings == null) return;
            settings.Clamp();
            lock (sync)
            {
                database.Execute("BEGIN IMMEDIATE;");
                try
                {
                    SetSetting("burst_mb", settings.BurstMegabytes.ToString(CultureInfo.InvariantCulture));
                    SetSetting("daily_gb", settings.DailyGigabytes.ToString("0.###", CultureInfo.InvariantCulture));
                    SetSetting("cooldown_minutes", settings.AppCooldownMinutes.ToString(CultureInfo.InvariantCulture));
                    database.Execute("COMMIT;");
                }
                catch
                {
                    try { database.Execute("ROLLBACK;"); } catch { }
                    throw;
                }
            }
        }

        private string GetSetting(string key)
        {
            object value = database.Scalar("SELECT value FROM settings WHERE key=?;", key);
            return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private void SetSetting(string key, string value)
        {
            database.NonQuery(
                "INSERT INTO settings(key,value) VALUES(?,?) " +
                "ON CONFLICT(key) DO UPDATE SET value=excluded.value;", key, value);
        }

        private static int ParseInt(string value, int fallback)
        {
            int parsed;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        private static double ParseDouble(string value, double fallback)
        {
            double parsed;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        private static long ToLong(object value)
        {
            if (value == null || value == DBNull.Value) return 0;
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (database != null)
                {
                    database.Dispose();
                    database = null;
                }
            }
        }
    }

    internal sealed class NativeSqlite : IDisposable
    {
        private const int SqliteOk = 0;
        private const int SqliteRow = 100;
        private const int SqliteDone = 101;
        private const int OpenReadWrite = 0x00000002;
        private const int OpenCreate = 0x00000004;
        private const int OpenFullMutex = 0x00010000;
        private static readonly IntPtr SqliteTransient = new IntPtr(-1);
        private IntPtr handle;

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr zVfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close_v2(IntPtr db);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_errmsg(IntPtr db);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr arg, out IntPtr error);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void sqlite3_free(IntPtr value);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_step(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_finalize(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_bind_null(IntPtr statement, int index);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_bind_double(IntPtr statement, int index, double value);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_bind_text(IntPtr statement, int index, byte[] value, int bytes, IntPtr destructor);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_count(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_type(IntPtr statement, int column);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern long sqlite3_column_int64(IntPtr statement, int column);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern double sqlite3_column_double(IntPtr statement, int column);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_bytes(IntPtr statement, int column);

        public NativeSqlite(string path)
        {
            byte[] filename = Utf8Null(path);
            int result = sqlite3_open_v2(filename, out handle, OpenReadWrite | OpenCreate | OpenFullMutex, IntPtr.Zero);
            if (result != SqliteOk)
            {
                string message = ErrorMessage();
                if (handle != IntPtr.Zero) sqlite3_close_v2(handle);
                handle = IntPtr.Zero;
                throw new InvalidOperationException("无法打开本地数据库：" + message);
            }
            sqlite3_busy_timeout(handle, 5000);
        }

        public void Execute(string sql)
        {
            IntPtr error;
            int result = sqlite3_exec(handle, Utf8Null(sql), IntPtr.Zero, IntPtr.Zero, out error);
            if (result == SqliteOk) return;
            string message = error == IntPtr.Zero ? ErrorMessage() : PtrToUtf8(error, -1);
            if (error != IntPtr.Zero) sqlite3_free(error);
            throw new InvalidOperationException("数据库操作失败：" + message);
        }

        public void NonQuery(string sql, params object[] parameters)
        {
            using (SqliteStatement statement = Prepare(sql, parameters))
            {
                int result = sqlite3_step(statement.Handle);
                if (result != SqliteDone) throw new InvalidOperationException("数据库写入失败：" + ErrorMessage());
            }
        }

        public object Scalar(string sql, params object[] parameters)
        {
            using (SqliteStatement statement = Prepare(sql, parameters))
            {
                int result = sqlite3_step(statement.Handle);
                if (result == SqliteDone) return null;
                if (result != SqliteRow) throw new InvalidOperationException("数据库查询失败：" + ErrorMessage());
                return ReadColumn(statement.Handle, 0);
            }
        }

        public List<object[]> Query(string sql, params object[] parameters)
        {
            List<object[]> rows = new List<object[]>();
            using (SqliteStatement statement = Prepare(sql, parameters))
            {
                int columns = sqlite3_column_count(statement.Handle);
                while (true)
                {
                    int result = sqlite3_step(statement.Handle);
                    if (result == SqliteDone) break;
                    if (result != SqliteRow) throw new InvalidOperationException("数据库查询失败：" + ErrorMessage());
                    object[] row = new object[columns];
                    for (int i = 0; i < columns; i++) row[i] = ReadColumn(statement.Handle, i);
                    rows.Add(row);
                }
            }
            return rows;
        }

        private SqliteStatement Prepare(string sql, object[] parameters)
        {
            IntPtr statement;
            byte[] bytes = Utf8Null(sql);
            int result = sqlite3_prepare_v2(handle, bytes, bytes.Length - 1, out statement, IntPtr.Zero);
            if (result != SqliteOk) throw new InvalidOperationException("数据库语句无效：" + ErrorMessage());
            SqliteStatement wrapper = new SqliteStatement(statement);
            try
            {
                if (parameters != null)
                {
                    for (int i = 0; i < parameters.Length; i++) Bind(statement, i + 1, parameters[i]);
                }
                return wrapper;
            }
            catch
            {
                wrapper.Dispose();
                throw;
            }
        }

        private void Bind(IntPtr statement, int index, object value)
        {
            int result;
            if (value == null || value == DBNull.Value) result = sqlite3_bind_null(statement, index);
            else if (value is int || value is long || value is short || value is byte)
                result = sqlite3_bind_int64(statement, index, Convert.ToInt64(value, CultureInfo.InvariantCulture));
            else if (value is double || value is float || value is decimal)
                result = sqlite3_bind_double(statement, index, Convert.ToDouble(value, CultureInfo.InvariantCulture));
            else
            {
                byte[] bytes = Encoding.UTF8.GetBytes(Convert.ToString(value, CultureInfo.InvariantCulture));
                result = sqlite3_bind_text(statement, index, bytes, bytes.Length, SqliteTransient);
            }
            if (result != SqliteOk) throw new InvalidOperationException("数据库参数绑定失败：" + ErrorMessage());
        }

        private static object ReadColumn(IntPtr statement, int column)
        {
            int type = sqlite3_column_type(statement, column);
            if (type == 1) return sqlite3_column_int64(statement, column);
            if (type == 2) return sqlite3_column_double(statement, column);
            if (type == 3)
            {
                IntPtr pointer = sqlite3_column_text(statement, column);
                return PtrToUtf8(pointer, sqlite3_column_bytes(statement, column));
            }
            return null;
        }

        private string ErrorMessage()
        {
            return handle == IntPtr.Zero ? "unknown error" : PtrToUtf8(sqlite3_errmsg(handle), -1);
        }

        private static byte[] Utf8Null(string value)
        {
            byte[] source = Encoding.UTF8.GetBytes(value ?? string.Empty);
            byte[] result = new byte[source.Length + 1];
            Buffer.BlockCopy(source, 0, result, 0, source.Length);
            return result;
        }

        private static string PtrToUtf8(IntPtr pointer, int length)
        {
            if (pointer == IntPtr.Zero) return string.Empty;
            if (length < 0)
            {
                length = 0;
                while (Marshal.ReadByte(pointer, length) != 0 && length < 1024 * 1024) length++;
            }
            if (length == 0) return string.Empty;
            byte[] bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }

        public void Dispose()
        {
            if (handle != IntPtr.Zero)
            {
                sqlite3_close_v2(handle);
                handle = IntPtr.Zero;
            }
        }

        private sealed class SqliteStatement : IDisposable
        {
            public SqliteStatement(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; private set; }
            public void Dispose()
            {
                if (Handle != IntPtr.Zero)
                {
                    sqlite3_finalize(Handle);
                    Handle = IntPtr.Zero;
                }
            }
        }
    }
}

