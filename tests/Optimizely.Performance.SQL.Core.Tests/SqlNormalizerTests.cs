using Optimizely.Performance.SQL.Fingerprinting;

namespace Optimizely.Performance.SQL.Tests
{
    /// <summary>
    /// The normalizer decides which textual differences are the same statement. Get it
    /// wrong in one direction and an approved rewrite silently stops matching; wrong in
    /// the other and two genuinely different statements collide on one entry.
    /// </summary>
    public class SqlNormalizerTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t\r\n")]
        public void Blank_input_normalizes_to_empty(string input)
        {
            Assert.Equal(string.Empty, SqlNormalizer.Normalize(input));
        }

        [Fact]
        public void Runs_of_whitespace_collapse_to_one_space()
        {
            Assert.Equal(
                "SELECT A FROM B",
                SqlNormalizer.Normalize("SELECT\n\t A   FROM\r\n B"));
        }

        [Fact]
        public void Leading_and_trailing_whitespace_is_dropped()
        {
            Assert.Equal("SELECT 1", SqlNormalizer.Normalize("   SELECT 1   "));
        }

        [Fact]
        public void Keywords_and_identifiers_are_upper_cased()
        {
            Assert.Equal(
                "SELECT PKID FROM TBLCONTENT",
                SqlNormalizer.Normalize("select pkID from tblContent"));
        }

        [Fact]
        public void Line_comments_are_stripped()
        {
            Assert.Equal(
                "SELECT 1 FROM T",
                SqlNormalizer.Normalize("SELECT 1 -- a trailing note\nFROM T"));
        }

        [Fact]
        public void Block_comments_are_stripped()
        {
            Assert.Equal(
                "SELECT 1 FROM T",
                SqlNormalizer.Normalize("SELECT 1 /* explanatory\n   note */ FROM T"));
        }

        [Fact]
        public void Nested_block_comments_are_stripped_whole()
        {
            Assert.Equal(
                "SELECT 1",
                SqlNormalizer.Normalize("SELECT /* outer /* inner */ still outer */ 1"));
        }

        [Fact]
        public void A_stripped_comment_still_separates_its_neighbours()
        {
            // "A/*x*/B" is two tokens, not "AB".
            Assert.Equal("A B", SqlNormalizer.Normalize("A/*x*/B"));
        }

        [Fact]
        public void String_literals_keep_their_case()
        {
            Assert.Equal(
                "SELECT 'MixedCase' FROM T",
                SqlNormalizer.Normalize("select 'MixedCase' from t"));
        }

        [Fact]
        public void Bracketed_identifiers_keep_their_case()
        {
            Assert.Equal(
                "SELECT [MixedCase] FROM T",
                SqlNormalizer.Normalize("select [MixedCase] from t"));
        }

        [Fact]
        public void Quoted_identifiers_keep_their_case()
        {
            Assert.Equal(
                "SELECT \"MixedCase\" FROM T",
                SqlNormalizer.Normalize("select \"MixedCase\" from t"));
        }

        [Fact]
        public void Doubled_quotes_inside_a_literal_are_an_escape_not_a_terminator()
        {
            Assert.Equal(
                "SELECT 'it''s Fine' FROM T",
                SqlNormalizer.Normalize("select 'it''s Fine' from t"));
        }

        [Fact]
        public void Unicode_literal_prefix_stays_attached_and_is_upper_cased()
        {
            Assert.Equal(
                "SELECT N'Ärlig' FROM T",
                SqlNormalizer.Normalize("select n'Ärlig' from t"));
        }

        [Fact]
        public void Whitespace_inside_a_literal_is_preserved()
        {
            Assert.Equal(
                "SELECT 'a   b' FROM T",
                SqlNormalizer.Normalize("select 'a   b'  from  t"));
        }

        [Fact]
        public void A_comment_marker_inside_a_literal_is_not_a_comment()
        {
            Assert.Equal(
                "SELECT '-- not a comment' FROM T",
                SqlNormalizer.Normalize("select '-- not a comment' from t"));
        }

        [Fact]
        public void An_unterminated_literal_degrades_instead_of_throwing()
        {
            // A malformed statement must come out as "no match", never as an exception on
            // the data path.
            var normalized = SqlNormalizer.Normalize("SELECT 'unterminated");

            Assert.Equal("SELECT 'unterminated", normalized);
        }

        [Fact]
        public void Formatting_only_differences_converge()
        {
            var a = SqlNormalizer.Normalize(
                "SELECT c.pkID\n  FROM tblContent c\n WHERE c.Name = @Name");

            var b = SqlNormalizer.Normalize(
                "select c.pkid from tblcontent c where c.name = @name");

            Assert.Equal(a, b);
        }

        [Fact]
        public void Semantically_different_statements_do_not_converge()
        {
            var a = SqlNormalizer.Normalize("SELECT 1 FROM tblContent WHERE pkID = @id");
            var b = SqlNormalizer.Normalize("SELECT 1 FROM tblContent WHERE pkID > @id");

            Assert.NotEqual(a, b);
        }
    }
}
