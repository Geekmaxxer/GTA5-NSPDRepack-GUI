using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace NspdRepack.Core
{
    public sealed class RepackException : Exception
    {
        public RepackException(string message) : base(message) { }
    }

    public sealed class RepackOptions
    {
        public string NspdPath = "";
        public string OutputRoot = "";
        public string HacpackPath = "";
        public string HactoolPath = "";
        public string KeysPath = "";
        public string RomfsBasePath = "";     
        public string TitleIdOverride = "";   
        public string KeyGeneration = "1";
        public string SdkVersion = "000C1100";
        public string TitleVersion = "00000000";
        public bool Verify = true;
        public bool DeepVerify = true;        // extract ExeFS from the built NCA and compare hashes
        public bool CopyBaseRomfs = false;    // only for a --romfs override: false = point hacpack at it in place
        public bool KeepWork = false;
    }

    public sealed class RepackResult
    {
        public string TitleId;
        public string OutputDir;
        public string NspPath;
        public string ManifestPath;
        public List<string> Warnings = new List<string>();
    }

    public sealed class Repacker
    {
        public const string DefaultTitleId = "0100b00b51230000";
        const int StageCount = 8;

        readonly RepackOptions _o;
        readonly Action<string> _log;
        readonly Action<int, int, string> _stage;
        readonly List<string> _warnings = new List<string>();
        int _stageIndex;
        string _filteredKeys;

        public Repacker(RepackOptions options, Action<string> log, Action<int, int, string> stage)
        {
            _o = options;
            _log = log;
            _stage = stage;
        }

        void Log(string s) { if (_log != null) _log(s); }

        void Warn(string s)
        {
            _warnings.Add(s);
            Log("WARNING: " + s);
        }

        void Stage(string name)
        {
            _stageIndex++;
            Log("");
            Log("[" + _stageIndex + "/" + StageCount + "] " + name);
            if (_stage != null) _stage(_stageIndex, StageCount, name);
        }

        // ------------------------------------------------------------------ main pipeline

        public RepackResult Run(CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool verify = _o.Verify;
            bool haveHactool = !string.IsNullOrWhiteSpace(_o.HactoolPath) && File.Exists(_o.HactoolPath);
            bool deep = verify && haveHactool && _o.DeepVerify;
            bool deepDone = false;   // only true if the ExeFS round-trip really ran and matched

            // ---- 1. validate -------------------------------------------------------------
            Stage("Validating inputs");
            RequireFile(_o.HacpackPath, "hacpack.exe", "Put it under the Tools folder and rebuild so it gets built into the exe.");
            RequireFile(_o.KeysPath, "prod.keys", "Put it under the Tools folder and rebuild so it gets built into the exe, or click Browse.");
            if (verify && !haveHactool)
                Warn("hactool.exe was not found under Tools - hactool checks are skipped (the built-in NSP structure check still runs).");
            if (string.IsNullOrWhiteSpace(_o.OutputRoot))
                throw new RepackException("No output folder set.");

            string nspdRoot = ResolveNspdRoot(_o.NspdPath);
            string codeDir = Path.Combine(nspdRoot, "program0.ncd", "code");
            string logoSrc = Path.Combine(nspdRoot, "program0.ncd", "logo");
            string controlSrc = Path.Combine(nspdRoot, "control0.ncd", "data");

            ValidateCodeDir(codeDir);

            if (!Directory.Exists(controlSrc) || Directory.GetFileSystemEntries(controlSrc).Length == 0)
                throw new RepackException("control0.ncd\\data is missing or empty in the NSPD. The Control NCA cannot be built without it.");

            bool haveLogo = Directory.Exists(logoSrc) && Directory.GetFileSystemEntries(logoSrc).Length > 0;
            if (!haveLogo)
                Warn("program0.ncd\\logo is missing or empty - the Program NCA will be built WITHOUT a logo section.");

            NpdmReader.Info npdm = null;
            try { npdm = NpdmReader.Read(Path.Combine(codeDir, "main.npdm")); }
            catch (Exception ex) { Warn("Could not read the Program ID from main.npdm: " + ex.Message); }

            string titleId = NormalizeTitleId(_o.TitleIdOverride);
            if (titleId == null)
            {
                if (!string.IsNullOrWhiteSpace(_o.TitleIdOverride))
                    throw new RepackException("Title ID must be 16 hex digits, got: " + _o.TitleIdOverride);
                titleId = npdm != null ? npdm.ProgramId : DefaultTitleId;
                Log("Title ID " + titleId + (npdm != null ? " (read from main.npdm, name '" + npdm.Name + "')" : " (default - could not read main.npdm)"));
            }
            else
            {
                Log("Title ID " + titleId + " (manual override)");
                if (npdm != null && !string.Equals(npdm.ProgramId, titleId, StringComparison.OrdinalIgnoreCase))
                    Warn("Title ID override " + titleId + " differs from the Program ID in main.npdm (" + npdm.ProgramId + ").");
            }

            // the base RomFS is unpacked here (first run only) - after the cheap NSPD checks above, so a bad drop fails fast
            bool bundledRomfs;
            string romfsBase = ResolveBaseRomfs(ct, out bundledRomfs);
            bool copyRomfs = _o.CopyBaseRomfs && !bundledRomfs;   // the bundled copy is already private to this app

            long otherBytes = DirectorySize(codeDir) * 5            // staged copy + hacpack temp + NCA + NSP + verify extract
                            + (Directory.Exists(logoSrc) ? DirectorySize(logoSrc) * 3 : 0)
                            + DirectorySize(controlSrc) * 3;
            CheckDiskSpace(romfsBase, copyRomfs, otherBytes);

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string runName = SafeName(Path.GetFileName(nspdRoot.TrimEnd('\\', '/')) + "-" + stamp);
            string outputDir = Path.Combine(_o.OutputRoot, runName);
            // short work folder name: hacpack is a C tool and does not handle paths over MAX_PATH (260)
            string workDir = Path.Combine(_o.OutputRoot, "_work", stamp + "_" + Guid.NewGuid().ToString("N").Substring(0, 4));
            string ncaOut = Path.Combine(outputDir, "ncas");
            string nspOut = Path.Combine(outputDir, "nsp");
            string logOut = Path.Combine(outputDir, "logs");
            foreach (var d in new[] { ncaOut, nspOut, logOut, workDir }) Directory.CreateDirectory(d);

            Log("NSPD   " + nspdRoot);
            Log("Output " + outputDir);

            // ---- 2. stage ----------------------------------------------------------------
            Stage("Staging ExeFS / logo / control data");
            string exefsDir = Path.Combine(workDir, "program", "exefs");
            string logoDir = Path.Combine(workDir, "program", "section2_pfs0");
            string controlDir = Path.Combine(workDir, "control", "romfs");
            Directory.CreateDirectory(exefsDir);

            int exefsCount = 0;
            foreach (var f in Directory.GetFiles(codeDir))
            {
                ct.ThrowIfCancellationRequested();
                string fileName = Path.GetFileName(f);
                if (!Regex.IsMatch(fileName, @"^(main|main\.npdm|rtld|sdk|subsdk\d+)$"))
                    Warn("Unexpected file in program0.ncd\\code: '" + fileName + "' - it will be packed into the ExeFS too.");
                File.Copy(f, Path.Combine(exefsDir, fileName), true);
                exefsCount++;
            }
            Log("ExeFS: " + exefsCount + " file(s)");
            if (haveLogo) CopyDirectory(logoSrc, logoDir, ct);
            CopyDirectory(controlSrc, controlDir, ct);

            string romfsDir = romfsBase;
            if (copyRomfs)
            {
                romfsDir = Path.Combine(workDir, "program", "romfs");
                Log("Copying base RomFS (this can take a while)...");
                CopyDirectory(romfsBase, romfsDir, ct);
            }
            else
            {
                Log("Using base RomFS in place: " + romfsDir);
            }

            string keys = PrepareKeys(workDir);
            var nca0 = ListNcas(ncaOut);

            // ---- 3. program NCA ----------------------------------------------------------
            Stage("Building Program NCA");
            var prog = new List<string>
            {
                "-k", keys, "-o", ncaOut,
                "--tempdir", Path.Combine(workDir, "temp_program"),
                "--backupdir", Path.Combine(workDir, "backup_program"),
                "--type", "nca", "--ncatype", "program",
                "--titleid", titleId,
                "--keygeneration", _o.KeyGeneration,
                "--sdkversion", _o.SdkVersion,
                "--exefsdir", exefsDir,
                "--romfsdir", romfsDir
            };
            if (haveLogo) { prog.Add("--logodir"); prog.Add(logoDir); }
            RunHacpack("01_program_nca", prog, workDir, logOut, ct);
            string programNca = NewNca(nca0, ncaOut, "Program NCA");
            Log("Program NCA: " + Path.GetFileName(programNca));

            // ---- 4. control NCA ----------------------------------------------------------
            Stage("Building Control NCA");
            var nca1 = ListNcas(ncaOut);
            RunHacpack("02_control_nca", new List<string>
            {
                "-k", keys, "-o", ncaOut,
                "--tempdir", Path.Combine(workDir, "temp_control"),
                "--backupdir", Path.Combine(workDir, "backup_control"),
                "--type", "nca", "--ncatype", "control",
                "--titleid", titleId,
                "--keygeneration", _o.KeyGeneration,
                "--sdkversion", _o.SdkVersion,
                "--romfsdir", controlDir
            }, workDir, logOut, ct);
            string controlNca = NewNca(nca1, ncaOut, "Control NCA");
            Log("Control NCA: " + Path.GetFileName(controlNca));

            // ---- 5. meta NCA -------------------------------------------------------------
            Stage("Building Meta NCA");
            var nca2 = ListNcas(ncaOut);
            RunHacpack("03_meta_nca", new List<string>
            {
                "-k", keys, "-o", ncaOut,
                "--tempdir", Path.Combine(workDir, "temp_meta"),
                "--backupdir", Path.Combine(workDir, "backup_meta"),
                "--type", "nca", "--ncatype", "meta", "--titletype", "application",
                "--titleid", titleId,
                "--titleversion", _o.TitleVersion,
                "--keygeneration", _o.KeyGeneration,
                "--sdkversion", _o.SdkVersion,
                "--programnca", programNca,
                "--controlnca", controlNca
            }, workDir, logOut, ct);
            string metaNca = NewNca(nca2, ncaOut, "Meta NCA");
            Log("Meta NCA: " + Path.GetFileName(metaNca));

            // ---- 6. NSP ------------------------------------------------------------------
            Stage("Packing NSP");
            RunHacpack("04_nsp", new List<string>
            {
                "-k", keys, "-o", nspOut,
                "--tempdir", Path.Combine(workDir, "temp_nsp"),
                "--backupdir", Path.Combine(workDir, "backup_nsp"),
                "--type", "nsp",
                "--titleid", titleId,
                "--ncadir", ncaOut
            }, workDir, logOut, ct);

            string nspPath = Path.Combine(nspOut, titleId + ".nsp");
            if (!File.Exists(nspPath))
            {
                string[] nsps = Directory.GetFiles(nspOut, "*.nsp");
                if (nsps.Length == 0) throw new RepackException("hacpack finished but no NSP was created in " + nspOut);
                nspPath = nsps[0];
            }
            Log("NSP: " + nspPath + " (" + FormatSize(new FileInfo(nspPath).Length) + ")");

            // ---- 7. verify ---------------------------------------------------------------
            Stage(verify ? "Verifying output" : "Verification skipped");
            if (verify)
            {
                VerifyNspStructure(nspPath, new[] { programNca, controlNca, metaNca });

                if (haveHactool)
                {
                    Tool("05_verify_nsp_pfs0", _o.HactoolPath, new List<string>
                    {
                        "--disablekeywarns", "-k", keys, "-t", "pfs0", "-i", nspPath
                    }, workDir, logOut, ct, true);

                    string progInfo = Tool("06_verify_program", _o.HactoolPath, new List<string>
                    {
                        "--disablekeywarns", "-k", keys, "-t", "nca", "-i", programNca
                    }, workDir, logOut, ct, false).Output;

                    string tid = @"Title ID:\s+" + titleId;
                    Require(progInfo, @"Content Type:\s+Program", "Program NCA has no 'Program' content type");
                    Require(progInfo, tid, "Program NCA has a different Title ID than " + titleId);
                    Require(progInfo, @"Partition Type:\s+ExeFS", "Program NCA has no ExeFS section");
                    Require(progInfo, @"Partition Type:\s+RomFS", "Program NCA has no RomFS section");
                    if (haveLogo) Require(progInfo, @"Partition Type:\s+PFS0", "Program NCA has no Logo (PFS0) section");

                    string ctrlInfo = Tool("06_verify_control", _o.HactoolPath, new List<string>
                    {
                        "--disablekeywarns", "-k", keys, "-t", "nca", "-i", controlNca
                    }, workDir, logOut, ct, true).Output;
                    Require(ctrlInfo, @"Content Type:\s+Control", "Control NCA has no 'Control' content type");
                    Require(ctrlInfo, @"Partition Type:\s+RomFS", "Control NCA has no RomFS section");
                    Require(ctrlInfo, tid, "Control NCA has a different Title ID than " + titleId);

                    string metaInfo = Tool("06_verify_meta", _o.HactoolPath, new List<string>
                    {
                        "--disablekeywarns", "-k", keys, "-t", "nca", "-i", metaNca
                    }, workDir, logOut, ct, true).Output;
                    Require(metaInfo, @"Content Type:\s+Meta", "Meta NCA has no 'Meta' content type");
                    Require(metaInfo, @"Partition Type:\s+PFS0", "Meta NCA has no PFS0 section");
                    Require(metaInfo, tid, "Meta NCA has a different Title ID than " + titleId);
                }
                Log("Structure checks passed.");

                if (deep) deepDone = DeepVerifyExefs(programNca, exefsDir, workDir, logOut, keys, ct);
            }

            // ---- 8. manifest + cleanup ---------------------------------------------------
            Stage("Writing manifest");
            string manifestPath = Path.Combine(outputDir, "manifest.json");
            var m = new List<KeyValuePair<string, object>>();
            Action<string, object> add = (k, v) => m.Add(new KeyValuePair<string, object>(k, v));
            add("tool", "NspdRepack " + typeof(Repacker).Assembly.GetName().Version.ToString(3));
            add("createdAt", DateTime.Now.ToString("s", CultureInfo.InvariantCulture));
            add("durationSeconds", (long)sw.Elapsed.TotalSeconds);
            add("titleId", titleId);
            add("nspdRoot", nspdRoot);
            add("baseRomfs", bundledRomfs ? "built-in" : romfsBase);
            add("romfsCopied", copyRomfs);
            add("keyGeneration", _o.KeyGeneration);
            add("sdkVersion", _o.SdkVersion);
            add("titleVersion", _o.TitleVersion);
            add("logoIncluded", haveLogo);
            add("verified", verify);
            add("hactoolChecks", verify && haveHactool);
            add("deepVerified", deepDone);
            add("nspPath", nspPath);
            add("nspBytes", new FileInfo(nspPath).Length);
            add("programNca", programNca);
            add("controlNca", controlNca);
            add("metaNca", metaNca);
            add("warnings", _warnings.ToList());
            File.WriteAllText(manifestPath, Json.Write(m), new UTF8Encoding(false));

            // a filtered keys copy that had to live in the work folder never outlives the run
            if (_filteredKeys != null && _filteredKeys.StartsWith(workDir, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(_filteredKeys); } catch { }
            }

            if (!_o.KeepWork)
            {
                Log("Cleaning up work folder...");
                TryDeleteDirectory(workDir);
                try
                {
                    string workRoot = Path.Combine(_o.OutputRoot, "_work");
                    if (Directory.Exists(workRoot) && Directory.GetFileSystemEntries(workRoot).Length == 0)
                        Directory.Delete(workRoot);
                }
                catch { }
            }
            else
            {
                Log("Work folder kept: " + workDir);
            }

            Log("");
            Log("Done in " + sw.Elapsed.ToString(@"mm\:ss") + ".");

            return new RepackResult
            {
                TitleId = titleId,
                OutputDir = outputDir,
                NspPath = nspPath,
                ManifestPath = manifestPath,
                Warnings = _warnings
            };
        }

        /// <summary>
        /// Gives hacpack/hactool a copy of prod.keys without the entries they never use (several are
        /// console-unique and hacpack echoes their values). Lives in the session folder, so it is deleted at exit.
        /// </summary>
        string PrepareKeys(string workDir)
        {
            string full = Path.GetFullPath(_o.KeysPath);
            try
            {
                string baseDir = EmbeddedTools.SessionDir ?? workDir;
                string dst = Path.Combine(baseDir, "filtered_keys", "prod.keys");
                KeyFilter.WriteSanitized(full, dst);
                _filteredKeys = dst;
                return dst;
            }
            catch (Exception ex)
            {
                Warn("Could not make a filtered copy of prod.keys (" + ex.Message + ") - using it as is.");
                return full;
            }
        }

        // ------------------------------------------------------------------ verification

        void VerifyNspStructure(string nspPath, string[] expectedNcas)
        {
            List<Pfs0Entry> entries;
            try { entries = Pfs0Reader.Read(nspPath); }
            catch (Exception ex) { throw new RepackException("NSP structure check failed: " + ex.Message); }

            var names = new HashSet<string>(entries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
            int ncaCount = entries.Count(e => e.Name.EndsWith(".nca", StringComparison.OrdinalIgnoreCase));
            Log("NSP contains " + entries.Count + " file(s): " + string.Join(", ", entries.Select(e => e.Name)));

            if (!entries.Any(e => e.Name.EndsWith(".cnmt.nca", StringComparison.OrdinalIgnoreCase)))
                throw new RepackException("NSP has no .cnmt.nca (meta) entry.");
            if (ncaCount < 3)
                throw new RepackException("NSP contains " + ncaCount + " NCA(s), expected 3 (program, control, meta).");
            if (ncaCount > 3)
                Warn("NSP contains " + ncaCount + " NCAs, expected 3.");
            foreach (var nca in expectedNcas)
            {
                if (!names.Contains(Path.GetFileName(nca)))
                    Warn("NSP does not contain " + Path.GetFileName(nca) + " by name.");
            }
        }

        /// <summary>
        /// Extracts the ExeFS back out of the finished Program NCA and compares SHA-256 with the files
        /// that went in. Proves the code that ends up in the NSP is the code from the NSPD.
        /// </summary>
        bool DeepVerifyExefs(string programNca, string stagedExefs, string workDir, string logOut, string keys, CancellationToken ct)
        {
            string extractDir = Path.Combine(workDir, "verify_exefs");
            Directory.CreateDirectory(extractDir);

            ToolResult r = Tool("07_deep_verify_exefs", _o.HactoolPath, new List<string>
            {
                "--disablekeywarns", "-k", keys, "-t", "nca",
                "--exefsdir=" + extractDir,
                "-i", programNca
            }, workDir, logOut, ct, false);

            if (r.ExitCode != 0)
            {
                Warn("Deep verify skipped: hactool could not extract the ExeFS (exit " + r.ExitCode + "). See logs\\07_deep_verify_exefs.log.");
                return false;
            }

            int ok = 0;
            foreach (var src in Directory.GetFiles(stagedExefs))
            {
                ct.ThrowIfCancellationRequested();
                string name = Path.GetFileName(src);
                string back = Path.Combine(extractDir, name);
                if (!File.Exists(back))
                    throw new RepackException("Deep verify failed: '" + name + "' is missing from the ExeFS inside the built Program NCA.");
                if (Sha256(src) != Sha256(back))
                    throw new RepackException("Deep verify failed: '" + name + "' inside the built Program NCA differs from the NSPD source file.");
                ok++;
            }
            Log("Deep verify OK: " + ok + " ExeFS file(s) match the NSPD byte-for-byte.");
            return true;
        }

        static void Require(string text, string pattern, string failure)
        {
            if (!Regex.IsMatch(text ?? "", pattern, RegexOptions.IgnoreCase))
                throw new RepackException("Verification failed: " + failure + ".");
        }

        // ------------------------------------------------------------------ tool helpers

        ToolResult Tool(string logName, string exe, List<string> args, string cwd, string logDir,
            CancellationToken ct, bool failOnNonZero, Func<string> heartbeat = null)
        {
            ct.ThrowIfCancellationRequested();
            Log("RUN " + logName);
            string logPath = Path.Combine(logDir, logName + ".log");
            ToolResult r = ToolRunner.Run(exe, args, logPath, line => Log("  " + line), cwd, ct, heartbeat);
            if (failOnNonZero && r.ExitCode != 0)
                throw new RepackException(logName + " failed with exit code " + r.ExitCode + ". See " + logPath);
            return r;
        }

        void RunHacpack(string logName, List<string> args, string cwd, string logDir, CancellationToken ct)
        {
            // hacpack's stdout is block-buffered, so it can stay silent for minutes on a big RomFS:
            // report how much data it has written so far
            string temp = ArgValue(args, "--tempdir"), outDir = ArgValue(args, "-o");
            Func<string> beat = () => "temp " + FormatSize(DirectorySize(temp)) + ", output " + FormatSize(DirectorySize(outDir));
            Tool(logName, _o.HacpackPath, args, cwd, logDir, ct, true, beat);
        }

        static string ArgValue(List<string> args, string name)
        {
            int i = args.IndexOf(name);
            return i >= 0 && i + 1 < args.Count ? args[i + 1] : "";
        }

        static HashSet<string> ListNcas(string dir)
        {
            return new HashSet<string>(Directory.GetFiles(dir, "*.nca"), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The NCA hacpack just produced = whatever appeared in the folder. No size heuristics.</summary>
        static string NewNca(HashSet<string> before, string dir, string what)
        {
            var created = Directory.GetFiles(dir, "*.nca").Where(f => !before.Contains(f)).ToList();
            if (created.Count != 1)
                throw new RepackException(what + ": expected hacpack to create exactly 1 new NCA in " + dir + ", found " + created.Count + ".");
            return created[0];
        }

        // ------------------------------------------------------------------ input helpers

        static void RequireFile(string path, string label, string hint)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new RepackException(label + " not found" + (string.IsNullOrWhiteSpace(path) ? "." : ": " + path) + " " + hint);
        }

        static string ResolveNspdRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new RepackException("No NSPD folder selected.");
            if (!Directory.Exists(path)) throw new RepackException("NSPD folder not found: " + path);

            if (Directory.Exists(Path.Combine(path, "program0.ncd", "code")))
                return path;

            var valid = Directory.GetDirectories(path, "*.nspd")
                .Where(d => Directory.Exists(Path.Combine(d, "program0.ncd", "code")))
                .ToList();
            if (valid.Count == 1) return valid[0];
            if (valid.Count == 0)
                throw new RepackException("No NSPD found: expected program0.ncd\\code inside the folder or inside exactly one *.nspd subfolder of it.");
            throw new RepackException("Found " + valid.Count + " .nspd folders under " + path + " - select the one you want directly.");
        }

        void ValidateCodeDir(string codeDir)
        {
            foreach (var required in new[] { "main", "main.npdm" })
            {
                if (!File.Exists(Path.Combine(codeDir, required)))
                    throw new RepackException("program0.ncd\\code\\" + required + " is missing - the NSPD extraction looks incomplete.");
            }
            foreach (var expected in new[] { "rtld", "sdk", "subsdk0" })
            {
                if (!File.Exists(Path.Combine(codeDir, expected)))
                    Warn("program0.ncd\\code\\" + expected + " is missing (the original game NSP had it).");
            }
        }

        static string NormalizeTitleId(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            return Regex.IsMatch(s, "^[0-9a-fA-F]{16}$") ? s.ToLowerInvariant() : null;
        }

        /// <summary>
        /// The base RomFS to build from: a --romfs override if given, otherwise the one bundled in the exe
        /// (unpacked once per session), otherwise a romfs_base folder next to a dev build.
        /// </summary>
        string ResolveBaseRomfs(CancellationToken ct, out bool bundled)
        {
            bundled = false;

            if (!string.IsNullOrWhiteSpace(_o.RomfsBasePath))
            {
                if (!Directory.Exists(_o.RomfsBasePath))
                    throw new RepackException("Base RomFS folder not found: " + _o.RomfsBasePath);
                Log("Base RomFS (override): " + _o.RomfsBasePath);
                return _o.RomfsBasePath;
            }

            if (EmbeddedTools.HasRomfs)
            {
                string dir = EmbeddedTools.EnsureRomfs(Log, ct);
                if (dir == null || !Directory.Exists(dir))
                    throw new RepackException("The built-in base RomFS could not be unpacked.");
                bundled = true;
                Log("Base RomFS: built-in (unpacked to " + dir + ")");
                return dir;
            }

            string dev = ToolLocator.FindRomfsFolder();
            if (dev != null)
            {
                Log("Base RomFS (found next to the exe): " + dev);
                return dev;
            }

            throw new RepackException("No base RomFS is built into this exe. Put it in Tools\\romfs_base next to NspdRepack.csproj and rebuild.");
        }

        void CheckDiskSpace(string romfsBase, bool copied, long otherBytes)
        {
            long romfsBytes = DirectorySize(romfsBase);
            // peak = hacpack temp (~1x) + program NCA (~1x) + NSP (~1x) [+ a copied RomFS (~1x)]
            double factor = copied ? 4.2 : 3.2;
            long need = (long)(romfsBytes * factor) + otherBytes + 256L * 1024 * 1024;
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(_o.OutputRoot));
                long free = new DriveInfo(root).AvailableFreeSpace;
                Log("Base RomFS " + FormatSize(romfsBytes) + " (+ " + FormatSize(otherBytes) + " for ExeFS/logo/control), free space on output drive " + FormatSize(free) + ", estimated need " + FormatSize(need));
                if (free < need)
                    throw new RepackException("Not enough free disk space on " + root + ": about " + FormatSize(need) + " needed, " + FormatSize(free) + " free.");
            }
            catch (RepackException) { throw; }
            catch { /* UNC path or unreadable drive: skip the check */ }
        }

        // ------------------------------------------------------------------ file helpers

        static long DirectorySize(string dir)
        {
            long total = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
            }
            catch { }
            return total;
        }

        static void CopyDirectory(string src, string dst, CancellationToken ct)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
            {
                ct.ThrowIfCancellationRequested();
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            }
            foreach (var d in Directory.GetDirectories(src))
                CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)), ct);
        }

        static void TryDeleteDirectory(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(dir, true);
            }
            catch { }
        }

        static string Sha256(string path)
        {
            using (var s = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }

        static string SafeName(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name)
                sb.Append("\\/:*?\"<>| ".IndexOf(c) >= 0 ? '_' : c);
            return sb.ToString();
        }

        public static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return v.ToString(u == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[u];
        }
    }

    /// <summary>Just enough JSON writing for the manifest (no extra assemblies needed).</summary>
    static class Json
    {
        public static string Write(List<KeyValuePair<string, object>> fields)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            for (int i = 0; i < fields.Count; i++)
            {
                sb.Append("  ").Append(Str(fields[i].Key)).Append(": ").Append(Value(fields[i].Value));
                sb.AppendLine(i < fields.Count - 1 ? "," : "");
            }
            sb.AppendLine("}");
            return sb.ToString();
        }

        static string Value(object v)
        {
            if (v == null) return "null";
            if (v is bool) return (bool)v ? "true" : "false";
            if (v is long) return ((long)v).ToString(CultureInfo.InvariantCulture);
            if (v is int) return ((int)v).ToString(CultureInfo.InvariantCulture);
            var list = v as List<string>;
            if (list != null) return "[" + string.Join(", ", list.Select(x => Str(x))) + "]";
            return Str(v.ToString());
        }

        static string Str(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
