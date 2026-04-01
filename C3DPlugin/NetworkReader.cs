#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;

namespace C3DPlugin
{
    /// <summary>
    /// Reads a Civil 3D pressure pipe network and produces WsproCsvRecord[].
    /// Uses reflection for Civil 3D API compatibility (same pattern as WsproImporter).
    /// </summary>
    public static class NetworkReader
    {
        private const double NodeTolerance = 0.01; // spatial clustering tolerance

        public static List<WsproCsvRecord> ReadNetwork(Editor ed, Transaction tr, out int pipeCount, out int fittingCount)
        {
            pipeCount = 0;
            fittingCount = 0;

            var civilDoc = CivilApplication.ActiveDocument;
            if (civilDoc == null)
            {
                ed.WriteMessage("\nNo active Civil 3D document.");
                return new List<WsproCsvRecord>();
            }

            // Find pressure pipe network
            if (!TryGetNetworkId(civilDoc, tr, ed, out ObjectId networkId))
            {
                ed.WriteMessage("\nNo pressure pipe network found in drawing.");
                return new List<WsproCsvRecord>();
            }

            var netObj = tr.GetObject(networkId, OpenMode.ForRead);
            if (netObj == null)
            {
                ed.WriteMessage("\nFailed to open pressure network.");
                return new List<WsproCsvRecord>();
            }

            ed.WriteMessage($"\nReading network: {GetNetworkName(netObj)}");

            // Collect all pipe data
            var rawPipes = ReadPipes(netObj, tr, ed);
            pipeCount = rawPipes.Count;

            // Collect all fitting positions
            var fittingPositions = ReadFittingPositions(netObj, tr, ed);
            fittingCount = fittingPositions.Count;

            ed.WriteMessage($"\nFound {pipeCount} pipes, {fittingCount} fittings in network.");

            // Generate node IDs by spatial clustering
            var nodeMap = BuildNodeMap(rawPipes, fittingPositions);
            ed.WriteMessage($"\nGenerated {nodeMap.Count} unique node IDs.");

            // Build WsproCsvRecords
            var records = new List<WsproCsvRecord>();
            foreach (var pipe in rawPipes)
            {
                string usId = FindNodeId(nodeMap, pipe.StartPoint);
                string dsId = FindNodeId(nodeMap, pipe.EndPoint);

                var record = new WsproCsvRecord
                {
                    UsId = usId,
                    DsId = dsId,
                    UsX = WsproCsvRecord.FmtCoord(pipe.StartPoint.X),
                    UsY = WsproCsvRecord.FmtCoord(pipe.StartPoint.Y),
                    DsX = WsproCsvRecord.FmtCoord(pipe.EndPoint.X),
                    DsY = WsproCsvRecord.FmtCoord(pipe.EndPoint.Y),
                    ElevationUs = WsproCsvRecord.Fmt(pipe.StartPoint.Z),
                    ElevationDs = WsproCsvRecord.Fmt(pipe.EndPoint.Z),
                    Diameter = WsproCsvRecord.Fmt(pipe.DiameterMm, 0),
                    Length = WsproCsvRecord.Fmt(pipe.Length),
                    Material = pipe.Material,
                    PipeId = $"{usId}_{dsId}",
                    PipeStatus = "open",
                    Vertices = $"{WsproCsvRecord.FmtCoord(pipe.StartPoint.X)} {WsproCsvRecord.FmtCoord(pipe.StartPoint.Y)}|{WsproCsvRecord.FmtCoord(pipe.EndPoint.X)} {WsproCsvRecord.FmtCoord(pipe.EndPoint.Y)}",

                    // Invert levels: centerline Z - radius
                    UsIl = WsproCsvRecord.Fmt(pipe.StartPoint.Z - (pipe.DiameterMm / 2000.0)),
                    DsIl = WsproCsvRecord.Fmt(pipe.EndPoint.Z - (pipe.DiameterMm / 2000.0)),
                };

                // Read PropertySet values for round-trip (simulation data from previous import)
                if (!pipe.EntityId.IsNull)
                    PropertySetManager.ReadValues(pipe.EntityId, tr, record);

                records.Add(record);
            }

            return records;
        }

