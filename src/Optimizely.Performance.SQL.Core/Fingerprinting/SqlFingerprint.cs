using System;
using System.Security.Cryptography;
using System.Text;

namespace Optimizely.Performance.SQL.Fingerprinting
{
    /// <summary>
    /// Computes the stable lookup key for a statement.
    /// </summary>
    /// <remarks>
    /// The key is the leading 128 bits of the SHA-256 of the normalized text, rendered
    /// as lower-case hex. Truncation is safe here: the key space is closed and small
    /// (tens of reviewed statements), so this is a dictionary key, not a security
    /// boundary, and 128 bits leaves no realistic collision risk.
    /// </remarks>
    public static class SqlFingerprint
    {
        /// <summary>Number of hex characters in a fingerprint.</summary>
        public const int Length = 32;

        /// <summary>
        /// Normalizes <paramref name="sql"/> and returns its fingerprint, or an empty
        /// string when the input is null or blank.
        /// </summary>
        public static string Compute(string sql)
        {
            var normalized = SqlNormalizer.Normalize(sql);
            return normalized.Length == 0 ? string.Empty : ComputeFromNormalized(normalized);
        }

        /// <summary>
        /// Fingerprints text that is already normalized. The hot path uses this to avoid
        /// normalizing twice.
        /// </summary>
        public static string ComputeFromNormalized(string normalizedSql)
        {
            if (string.IsNullOrEmpty(normalizedSql))
            {
                return string.Empty;
            }

            var bytes = Encoding.UTF8.GetBytes(normalizedSql);

            // SHA256.Create() rather than a cached instance: the managed implementations
            // are not thread-safe and this runs on every command.
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var result = new StringBuilder(Length);

                for (var i = 0; i < Length / 2; i++)
                {
                    result.Append(hash[i].ToString("x2"));
                }

                return result.ToString();
            }
        }

        /// <summary>
        /// True when <paramref name="value"/> is well-formed: 32 lower-case hex characters.
        /// </summary>
        public static bool IsValid(string value)
        {
            if (value == null || value.Length != Length)
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');

                if (!isHex)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
