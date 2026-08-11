using System;

namespace Optimizely.Performance.SQL.Configuration
{
    /// <summary>
    /// Optimizely CMS major versions this shim targets.
    /// </summary>
    /// <remarks>
    /// V13 is deliberately absent. Its content store is not SQL Server, so a
    /// <c>DbProviderFactory</c>-based statement rewrite has nothing to attach to;
    /// that platform needs a separate interception strategy.
    /// </remarks>
    [Flags]
    public enum CmsVersion
    {
        None = 0,

        /// <summary>Optimizely CMS 11 (.NET Framework, System.Data.SqlClient).</summary>
        V11 = 1,

        /// <summary>Optimizely CMS 12 (.NET 6+, Microsoft.Data.SqlClient).</summary>
        V12 = 2,

        All = V11 | V12
    }
}
