#nullable disable
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace C3DPlugin
{
    public static class CsvWriter
    {
        /// <summary>
        /// Writes WsproCsvRecords to a WSPro-format CSV file.
        /// All values are double-quoted to match WSPro export convention.
        /// </summary>
        public static void Write(string outputPath, List<WsproCsvRecord> records)
        {
            using var writer = new StreamWriter(outputPath, false, System.Text.Encoding.UTF8);

            // Header row
            writer.WriteLine(string.Join(",", WsproCsvRecord.Headers.Select(Quote)));

            // Data rows
            foreach (var record in records)
            {
                var row = record.ToRow();
                writer.WriteLine(string.Join(",", row.Select(Quote)));
            }
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
        }
    }
}
