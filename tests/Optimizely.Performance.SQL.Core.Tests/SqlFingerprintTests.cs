using Optimizely.Performance.SQL.Fingerprinting;

namespace Optimizely.Performance.SQL.Tests
{
    public class SqlFingerprintTests
    {
        [Fact]
        public void Fingerprint_is_thirty_two_lower_case_hex_characters()
        {
            var fingerprint = SqlFingerprint.Compute("SELECT 1");

            Assert.Equal(SqlFingerprint.Length, fingerprint.Length);
            Assert.Matches("^[0-9a-f]{32}$", fingerprint);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Blank_input_fingerprints_to_empty(string input)
        {
            Assert.Equal(string.Empty, SqlFingerprint.Compute(input));
        }

        [Fact]
        public void The_same_statement_fingerprints_the_same_way_every_time()
        {
            Assert.Equal(
                SqlFingerprint.Compute("SELECT 1 FROM tblContent"),
                SqlFingerprint.Compute("SELECT 1 FROM tblContent"));
        }

        [Fact]
        public void Formatting_differences_produce_the_same_fingerprint()
        {
            Assert.Equal(
                SqlFingerprint.Compute("SELECT  pkID\n FROM tblContent -- note"),
                SqlFingerprint.Compute("select pkid from tblcontent"));
        }

        [Fact]
        public void Different_statements_produce_different_fingerprints()
        {
            Assert.NotEqual(
                SqlFingerprint.Compute("SELECT 1 FROM tblContent"),
                SqlFingerprint.Compute("SELECT 2 FROM tblContent"));
        }

        [Fact]
        public void Computing_from_normalized_text_matches_computing_from_raw()
        {
            const string sql = "select pkID from tblContent where Name = @Name";

            Assert.Equal(
                SqlFingerprint.Compute(sql),
                SqlFingerprint.ComputeFromNormalized(SqlNormalizer.Normalize(sql)));
        }

        [Fact]
        public void The_fingerprint_is_the_leading_half_of_the_sha256()
        {
            // Pinning the algorithm: the sync tool computes these offline and the runtime
            // must agree, so a change here is a breaking change to every published config.
            var expected = System.BitConverter
                .ToString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(SqlNormalizer.Normalize("SELECT 1"))))
                .Replace("-", string.Empty)
                .ToLowerInvariant()
                .Substring(0, SqlFingerprint.Length);

            Assert.Equal(expected, SqlFingerprint.Compute("SELECT 1"));
        }

        [Theory]
        [InlineData("0123456789abcdef0123456789abcdef", true)]
        [InlineData("0123456789ABCDEF0123456789ABCDEF", false)] // upper case is not the canonical form
        [InlineData("0123456789abcdef0123456789abcde", false)]  // too short
        [InlineData("0123456789abcdef0123456789abcdef0", false)] // too long
        [InlineData("0123456789abcdef0123456789abcdeg", false)] // not hex
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Validity_check_accepts_only_the_canonical_form(string value, bool expected)
        {
            Assert.Equal(expected, SqlFingerprint.IsValid(value));
        }
    }

    public class ModuleHashTests
    {
        [Fact]
        public void Hash_is_sixty_four_lower_case_hex_characters()
        {
            var hash = ModuleHash.Compute("CREATE PROCEDURE dbo.p AS SELECT 1");

            Assert.Equal(ModuleHash.Length, hash.Length);
            Assert.Matches("^[0-9a-f]{64}$", hash);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   \r\n  ")]
        public void Blank_input_hashes_to_empty(string input)
        {
            Assert.Equal(string.Empty, ModuleHash.Compute(input));
        }

        [Fact]
        public void Line_endings_do_not_change_the_hash()
        {
            // Scripting a procedure out and back in can rewrite line endings without
            // changing the procedure, and that must not read as drift.
            Assert.Equal(
                ModuleHash.Compute("CREATE PROC p\r\nAS\r\nSELECT 1"),
                ModuleHash.Compute("CREATE PROC p\nAS\nSELECT 1"));
        }

        [Fact]
        public void Surrounding_whitespace_does_not_change_the_hash()
        {
            Assert.Equal(
                ModuleHash.Compute("CREATE PROC p AS SELECT 1"),
                ModuleHash.Compute("\n  CREATE PROC p AS SELECT 1  \n"));
        }

        [Fact]
        public void An_interior_whitespace_change_does_change_the_hash()
        {
            // Anything inside the body is exactly the kind of edit that should force the
            // replacement to be re-derived, so this must not be normalized away.
            Assert.NotEqual(
                ModuleHash.Compute("CREATE PROC p AS SELECT 1"),
                ModuleHash.Compute("CREATE PROC p AS  SELECT 1"));
        }

        [Fact]
        public void A_body_change_changes_the_hash()
        {
            Assert.NotEqual(
                ModuleHash.Compute("CREATE PROC p AS SELECT 1"),
                ModuleHash.Compute("CREATE PROC p AS SELECT 2"));
        }
    }
}
