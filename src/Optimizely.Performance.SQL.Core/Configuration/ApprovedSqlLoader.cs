using System;
using System.IO;
using System.Text.Json;

namespace Optimizely.Performance.SQL.Configuration
{
    /// <summary>
    /// Reads <c>config/approved-sql.json</c> from disk.
    /// </summary>
    public static class ApprovedSqlLoader
    {
        /// <summary>An empty document, used whenever configuration is missing or unreadable.</summary>
        public static ApprovedSqlDocument Empty
        {
            get { return new ApprovedSqlDocument { Statements = Array.Empty<ApprovedStatement>() }; }
        }

        /// <summary>
        /// Loads the document at <paramref name="path"/>, resolving relative paths against
        /// <paramref name="baseDirectory"/> (the application base directory by default).
        /// </summary>
        /// <remarks>
        /// A missing file yields an empty document rather than an exception: a site that
        /// has installed the package but not yet approved anything should start normally.
        /// A malformed file is a different matter and throws, because silently running
        /// unrewritten after a bad deploy is worse than failing loudly at startup.
        /// </remarks>
        public static ApprovedSqlDocument Load(string path, string baseDirectory = null)
        {
            var resolved = Resolve(path, baseDirectory);

            if (resolved == null || !File.Exists(resolved))
            {
                return Empty;
            }

            var json = File.ReadAllText(resolved);

            return Parse(json);
        }

        /// <summary>
        /// Loads the document, returning <see cref="Empty"/> and the failure reason
        /// instead of throwing. Intended for hot reload, where an operator editing a file
        /// must not be able to take the site down mid-request.
        /// </summary>
        public static bool TryLoad(string path, out ApprovedSqlDocument document, out string error, string baseDirectory = null)
        {
            try
            {
                document = Load(path, baseDirectory);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                document = Empty;
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Deserializes a document from JSON text.</summary>
        public static ApprovedSqlDocument Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return Empty;
            }

            var document = JsonSerializer.Deserialize<ApprovedSqlDocument>(
                json,
                ApprovedSqlDocument.SerializerOptions);

            if (document == null)
            {
                return Empty;
            }

            document.Statements ??= Array.Empty<ApprovedStatement>();

            return document;
        }

        /// <summary>Serializes a document, matching the format the sync tool writes.</summary>
        public static string Serialize(ApprovedSqlDocument document)
        {
            return JsonSerializer.Serialize(document, ApprovedSqlDocument.SerializerOptions);
        }

        /// <summary>Resolves a possibly-relative configuration path to an absolute one.</summary>
        public static string Resolve(string path, string baseDirectory = null)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            if (Path.IsPathRooted(path))
            {
                return path;
            }

            var root = baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory ?? Directory.GetCurrentDirectory();

            return Path.GetFullPath(Path.Combine(root, path));
        }
    }
}
