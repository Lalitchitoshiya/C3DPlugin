#nullable disable
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace C3DPlugin
{
    public static class EpanetWriter
    {
        /// <summary>
        /// Writes an EPANET .inp file from WsproCsvRecords.
        /// Produces a basic but valid EPANET input file.
        /// </summary>
        public static void Write(string outputPath, List<WsproCsvRecord> records)
        {
            // Build unique node set from pipe endpoints
            var nodes = new Dictionary<string, (double X, double Y, double Elev)>();

            foreach (var r in records)
            {
                if (!string.IsNullOrEmpty(r.UsId) && !nodes.ContainsKey(r.UsId))
                {
                    double.TryParse(r.UsX, NumberStyles.Any, CultureInfo.InvariantCulture, out double x);
                    double.TryParse(r.UsY, NumberStyles.Any, CultureInfo.InvariantCulture, out double y);
                    double.TryParse(r.ElevationUs, NumberStyles.Any, CultureInfo.InvariantCulture, out double elev);
                    nodes[r.UsId] = (x, y, elev);
                }

                if (!string.IsNullOrEmpty(r.DsId) && !nodes.ContainsKey(r.DsId))
                {
                    double.TryParse(r.DsX, NumberStyles.Any, CultureInfo.InvariantCulture, out double x);
                    double.TryParse(r.DsY, NumberStyles.Any, CultureInfo.InvariantCulture, out double y);
                    double.TryParse(r.ElevationDs, NumberStyles.Any, CultureInfo.InvariantCulture, out double elev);
                    nodes[r.DsId] = (x, y, elev);
                }
            }

            using var w = new StreamWriter(outputPath, false, System.Text.Encoding.UTF8);

            // TITLE
            w.WriteLine("[TITLE]");
            w.WriteLine("Exported from Civil 3D via WSPro Plugin");
            w.WriteLine();

            // JUNCTIONS
            w.WriteLine("[JUNCTIONS]");
            w.WriteLine(";ID              \tElev          \tDemand");
            foreach (var kvp in nodes.OrderBy(n => n.Key))
            {
                w.WriteLine($" {kvp.Key,-16}\t{F(kvp.Value.Elev),-14}\t0.0");
            }
            w.WriteLine();

            // PIPES
            w.WriteLine("[PIPES]");
            w.WriteLine(";ID              \tNode1          \tNode2          \tLength        \tDiameter      \tRoughness     \tMinorLoss \tStatus");
            foreach (var r in records)
            {
                string pipeId = !string.IsNullOrEmpty(r.PipeId) ? r.PipeId : $"{r.UsId}_{r.DsId}";
                double.TryParse(r.Length, NumberStyles.Any, CultureInfo.InvariantCulture, out double len);
                double.TryParse(r.Diameter, NumberStyles.Any, CultureInfo.InvariantCulture, out double dia);
                double.TryParse(r.Roughness, NumberStyles.Any, CultureInfo.InvariantCulture, out double rough);
                if (rough <= 0) rough = 130; // default Hazen-Williams C

                string status = string.IsNullOrEmpty(r.PipeStatus) ? "Open" : r.PipeStatus;
                w.WriteLine($" {pipeId,-16}\t{r.UsId,-15}\t{r.DsId,-15}\t{F(len),-14}\t{F(dia),-14}\t{F(rough),-14}\t0         \t{status}");
            }
            w.WriteLine();

            // COORDINATES
            w.WriteLine("[COORDINATES]");
            w.WriteLine(";Node           \tX-Coord        \tY-Coord");
            foreach (var kvp in nodes.OrderBy(n => n.Key))
            {
                w.WriteLine($" {kvp.Key,-16}\t{F4(kvp.Value.X),-15}\t{F4(kvp.Value.Y)}");
            }
            w.WriteLine();

            // OPTIONS
            w.WriteLine("[OPTIONS]");
            w.WriteLine(" Units             \tLPS");
            w.WriteLine(" Headloss          \tH-W");
            w.WriteLine();

            // TIMES
            w.WriteLine("[TIMES]");
            w.WriteLine(" Duration          \t0:00");
            w.WriteLine();

            w.WriteLine("[END]");
        }

        private static string F(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
        private static string F4(double v) => v.ToString("F4", CultureInfo.InvariantCulture);
    }
}
