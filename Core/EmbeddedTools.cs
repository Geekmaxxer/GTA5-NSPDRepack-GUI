using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace NspdRepack.Core
{
    public static class EmbeddedTools
    {
        const string RootName = "NspdRepack";
        const string LockName = ".lock";
        const string RomfsDirName = "romfs_base";
        const string RomfsPrefix = "romfs_base/";

        static readonly object Sync = new object();
        static readonly object RomfsSync = new object();

        static bool _initialized;
        static bool _cleaned;
        static bool _toolsReady;
        static string _root;          // %LOCALAPPDATA%\NspdRepack
        static string _session;       // <root>\<session-id>
        static FileStream _lock;
        static Payload _payload;
        static string _romfsDir;      
        static int _romfsFiles;
        static long _romfsBytes;
        static string _initError;     


        public static string SessionDir { get { Initialize(); return _session; } }

        public static string InitError { get { Initialize(); return _initError; } }

        public static bool HasRomfs { get { Initialize(); return _romfsFiles > 0; } }
        public static int RomfsFileCount { get { Initialize(); return _romfsFiles; } }
        public static long RomfsBytes { get { Initialize(); return _romfsBytes; } }

        public static string Get(string fileName)
        {
            Initialize();
            if (!_toolsReady || _session == null) return null;
            string p = Path.Combine(_session, fileName);
            return File.Exists(p) ? p : null;
        }

        public static bool IsEmbedded(string path)
        {
            Initialize();
            if (_session == null || string.IsNullOrEmpty(path)) return false;
            return path.StartsWith(_session, StringComparison.OrdinalIgnoreCase);
        }


        public static void Initialize()
        {
            lock (Sync)
            {
                if (_initialized) return;
                _initialized = true;

                try
                {
                    _payload = Payload.Load();
                    if (_payload != null)
                    {
                        foreach (var e in _payload.Entries)
                        {
                            if (!e.IsDirectory && e.Name.StartsWith(RomfsPrefix, StringComparison.Ordinal))
                            {
                                _romfsFiles++;
                                _romfsBytes += e.Size;
                            }
                        }
                    }

                    string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    if (string.IsNullOrEmpty(local)) { _initError = "LocalAppData folder is not available"; return; }
                    _root = Path.Combine(local, RootName);

                    SweepStale();
                    if (_payload == null) return;   

                    string session = Path.Combine(_root, Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(session);
                    _lock = new FileStream(Path.Combine(session, LockName), FileMode.CreateNew,
                        FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                    _session = session;

                    _payload.Extract(session, e => !e.IsDirectory && e.Name.IndexOf('/') < 0, null, CancellationToken.None);
                    _toolsReady = true;
                }
                catch (Exception ex)
                {
                    _initError = ex.Message;
                }
            }
        }

        public static string EnsureRomfs(Action<string> log, CancellationToken ct)
        {
            Initialize();
            lock (RomfsSync)
            {
                if (_romfsDir != null && Directory.Exists(_romfsDir)) return _romfsDir;
                if (_payload == null || _session == null || _romfsFiles == 0) return null;

                string dest = Path.Combine(_session, RomfsDirName);

                long need = _romfsBytes + 256L * 1024 * 1024;
                try
                {
                    long free = new DriveInfo(Path.GetPathRoot(_session)).AvailableFreeSpace;
                    if (free < need)
                        throw new RepackException("Not enough free disk space to unpack the built-in base RomFS to " + _session
                            + ": about " + Repacker.FormatSize(need) + " needed, " + Repacker.FormatSize(free) + " free.");
                }
                catch (RepackException) { throw; }
                catch { /* unreadable drive: skip the check */ }

                if (log != null)
                    log("Unpacking the built-in base RomFS (" + _romfsFiles + " files, " + Repacker.FormatSize(_romfsBytes)
                        + "). Done once per session; it is deleted again when the app closes.");

                var clock = Stopwatch.StartNew();
                long lastLogMs = -5000;
                Action<long, long> progress = (done, total) =>
                {
                    long ms = clock.ElapsedMilliseconds;
                    if (log == null || (ms - lastLogMs < 2000 && done < total)) return;
                    lastLogMs = ms;
                    long pct = total > 0 ? done * 100 / total : 100;
                    log("  unpacking base RomFS: " + Repacker.FormatSize(done) + " / " + Repacker.FormatSize(total) + " (" + pct + "%)");
                };

                try
                {
                    _payload.Extract(_session,
                        e => e.Name == RomfsDirName || e.Name.StartsWith(RomfsPrefix, StringComparison.Ordinal),
                        progress, ct);
                }
                catch
                {
                    DeleteDirectory(dest);  
                    throw;
                }

                _romfsDir = dest;
                return dest;
            }
        }

        public static void Cleanup()
        {
            lock (Sync)
            {
                if (_cleaned) return;
                _cleaned = true;

                try { ToolRunner.KillAll(); } catch { }

                try { if (_lock != null) _lock.Dispose(); } catch { }   
                _lock = null;

                try { if (_session != null) DeleteDirectory(_session); } catch { }
                _session = null;

                try
                {
                    if (_root != null)
                    {
                        SweepStale();
                        if (Directory.Exists(_root) && Directory.GetFileSystemEntries(_root).Length == 0)
                            Directory.Delete(_root);
                    }
                }
                catch { }
            }
        }

        static void SweepStale()
        {
            if (_root == null || !Directory.Exists(_root)) return;

            string[] entries;
            try { entries = Directory.GetFileSystemEntries(_root); }
            catch { return; }

            foreach (string entry in entries)
            {
                try
                {
                    if (Directory.Exists(entry))
                    {
                        if (_session != null && string.Equals(entry, _session, StringComparison.OrdinalIgnoreCase)) continue;
                        if (IsOwnedByRunningInstance(entry)) continue;
                        DeleteDirectory(entry);
                    }
                    else
                    {
                        File.SetAttributes(entry, FileAttributes.Normal);
                        File.Delete(entry);
                    }
                }
                catch { }
            }
        }

        static bool IsOwnedByRunningInstance(string dir)
        {
            if (!IsSessionName(Path.GetFileName(dir))) return false;

            string lockPath = Path.Combine(dir, LockName);
            if (!File.Exists(lockPath))
            {
                try { return DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) < TimeSpan.FromMinutes(2); }
                catch { return true; }
            }

            try
            {
                using (new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                return false;   
            }
            catch (IOException) { return true; }    
            catch { return true; }                  
        }

        static bool IsSessionName(string name)
        {
            if (name == null || name.Length != 32) return false;
            foreach (char c in name)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!hex) return false;
            }
            return true;
        }

        static void DeleteDirectory(string dir)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (!Directory.Exists(dir)) return;
                    Directory.Delete(dir, true);
                    return;
                }
                catch
                {
                    try
                    {
                        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                        {
                            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                        }
                    }
                    catch { }
                    Thread.Sleep(150 * (attempt + 1));
                }
            }
        }
    }
}
