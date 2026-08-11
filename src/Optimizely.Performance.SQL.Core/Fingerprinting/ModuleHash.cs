using System.Security.Cryptography;
using System.Text;

namespace Optimizely.Performance.SQL.Fingerprinting
{
    /// <summary>
    /// Hashes a stored procedure body so the shim can tell whether the procedure it is
    /// redirecting away from is still the one its replacement was written against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately computed in the client rather than with <c>HASHBYTES</c> in T-SQL.
    /// <c>HASHBYTES</c> rejects <c>nvarchar(max)</c> before SQL Server 2016, and a probe
    /// that throws on an older engine would cost every rewrite on that database, not just
    /// the procedure redirects. Hashing here also guarantees the sync tool and the runtime
    /// agree, because both call this method.
    /// </para>
    /// <para>
    /// Full 256 bits, unlike <see cref="SqlFingerprint"/>. This one is a
    /// has-somebody-changed-this check rather than a dictionary key, so there is no reason
    /// to truncate.
    /// </para>
    /// </remarks>
    public static class ModuleHash
    {
        /// <summary>Number of hex characters in a module hash.</summary>
        public const int Length = 64;

        /// <summary>
        /// Returns the lower-case hex SHA-256 of <paramref name="definition"/> after
        /// normalizing line endings and trimming, or an empty string for no input.
        /// </summary>
        /// <remarks>
        /// Line endings are normalized because scripting a procedure out and back in can
        /// change them without changing the procedure. Nothing else is normalized: a
        /// whitespace or comment change inside a body is exactly the kind of edit that
        /// should force the replacement to be re-derived.
        /// </remarks>
        public static string Compute(string definition)
        {
            if (string.IsNullOrEmpty(definition))
            {
                return string.Empty;
            }

            var canonical = definition.Replace("\r\n", "\n").Replace("\r", "\n").Trim();

            if (canonical.Length == 0)
            {
                return string.Empty;
            }

            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                var result = new StringBuilder(Length);

                foreach (var b in hash)
                {
                    result.Append(b.ToString("x2"));
                }

                return result.ToString();
            }
        }
    }
}
