using System;
using System.Text;

namespace Optimizely.Performance.SQL.Fingerprinting
{
    /// <summary>
    /// Reduces a statement to a canonical form so that texts differing only in
    /// formatting produce the same fingerprint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The transformation is deliberately conservative. It collapses whitespace, strips
    /// comments and upper-cases unquoted text; it does <em>not</em> reorder, reformat or
    /// parameterize anything. Two statements that normalize identically are the same
    /// statement, which is the property the runtime lookup depends on.
    /// </para>
    /// <para>
    /// Content inside string literals, <c>[bracketed]</c> identifiers and
    /// <c>"quoted"</c> identifiers is preserved verbatim, including case, because
    /// changing it could change results.
    /// </para>
    /// </remarks>
    public static class SqlNormalizer
    {
        /// <summary>
        /// Returns the canonical form of <paramref name="sql"/>, or an empty string when
        /// the input is null or blank.
        /// </summary>
        public static string Normalize(string sql)
        {
            if (string.IsNullOrEmpty(sql))
            {
                return string.Empty;
            }

            var output = new StringBuilder(sql.Length);
            var pendingSeparator = false;
            var index = 0;

            while (index < sql.Length)
            {
                var current = sql[index];

                // -- line comment, runs to end of line.
                if (current == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
                {
                    while (index < sql.Length && sql[index] != '\n')
                    {
                        index++;
                    }

                    pendingSeparator = true;
                    continue;
                }

                // /* block comment */, which SQL Server allows to nest.
                if (current == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
                {
                    var depth = 1;
                    index += 2;

                    while (index < sql.Length && depth > 0)
                    {
                        if (sql[index] == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
                        {
                            depth++;
                            index += 2;
                        }
                        else if (sql[index] == '*' && index + 1 < sql.Length && sql[index + 1] == '/')
                        {
                            depth--;
                            index += 2;
                        }
                        else
                        {
                            index++;
                        }
                    }

                    pendingSeparator = true;
                    continue;
                }

                if (char.IsWhiteSpace(current))
                {
                    pendingSeparator = true;
                    index++;
                    continue;
                }

                if (pendingSeparator)
                {
                    if (output.Length > 0)
                    {
                        output.Append(' ');
                    }

                    pendingSeparator = false;
                }

                // Literals and quoted identifiers are copied through untouched.
                if (current == '\'' || current == '"' || current == '[')
                {
                    index = CopyDelimited(sql, index, output);
                    continue;
                }

                // N'unicode literal' — keep the prefix attached to its literal.
                if ((current == 'N' || current == 'n') && index + 1 < sql.Length && sql[index + 1] == '\'')
                {
                    output.Append('N');
                    index = CopyDelimited(sql, index + 1, output);
                    continue;
                }

                output.Append(char.ToUpperInvariant(current));
                index++;
            }

            return output.ToString();
        }

        /// <summary>
        /// Copies a delimited run starting at <paramref name="start"/> verbatim and
        /// returns the index just past its closing delimiter. Doubled closing delimiters
        /// are treated as escapes, matching T-SQL.
        /// </summary>
        private static int CopyDelimited(string sql, int start, StringBuilder output)
        {
            var open = sql[start];
            var close = open == '[' ? ']' : open;

            output.Append(open);
            var index = start + 1;

            while (index < sql.Length)
            {
                var current = sql[index];

                if (current == close)
                {
                    // '' inside '…' and ]] inside […] are escaped delimiters, not terminators.
                    if (index + 1 < sql.Length && sql[index + 1] == close)
                    {
                        output.Append(close, 2);
                        index += 2;
                        continue;
                    }

                    output.Append(close);
                    return index + 1;
                }

                output.Append(current);
                index++;
            }

            // Unterminated literal: return what we have rather than throwing, so a
            // malformed statement degrades to "no match" instead of an exception on the
            // data path.
            return index;
        }
    }
}
