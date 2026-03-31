#nullable disable
using System;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;

namespace C3DPlugin
{
    /// <summary>
    /// Export orchestrator: reads Civil 3D pressure network and writes WSPro CSV + EPANET .inp.
    /// </summary>
    public static class WsproExporter
    {
        public static void ExportNetwork(Editor ed, string outputFolder)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                ed.WriteMessage("\nNo active document.");
                return;
            }

            if (!Directory.Exists(outputFolder))
            {
                try { Directory.CreateDirectory(outputFolder); }
                catch (Exception ex)
                {
                    ed.WriteMessage($"\nFailed to create output folder: {ex.Message}");
                    return;
                }
            }

            using (var tr = doc.TransactionManager.StartTransaction())
            {
                var records = NetworkReader.ReadNetwork(ed, tr, out int pipeCount, out int fittingCount);

                if (records.Count == 0)
                {
                    ed.WriteMessage("\nNo pipe data to export.");
                    tr.Commit();
                    return;
                }

                // Write WSPro CSV
                string csvPath = Path.Combine(outputFolder, "WSPro_Export.csv");
                try
                {
                    CsvWriter.Write(csvPath, records);
                    ed.WriteMessage($"\nWSPro CSV written: {csvPath}");
                    ed.WriteMessage($"\n  {records.Count} pipes, 30 columns");
                }
                catch (Exception ex)
                {
                    ed.WriteMessage($"\nFailed to write CSV: {ex.Message}");
                }

                // Write EPANET .inp
                string inpPath = Path.Combine(outputFolder, "WSPro_Export.inp");
                try
                {
                    EpanetWriter.Write(inpPath, records);
                    ed.WriteMessage($"\nEPANET .inp written: {inpPath}");
                }
                catch (Exception ex)
                {
                    ed.WriteMessage($"\nFailed to write EPANET: {ex.Message}");
                }

                ed.WriteMessage($"\nExport complete: {pipeCount} pipes, {fittingCount} fittings.");

                tr.Commit();
            }
        }
    }
}
