using System.Text.Json;
using System.Text.Json.Serialization;

namespace Optimizely.Performance.SQL.Configuration
{
    /// <summary>
    /// Root of <c>config/approved-sql.json</c>: the generated, machine-readable half of
    /// the approval process. Authored by the sync tool from the markdown records under
    /// <c>approvals/</c>, never by hand.
    /// </summary>
    public sealed class ApprovedSqlDocument
    {
        /// <summary>Schema version of this document, for forward compatibility.</summary>
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = 1;

        /// <summary>Warning banner emitted into the generated file.</summary>
        [JsonPropertyName("generatedBy")]
        public string GeneratedBy { get; set; }

        /// <summary>The reviewed statements.</summary>
        [JsonPropertyName("statements")]
        public ApprovedStatement[] Statements { get; set; }

        /// <summary>Serializer settings shared by the loader and the sync tool.</summary>
        public static JsonSerializerOptions SerializerOptions
        {
            get
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };
                options.Converters.Add(new JsonStringEnumConverter());
                return options;
            }
        }
    }
}
