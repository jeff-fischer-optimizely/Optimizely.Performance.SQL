namespace Optimizely.Performance.SQL.Configuration
{
    /// <summary>
    /// How much could go wrong if this rewrite is applied to a database nobody tested it
    /// against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a property of the <em>rewrite</em>, not of the query it replaces. A statement
    /// burning millions of logical reads is a large opportunity, not a large risk; the risk
    /// is in what the substitution could do that the original would not. Impact and risk are
    /// recorded separately and should be read together — the work worth doing first is high
    /// impact and low risk, and that ordering is only visible if the two are not conflated.
    /// </para>
    /// <para>
    /// The tier is descriptive. Nothing in the shim reads it to decide whether to apply a
    /// rewrite, because everything present in configuration is approved by construction and
    /// an unapproved statement never reaches the registry at all. It exists so that a
    /// staged rollout can be planned, audited and explained after the fact.
    /// </para>
    /// </remarks>
    public enum RewriteRisk
    {
        /// <summary>
        /// Nobody has assessed this entry yet.
        /// </summary>
        /// <remarks>
        /// Deliberately the default, and deliberately not a synonym for <see cref="Low"/>.
        /// An unassessed rewrite that silently reads as low risk is precisely the failure
        /// this field exists to prevent.
        /// </remarks>
        Unclassified = 0,

        /// <summary>
        /// The replacement returns the same rows in the same order for every input, and can
        /// be shown to do so by inspection once its preconditions hold.
        /// </summary>
        /// <remarks>
        /// Dropping <c>LOWER()</c> from both sides of a predicate on a database proven
        /// case-insensitive is the archetype: gated on the collation, the two statements are
        /// indistinguishable. So is <c>ROW_NUMBER()</c> pagination rewritten as
        /// <c>OFFSET/FETCH</c> against a total ordering. If an argument for equivalence
        /// needs data characteristics rather than schema facts, it is not this tier.
        /// </remarks>
        Low = 1,

        /// <summary>
        /// The result set is unchanged, but the plan the optimiser produces is materially
        /// different and could regress on a shape nobody sampled.
        /// </summary>
        /// <remarks>
        /// Shedding <c>(@p IS NULL OR col = @p)</c> branches into per-parameter variants
        /// belongs here: each variant is provably equivalent for the parameter combination
        /// that selects it, but the plans stop being shared and the cache behaves
        /// differently. Replacing a table variable to escape its one-row estimate is the
        /// same story. These want measurement on real data before they leave shadow mode.
        /// </remarks>
        Moderate = 2,

        /// <summary>
        /// The replacement could return different rows, or its equivalence depends on data
        /// characteristics rather than on anything the schema guarantees.
        /// </summary>
        /// <remarks>
        /// Removing a <c>SELECT DISTINCT</c> is the archetype: it is only safe if the joins
        /// beneath it cannot multiply rows, which is a fact about the data and the customer's
        /// content model, not about the query. Nothing in this tier should ship without a
        /// per-database argument, and most of it should not ship at all.
        /// </remarks>
        High = 3
    }
}
