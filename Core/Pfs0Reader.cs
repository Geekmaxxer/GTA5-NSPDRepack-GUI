using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NspdRepack.Core
{
    public sealed class Pfs0Entry
    {
        public string Name;
        public long Offset;   
        public long Size;
    }

    public static class Pfs0Reader
    {
        public static List<Pfs0Entry> Read(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var br = new BinaryReader(fs))
            {
                if (fs.Length < 16) throw new InvalidDataException("file too small to be a PFS0");

                byte[] magic = br.ReadBytes(4);
                if (magic[0] != (byte)'P' || magic[1] != (byte)'F' || magic[2] != (byte)'S' || magic[3] != (byte)'0')
                    throw new InvalidDataException("missing PFS0 magic");

                uint count = br.ReadUInt32();
                uint stringTableSize = br.ReadUInt32();
                br.ReadUInt32(); // reserved

                if (count == 0 || count > 4096) throw new InvalidDataException("implausible file count: " + count);
                if (stringTableSize > 1024 * 1024) throw new InvalidDataException("implausible string table size");

                var raw = new List<long[]>();
                for (uint i = 0; i < count; i++)
                {
                    long offset = (long)br.ReadUInt64();
                    long size = (long)br.ReadUInt64();
                    uint nameOffset = br.ReadUInt32();
                    br.ReadUInt32(); // reserved
                    raw.Add(new[] { offset, size, nameOffset });
                }

                byte[] strings = br.ReadBytes((int)stringTableSize);
                long dataStart = 16L + count * 24L + stringTableSize;

                var result = new List<Pfs0Entry>();
                foreach (var r in raw)
                {
                    int nameOffset = (int)r[2];
                    if (nameOffset < 0 || nameOffset >= strings.Length) throw new InvalidDataException("bad name offset");
                    int end = nameOffset;
                    while (end < strings.Length && strings[end] != 0) end++;
                    string name = Encoding.UTF8.GetString(strings, nameOffset, end - nameOffset);

                    if (r[0] < 0 || r[1] < 0 || dataStart + r[0] + r[1] > fs.Length)
                        throw new InvalidDataException("entry '" + name + "' extends past the end of the file (truncated NSP?)");

                    result.Add(new Pfs0Entry { Name = name, Offset = r[0], Size = r[1] });
                }
                return result;
            }
        }
    }
}
