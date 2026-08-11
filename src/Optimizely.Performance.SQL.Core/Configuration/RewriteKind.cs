namespace Optimizely.Performance.SQL.Configuration
{
    /// <summary>
    /// What an approved entry substitutes.
    /// </summary>
    public enum RewriteKind
    {
        /// <summary>
        /// Replaces the text of a <c>CommandType.Text</c> command with an approved
        /// statement, optionally selected by parameter nullity.
        /// </summary>
        StatementRewrite = 0,

        /// <summary>
        /// Replaces the procedure name of a <c>CommandType.StoredProcedure</c> command,
        /// pointing it at a separately deployed, versioned copy. The shipped procedure is
        /// never altered.
        /// </summary>
        ProcedureRedirect = 1
    }
}