        // ── Internal pipe data ──

        private class RawPipe
        {
            public ObjectId EntityId;
            public Point3d StartPoint;
            public Point3d EndPoint;
            public double DiameterMm;
            public double Length;
            public string Material;
        }

        // ── Read pipes from network ──

        private static List<RawPipe> ReadPipes(DBObject network, Transaction tr, Editor ed)
        {
            var pipes = new List<RawPipe>();

            // Get PipeIds collection from the network
            var pipeIds = GetObjectIdCollection(network, "PipeIds", "GetPipeIds");
            if (pipeIds == null)
            {
                ed.WriteMessage("\nCould not find PipeIds on network. Probing...");
                pipeIds = ProbeForObjectIds(network, tr, "Pipe");
            }

            if (pipeIds == null || pipeIds.Count == 0)
            {
                ed.WriteMessage("\nNo pipes found in network.");
                return pipes;
            }

            foreach (ObjectId pipeId in pipeIds)
            {
                if (pipeId.IsNull) continue;

                try
                {
                    var pipeObj = tr.GetObject(pipeId, OpenMode.ForRead);
                    if (pipeObj == null) continue;

                    if (!PropertyExtractor.TryGetStartPoint(pipeObj, out var startPt)) continue;
                    if (!PropertyExtractor.TryGetEndPoint(pipeObj, out var endPt)) continue;

                    PropertyExtractor.TryGetDiameter(pipeObj, tr, out double dia);
                    PropertyExtractor.TryGetLength(pipeObj, out double len);
                    string material = PropertyExtractor.GetMaterialCode(pipeObj, tr);

                    pipes.Add(new RawPipe
                    {
                        EntityId = pipeId,
                        StartPoint = startPt,
                        EndPoint = endPt,
                        DiameterMm = dia,
                        Length = len > 0 ? len : startPt.DistanceTo(endPt),
                        Material = material
                    });
                }
                catch { }
            }

            return pipes;
        }

        // ── Read fitting positions from network ──

        private static List<Point3d> ReadFittingPositions(DBObject network, Transaction tr, Editor ed)
        {
            var positions = new List<Point3d>();

            var fittingIds = GetObjectIdCollection(network, "FittingIds", "GetFittingIds");
            if (fittingIds == null)
                fittingIds = ProbeForObjectIds(network, tr, "Fitting");

            if (fittingIds == null || fittingIds.Count == 0)
                return positions;

            foreach (ObjectId fId in fittingIds)
            {
                if (fId.IsNull) continue;
                try
                {
                    var fObj = tr.GetObject(fId, OpenMode.ForRead);
                    if (fObj != null && PropertyExtractor.TryGetPosition(fObj, out var pos))
                        positions.Add(pos);
                }
                catch { }
            }

            return positions;
        }

        // ── Node ID generation via spatial clustering ──

