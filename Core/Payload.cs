using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace NspdRepack.Core
{
    public sealed class PayloadEntry
    {
        public string Name;    
        public long Offset;     
        public long Size;      
        public bool IsDirectory { get { return Size < 0; } }
    }

    public sealed class Payload
    {
        const int FooterSize = 32;
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("NSPDPAY1");

        public readonly string ExePath;
        public readonly List<PayloadEntry> Entries;

        Payload(string exePath, List<PayloadEntry> entries)
        {
            ExePath = exePath;
            Entries = entries;
        }

        public static Payload Load()
        {
            try
            {
                string exe = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return null;
                using (var fs = OpenExe(exe))
                {
                    List<PayloadEntry> list = ReadIndex(fs);
                    return list == null ? null : new Payload(exe, list);
                }
            }
            catch
            {
                return null;
            }
        }

        static FileStream OpenExe(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
        }

        static List<PayloadEntry> ReadIndex(FileStream fs)
        {
            if (fs.Length < FooterSize) return null;

            fs.Seek(-FooterSize, SeekOrigin.End);
            using (var br = new BinaryReader(fs, Encoding.UTF8, true))
            {
                byte[] magic = br.ReadBytes(8);
                if (magic.Length != 8) return null;
                for (int i = 0; i < 8; i++)
                    if (magic[i] != Magic[i]) return null;

                long payloadStart = br.ReadInt64();
                long indexOffset = br.ReadInt64();
                int count = br.ReadInt32();

                if (payloadStart <= 0 || indexOffset < payloadStart || indexOffset > fs.Length - FooterSize) return null;
                if (count < 0 || count > 5000000) return null;

                fs.Seek(indexOffset, SeekOrigin.Begin);
                var list = new List<PayloadEntry>(count);
                for (int i = 0; i < count; i++)
                {
                    var e = new PayloadEntry();
                    e.Name = br.ReadString();
                    e.Offset = br.ReadInt64();
                    e.Size = br.ReadInt64();
                    list.Add(e);
                }
                return list;
            }
        }


        public void Extract(string destRoot, Func<PayloadEntry, bool> include,
            Action<long, long> progress, CancellationToken ct)
        {
            var chosen = new List<PayloadEntry>();
            long total = 0;
            foreach (var e in Entries)
            {
                if (!include(e)) continue;
                chosen.Add(e);
                if (!e.IsDirectory) total += e.Size;
            }

            string root = Path.GetFullPath(destRoot);
            Directory.CreateDirectory(root);

            long done = 0;
            byte[] buf = new byte[1 << 20];
            using (var src = OpenExe(ExePath))
            {
                foreach (var e in chosen)
                {
                    ct.ThrowIfCancellationRequested();
                    string target = SafeTarget(root, e.Name);

                    if (e.IsDirectory)
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    src.Seek(e.Offset, SeekOrigin.Begin);
                    using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                    {
                        long left = e.Size;
                        while (left > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            int n = src.Read(buf, 0, (int)Math.Min(buf.Length, left));
                            if (n <= 0) throw new InvalidDataException("The data bundled inside the exe is truncated.");
                            dst.Write(buf, 0, n);
                            left -= n;
                            done += n;
                            if (progress != null) progress(done, total);
                        }
                    }
                }
            }
        }

        static string SafeTarget(string root, string name)
        {
            string rel = name.Replace('/', Path.DirectorySeparatorChar);
            string full = Path.GetFullPath(Path.Combine(root, rel));
            string prefix = root.EndsWith(Path.DirectorySeparatorChar.ToString()) ? root : root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsafe path inside the bundled data: " + name);
            return full;
        }
    }
}
