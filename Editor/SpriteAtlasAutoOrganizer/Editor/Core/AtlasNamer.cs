using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SpriteAtlasAutoOrganizer.Editor
{
    /// <summary>
    /// 用 Cluster 内 Sprite 键做稳定哈希，保证内容不变时 Atlas 文件名不变。
    /// </summary>
    internal static class AtlasNamer
    {
        private static readonly Regex InvalidFileChars =
            new Regex(@"[^A-Za-z0-9_]+", RegexOptions.Compiled);

        public static string BuildStableName(string domain, IEnumerable<SpriteKey> sprites)
        {
            string safeDomain = SanitizeDomain(domain);
            string hash = ComputeContentHash(sprites);
            return "Atlas_" + safeDomain + "_" + hash;
        }

        public static string ComputeContentHash(IEnumerable<SpriteKey> sprites)
        {
            var tokens = new List<string>();
            if (sprites != null)
            {
                foreach (SpriteKey key in sprites)
                    tokens.Add(key.Token);
            }

            tokens.Sort(StringComparer.Ordinal);
            using (var sha1 = SHA1.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(string.Join("|", tokens));
                byte[] hash = sha1.ComputeHash(bytes);
                var builder = new StringBuilder(6);
                for (int i = 0; i < 3; i++)
                    builder.Append(hash[i].ToString("X2"));
                return builder.ToString();
            }
        }

        public static string ComputeHostHash(IEnumerable<SpriteKey> sprites)
        {
            return ComputeContentHash(sprites);
        }

        public static string SanitizeDomain(string domain)
        {
            if (string.IsNullOrEmpty(domain))
                return "Default";

            string sanitized = InvalidFileChars.Replace(domain, "_");
            sanitized = sanitized.Trim('_');
            return string.IsNullOrEmpty(sanitized) ? "Default" : sanitized;
        }
    }
}