        private static Dictionary<int, Point3d> BuildNodeMap(List<RawPipe> pipes, List<Point3d> fittingPositions)
        {
            var allPoints = new List<Point3d>();

            foreach (var pipe in pipes)
            {
                allPoints.Add(pipe.StartPoint);
                allPoints.Add(pipe.EndPoint);
            }

            allPoints.AddRange(fittingPositions);

            // Cluster points within tolerance
            var clusters = new List<Point3d>(); // representative point for each cluster
            foreach (var pt in allPoints)
            {
                bool found = false;
                for (int i = 0; i < clusters.Count; i++)
                {
                    if (Distance2D(pt, clusters[i]) < NodeTolerance)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                    clusters.Add(pt);
            }

            // Assign sequential integer IDs
            var nodeMap = new Dictionary<int, Point3d>();
            for (int i = 0; i < clusters.Count; i++)
            {
                nodeMap[i + 1] = clusters[i]; // IDs start at 1
            }

            return nodeMap;
        }

        private static string FindNodeId(Dictionary<int, Point3d> nodeMap, Point3d point)
        {
            foreach (var kvp in nodeMap)
            {
                if (Distance2D(point, kvp.Value) < NodeTolerance)
                    return kvp.Key.ToString();
            }

            // Should not happen if BuildNodeMap was called with all points
            return "0";
        }

        private static double Distance2D(Point3d a, Point3d b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // ── Reflection helpers for reading network object ID collections ──

        private static List<ObjectId> GetObjectIdCollection(DBObject network, string propertyName, string methodName)
        {
            var netType = network.GetType();

            // Try property first (e.g., PipeIds)
            try
            {
                var prop = netType.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
                if (prop != null)
                {
                    var val = prop.GetValue(network, null);
                    return ExtractObjectIds(val);
                }
            }
            catch { }

            // Try method (e.g., GetPipeIds())
            try
            {
                var method = netType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                if (method != null)
                {
                    var val = method.Invoke(network, null);
                    return ExtractObjectIds(val);
                }
            }
            catch { }

            return null;
        }

        private static List<ObjectId> ExtractObjectIds(object value)
        {
            if (value == null) return null;

            var ids = new List<ObjectId>();

            if (value is ObjectIdCollection oidCol)
            {
                foreach (ObjectId id in oidCol)
                    ids.Add(id);
                return ids;
            }

            if (value is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item is ObjectId oid)
                        ids.Add(oid);
                }

                if (ids.Count > 0) return ids;
            }

            return ids.Count > 0 ? ids : null;
        }

        private static List<ObjectId> ProbeForObjectIds(DBObject network, Transaction tr, string keyword)
        {
            var netType = network.GetType();

            // Try all properties/methods whose name contains the keyword
            foreach (var prop in netType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 &&
                            p.GetIndexParameters().Length == 0)
                .OrderByDescending(p => p.Name.IndexOf("Id", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0))
            {
                try
                {
                    var val = prop.GetValue(network, null);
                    var ids = ExtractObjectIds(val);
                    if (ids != null && ids.Count > 0)
                        return ids;
                }
                catch { }
            }

            foreach (var method in netType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 &&
                            m.GetParameters().Length == 0 &&
                            m.ReturnType != typeof(void)))
            {
                try
                {
                    var val = method.Invoke(network, null);
                    var ids = ExtractObjectIds(val);
                    if (ids != null && ids.Count > 0)
                        return ids;
                }
                catch { }
            }

            return null;
        }

        // ── Helpers ──

        private static bool TryGetNetworkId(CivilDocument civilDoc, Transaction tr, Editor ed, out ObjectId networkId)
        {
            networkId = ObjectId.Null;

            var pressureAsm = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "AeccPressurePipesMgd", StringComparison.OrdinalIgnoreCase));
            if (pressureAsm == null) return false;

            Type docExt = pressureAsm.GetType("Autodesk.Civil.ApplicationServices.CivilDocumentPressurePipesExtension", throwOnError: false);
            if (docExt == null) return false;

            var getIds = docExt.GetMethod("GetPressurePipeNetworkIds", BindingFlags.Public | BindingFlags.Static);
            if (getIds == null) return false;

            object result;
            try { result = getIds.Invoke(null, new object[] { civilDoc }); }
            catch { return false; }

            if (result is not IEnumerable enumerable) return false;

            foreach (var item in enumerable)
            {
                if (item is ObjectId netId && !netId.IsNull)
                {
                    networkId = netId;
                    return true;
                }
            }

            return false;
        }

        private static string GetNetworkName(DBObject network)
        {
            if (PropertyExtractor.TryGetString(network, "Name", out var name) && !string.IsNullOrEmpty(name))
                return name;
            return network.GetType().Name;
        }
    }
}
