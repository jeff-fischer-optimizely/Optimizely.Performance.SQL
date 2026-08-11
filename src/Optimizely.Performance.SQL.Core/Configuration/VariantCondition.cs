using System.Text.Json.Serialization;

namespace Optimizely.Performance.SQL.Configuration
{
    /// <summary>
    /// A test against one runtime parameter. Conditions are how a family of
    /// pre-analyzed statements replaces dynamic SQL: instead of building a string at
    /// runtime to drop <c>(@p IS NULL OR col = @p)</c> predicates, every useful
    /// combination is authored ahead of time and selected by inspecting the parameters
    /// actually supplied on the command.
    /// </summary>
    public sealed class VariantCondition
    {
        /// <summary>
        /// Parameter name, with or without the leading <c>@</c>. Matched case-insensitively.
        /// </summary>
        [JsonPropertyName("parameter")]
        public string Parameter { get; set; }

        /// <summary>
        /// The test to apply to <see cref="Parameter"/>.
        /// </summary>
        [JsonPropertyName("test")]
        public ParameterTest Test { get; set; }

        /// <summary>
        /// Comparison operand for <see cref="ParameterTest.EqualsValue"/>, compared
        /// via invariant-culture string form. Ignored by the other tests.
        /// </summary>
        [JsonPropertyName("value")]
        public string Value { get; set; }

        public override string ToString()
        {
            return Test == ParameterTest.EqualsValue
                ? "@" + Parameter + " == " + Value
                : "@" + Parameter + " " + Test;
        }
    }

    /// <summary>
    /// The predicate applied to a parameter when selecting a variant.
    /// </summary>
    public enum ParameterTest
    {
        /// <summary>Parameter is absent from the command, or its value is null / <see cref="System.DBNull"/>.</summary>
        IsNull = 0,

        /// <summary>Parameter is present and carries a non-null value.</summary>
        IsNotNull = 1,

        /// <summary>Parameter is present (regardless of value).</summary>
        IsPresent = 2,

        /// <summary>Parameter is not supplied on the command at all.</summary>
        IsAbsent = 3,

        /// <summary>Parameter's invariant string form equals <see cref="VariantCondition.Value"/>, case-insensitively.</summary>
        EqualsValue = 4
    }
}
