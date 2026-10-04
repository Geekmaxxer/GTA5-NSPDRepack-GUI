using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace NspdRepack.Core
{
    public static class KeyFilter
    {
        static readonly Regex Unused = new Regex(
            @"^\s*(bis_kek_source|bis_key_0[0-3]|bis_key_source_0[0-2]|device_key_4x" +
            @"|eticket_rsa_(kek|kek_personalized|kek_source|kekek_source|keypair)" +
            @"|mariko_master_kek_source_(0[5-9a-f]|1[0-5])|per_console_key_source" +
            @"|retail_specific_aes_key_source|save_mac_(key|sd_card_kek_source|sd_card_key_source)" +
            @"|sd_card_custom_storage_key_source|sd_seed" +
            @"|ssl_rsa_(kek|kek_personalized|kek_source|kekek_source|key))\s*=",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static readonly Regex KeyWarning = new Regex(
            @"^\s*\[WARN\]:?\s+Failed to match key", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string WriteSanitized(string src, string dst)
        {
            string dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var r = new StreamReader(src))
            using (var w = new StreamWriter(dst, false, new UTF8Encoding(false)))
            {
                string line;
                while ((line = r.ReadLine()) != null)
                    if (!Unused.IsMatch(line)) w.WriteLine(line);
            }
            return dst;
        }
    }
}
