using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using NspdRepack.Core;
using NspdRepack.UI;

namespace NspdRepack
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int processId);

        [STAThread]
        static int Main(string[] args)
        {
            AppDomain.CurrentDomain.ProcessExit += (sender, e) => Shutdown();

            try
            {
                EmbeddedTools.Initialize();

                var rest = new List<string>(args);
                int cliIndex = rest.FindIndex(a => string.Equals(a, "--cli", StringComparison.OrdinalIgnoreCase));
                if (cliIndex >= 0)
                {
                    rest.RemoveAt(cliIndex);
                    return RunCli(rest);
                }

                string initial = rest.Count > 0 && Directory.Exists(rest[0]) ? rest[0] : null;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(initial));
                return 0;
            }
            finally
            {
                Shutdown();
            }
        }

        static void Shutdown()
        {
            try { ToolRunner.KillAll(); } catch { }
            EmbeddedTools.Cleanup();   // also kills tools first; never throws
        }

        static int RunCli(List<string> a)
        {
            AttachConsole(-1); 

            var o = new RepackOptions
            {
                HacpackPath = ToolLocator.FindTool("hacpack.exe") ?? "",
                HactoolPath = ToolLocator.FindTool("hactool.exe") ?? "",
                KeysPath = ToolLocator.FindKeys() ?? "",
                OutputRoot = ToolLocator.DefaultOutputRoot()
            };

            try
            {
                for (int i = 0; i < a.Count; i++)
                {
                    string arg = a[i];
                    switch (arg.ToLowerInvariant())
                    {
                        case "--keys": o.KeysPath = Next(a, ref i); break;
                        case "--romfs": o.RomfsBasePath = Next(a, ref i); break;
                        case "--out": o.OutputRoot = Next(a, ref i); break;
                        case "--titleid": o.TitleIdOverride = Next(a, ref i); break;
                        case "--keygen": o.KeyGeneration = Next(a, ref i); break;
                        case "--sdk": o.SdkVersion = Next(a, ref i); break;
                        case "--no-verify": o.Verify = false; break;
                        case "--no-deep": o.DeepVerify = false; break;
                        case "--copy-romfs": o.CopyBaseRomfs = true; break;
                        case "--keep-work": o.KeepWork = true; break;
                        default:
                            if (arg.StartsWith("--")) throw new RepackException("Unknown option: " + arg);
                            o.NspdPath = arg;
                            break;
                    }
                }
            }
            catch (RepackException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }

            using (var cts = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (sender, e) => { e.Cancel = true; cts.Cancel(); };
                try
                {
                    var result = new Repacker(o, Console.WriteLine, null).Run(cts.Token);
                    Console.WriteLine("NSP=" + result.NspPath);
                    return 0;
                }
                catch (OperationCanceledException)
                {
                    Console.Error.WriteLine("Cancelled.");
                    return 130;
                }
                catch (RepackException ex)
                {
                    Console.Error.WriteLine("ERROR: " + ex.Message);
                    return 1;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("UNEXPECTED ERROR: " + ex);
                    return 1;
                }
            }
        }

        static string Next(List<string> a, ref int i)
        {
            if (i + 1 >= a.Count) throw new RepackException("Missing value after " + a[i]);
            i++;
            return a[i];
        }
    }
}
