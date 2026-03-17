using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace C3DPlugin
{
    public class WsproNode
    {
        public string Id { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public string Type { get; set; }
    }

    public class WsproPipe
    {
        public string Id { get; set; }
        public string FromNodeId { get; set; }
        public string ToNodeId { get; set; }
        public double DiameterMm { get; set; }
        public string Material { get; set; }
    }

    public static class WsproCsvReader
    {
        public static List<WsproNode> ReadNodes(string csvPath)
        {
            if (!File.Exists(csvPath))
                throw new FileNotFoundException("Nodes CSV not found.", csvPath);

            var nodes = new List<WsproNode>();
            using (var fs = new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs))
            {
                if (reader.EndOfStream)
                    return nodes;

                var headerLine = reader.ReadLine();
                if (headerLine == null)
                    return nodes;

                var headers = SplitCsvLine(headerLine);

                // WSPro export headers seen: ASSET_ID,X,Y,Z_ELEV,TYPE
                int idxId = IndexOf(headers, "ASSET_ID", "NodeID", "ID", "NodeId", "node_id");
                int idxX = IndexOf(headers, "X", "Easting");
                int idxY = IndexOf(headers, "Y", "Northing");
                int idxZ = IndexOf(headers, "Z_ELEV", "Z", "Level", "Elevation");
                int idxType = IndexOf(headers, "Type", "NodeType");

                RequireColumns(
                    csvPath,
                    headers,
                    ("ASSET_ID/ID", idxId),
                    ("X", idxX),
                    ("Y", idxY),
                    ("Z_ELEV/Z", idxZ));

                while (!reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var cols = SplitCsvLine(line);
                    var node = new WsproNode
                    {
                        Id = GetString(cols, idxId),
                        X = GetDouble(cols, idxX),
                        Y = GetDouble(cols, idxY),
                        Z = GetDouble(cols, idxZ),
                        Type = GetString(cols, idxType)
                    };

                    if (!string.IsNullOrEmpty(node.Id))
                        nodes.Add(node);
                }
            }

            return nodes;
        }

        public static List<WsproPipe> ReadPipes(string csvPath)
        {
            if (!File.Exists(csvPath))
                throw new FileNotFoundException("Pipes CSV not found.", csvPath);

            var pipes = new List<WsproPipe>();
            using (var fs = new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs))
            {
                if (reader.EndOfStream)
                    return pipes;

                var headerLine = reader.ReadLine();
                if (headerLine == null)
                    return pipes;

                var headers = SplitCsvLine(headerLine);

                // WSPro export headers seen include: DIAMETER,MATERIAL,US_ID,DS_ID,PIPE_ID
                int idxId = IndexOf(headers, "PIPE_ID", "PipeID", "ID", "PipeId", "pipe_id");
                int idxFrom = IndexOf(headers, "US_ID", "FromNode", "StartNode", "from_node");
                int idxTo = IndexOf(headers, "DS_ID", "ToNode", "EndNode", "to_node");
                int idxDiameter = IndexOf(headers, "DIAMETER", "Diameter", "PipeDiameter", "diameter_mm");
                int idxMaterial = IndexOf(headers, "MATERIAL", "Material", "PipeMaterial");

                RequireColumns(
                    csvPath,
                    headers,
                    ("US_ID/FromNode", idxFrom),
                    ("DS_ID/ToNode", idxTo),
                    ("DIAMETER", idxDiameter));

                while (!reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var cols = SplitCsvLine(line);
                    var pipe = new WsproPipe
                    {
                        Id = GetString(cols, idxId),
                        FromNodeId = GetString(cols, idxFrom),
                        ToNodeId = GetString(cols, idxTo),
                        DiameterMm = GetDouble(cols, idxDiameter),
                        Material = GetString(cols, idxMaterial)
                    };

                    if (!string.IsNullOrEmpty(pipe.FromNodeId) && !string.IsNullOrEmpty(pipe.ToNodeId))
                        pipes.Add(pipe);
                }
            }

            return pipes;
        }

        private static string[] SplitCsvLine(string line)
        {
            // Simple split; adjust if WSPro exports quoted commas.
            return line.Split(',');
        }

        private static int IndexOf(string[] headers, params string[] candidates)
        {
            for (int i = 0; i < headers.Length; i++)
            {
                var h = Normalize(headers[i]);
                foreach (var cand in candidates)
                {
                    if (h.Equals(Normalize(cand), StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }
            return -1;
        }

        private static string GetString(string[] cols, int index)
        {
            if (index < 0 || index >= cols.Length)
                return string.Empty;
            return Normalize(cols[index]);
        }

        private static double GetDouble(string[] cols, int index)
        {
            var s = GetString(cols, index);
            if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
                return v;
            return 0.0;
        }

        private static void RequireColumns(string csvPath, string[] headers, params (string label, int index)[] required)
        {
            var missing = required.Where(r => r.index < 0).Select(r => r.label).ToList();
            if (missing.Count == 0)
                return;

            string headerDump = string.Join(", ", headers.Select(h => h.Trim()));
            throw new InvalidDataException(
                $"CSV '{Path.GetFileName(csvPath)}' is missing required column(s): {string.Join(", ", missing)}. " +
                $"Headers found: {headerDump}");
        }

        private static string Normalize(string s)
        {
            if (s == null) return string.Empty;
            var t = s.Trim();
            if (t.Length >= 2 && t[0] == '"' && t[^1] == '"')
                t = t.Substring(1, t.Length - 2);
            return t.Trim();
        }
    }
}

