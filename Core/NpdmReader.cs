using System;
using System.IO;
using System.Text;

namespace NspdRepack.Core
{
    public static class NpdmReader
    {
        public sealed class Info
        {
            public string ProgramId;  
            public string Name;    
        }

        public static Info Read(string path)
        {
            byte[] d = File.ReadAllBytes(path);
            if (d.Length < 0x80 || d[0] != (byte)'M' || d[1] != (byte)'E' || d[2] != (byte)'T' || d[3] != (byte)'A')
                throw new InvalidDataException("not an NPDM file (missing META magic)");

            uint aciOffset = BitConverter.ToUInt32(d, 0x70);
            if ((long)aciOffset + 0x18 > d.Length)
                throw new InvalidDataException("ACI0 offset is outside the file");

            int a = (int)aciOffset;
            if (d[a] != (byte)'A' || d[a + 1] != (byte)'C' || d[a + 2] != (byte)'I' || d[a + 3] != (byte)'0')
                throw new InvalidDataException("missing ACI0 magic");

            ulong programId = BitConverter.ToUInt64(d, a + 0x10);
            string name = Encoding.ASCII.GetString(d, 0x20, 0x10).TrimEnd('\0');

            return new Info { ProgramId = programId.ToString("x16"), Name = name };
        }
    }
}
