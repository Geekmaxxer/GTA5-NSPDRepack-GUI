using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace NspdRepack.Core
{
    public sealed class ToolResult
    {
        public int ExitCode;
        public string Output;
    }

    public static class ToolRunner
    {
        static readonly object ActiveLock = new object();
        static readonly List<ProcessJob> Active = new List<ProcessJob>();

        public static void KillAll()
        {
            lock (ActiveLock)
            {
                foreach (var job in Active) job.Terminate();
            }
        }

        public static ToolResult Run(string exe, IList<string> args, string logPath,
            Action<string> onLine, string workingDir, CancellationToken ct,
            Func<string> heartbeat = null, int heartbeatSeconds = 5)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = BuildArguments(args),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = string.IsNullOrEmpty(workingDir) ? Path.GetDirectoryName(exe) : workingDir
            };

            var sb = new StringBuilder();
            var sync = new object();
            var clock = Stopwatch.StartNew();
            var lastOutputMs = new long[] { 0 };   
            var hidden = new int[] { 0 };          
            long hbMs = Math.Max(1, heartbeatSeconds) * 1000L;
            long lastBeatMs = 0;

            using (var writer = new StreamWriter(logPath, false, new UTF8Encoding(false)))
            using (var p = new Process { StartInfo = psi })
            using (var job = new ProcessJob())
            {
                writer.AutoFlush = true;

                DataReceivedEventHandler handler = (s, e) =>
                {
                    if (e.Data == null) return;
                    Interlocked.Exchange(ref lastOutputMs[0], clock.ElapsedMilliseconds);
                    if (KeyFilter.KeyWarning.IsMatch(e.Data)) { Interlocked.Increment(ref hidden[0]); return; }
                    lock (sync)
                    {
                        sb.AppendLine(e.Data);
                        writer.WriteLine(e.Data);
                    }
                    if (onLine != null) onLine(e.Data);
                };
                p.OutputDataReceived += handler;
                p.ErrorDataReceived += handler;

                try
                {
                    p.Start();
                }
                catch (Exception ex)
                {
                    throw new RepackException("Could not start " + Path.GetFileName(exe) + ": " + ex.Message);
                }

                job.Assign(p);
                lock (ActiveLock) Active.Add(job);

                try
                {
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    while (!p.WaitForExit(150))
                    {
                        if (ct.IsCancellationRequested)
                        {
                            KillTree(p, job);
                            p.WaitForExit();
                            ct.ThrowIfCancellationRequested();
                        }

                        if (heartbeat != null)
                        {
                            long now = clock.ElapsedMilliseconds;
                            long quiet = now - Interlocked.Read(ref lastOutputMs[0]);
                            if (quiet >= hbMs && now - lastBeatMs >= hbMs)
                            {
                                lastBeatMs = now;
                                string extra = null;
                                try { extra = heartbeat(); } catch { }
                                if (onLine != null)
                                    onLine("... still running (" + clock.Elapsed.ToString(@"hh\:mm\:ss") + ")"
                                           + (string.IsNullOrEmpty(extra) ? "" : " - " + extra));
                            }
                        }
                    }
                    p.WaitForExit(); 

                    ct.ThrowIfCancellationRequested();

                    lock (sync)
                    {
                        if (hidden[0] > 0)
                            writer.WriteLine("(" + hidden[0] + " unused prod.keys warning line(s) hidden - they print key values)");
                        writer.Flush();
                    }
                    return new ToolResult { ExitCode = p.ExitCode, Output = sb.ToString() };
                }
                finally
                {
                    lock (ActiveLock) Active.Remove(job);
                }
            }
        }

        static void KillTree(Process p, ProcessJob job)
        {
            job.Terminate();                 
            try { p.Kill(); } catch { }      
        }

        public static string BuildArguments(IEnumerable<string> args)
        {
            var sb = new StringBuilder();
            foreach (var a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Quote(a));
            }
            return sb.ToString();
        }

        public static string Quote(string arg)
        {
            if (arg == null) arg = "";
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
                return arg;

            var sb = new StringBuilder();
            sb.Append('"');
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(c);
                }
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }
    }
}
