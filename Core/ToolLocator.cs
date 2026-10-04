using System;
using System.Collections.Generic;
using System.IO;

namespace NspdRepack.Core
{
    public static class AppPaths
    {
        public static string AppDir
        {
            get { return AppDomain.CurrentDomain.BaseDirectory; }
        }
    }

    public static class ToolLocator
    {
        public static string FindTool(string fileName)
        {
            string embedded = EmbeddedTools.Get(fileName);
            if (embedded != null) return embedded;

            string tools = Path.Combine(AppPaths.AppDir, "Tools");
            string[] direct =
            {
                Path.Combine(AppPaths.AppDir, fileName),
                Path.Combine(tools, fileName)
            };
            foreach (var d in direct)
                if (File.Exists(d)) return d;

            string found = SearchTree(tools, fileName);
            if (found != null) return found;

            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                try
                {
                    string c = Path.Combine(dir.Trim().Trim('"'), fileName);
                    if (File.Exists(c)) return c;
                }
                catch { }
            }
            return null;
        }

        public static string FindKeys()
        {
            string embedded = EmbeddedTools.Get("prod.keys");
            if (embedded != null) return embedded;

            string tools = Path.Combine(AppPaths.AppDir, "Tools");
            string[] candidates =
            {
                Path.Combine(AppPaths.AppDir, "prod.keys"),
                Path.Combine(tools, "prod.keys"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".switch", "prod.keys")
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;

            return SearchTree(tools, "prod.keys");
        }

        public static string FindRomfsFolder()
        {
            string[] candidates =
            {
                Path.Combine(AppPaths.AppDir, "romfs_base"),
                Path.Combine(AppPaths.AppDir, "Tools", "romfs_base")
            };
            foreach (var c in candidates)
                if (Directory.Exists(c)) return c;
            return null;
        }

        public static string DefaultOutputRoot()
        {
            if (IsWritable(AppPaths.AppDir)) return Path.Combine(AppPaths.AppDir, "outputs");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NspdRepack", "outputs");
        }

        static bool IsWritable(string dir)
        {
            try
            {
                string t = Path.Combine(dir, ".w" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(t, "");
                File.Delete(t);
                return true;
            }
            catch { return false; }
        }

        static string SearchTree(string root, string name)
        {
            if (!Directory.Exists(root)) return null;
            try
            {
                string direct = Path.Combine(root, name);
                if (File.Exists(direct)) return direct;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    if (string.Equals(Path.GetFileName(dir), "romfs_base", StringComparison.OrdinalIgnoreCase)) continue;
                    string r = SearchTree(dir, name);
                    if (r != null) return r;
                }
            }
            catch { }
            return null;
        }
    }
}
