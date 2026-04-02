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

        public class ExportNode
        {
            public string Id;
            public double X, Y, Z;
        }

        public static List<WsproCsvRecord> ReadNetwork(Editor ed, Transaction tr, out int pipeCount, out int fittingCount, out List<ExportNode> exportNodes)
        {
            exportNodes = new List<ExportNode>();
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

            // Build fitting-based node map: FittingObjectId → (nodeId, position)
            // Fittings become EPANET nodes. Pipes connect to fitting nodes.
            var fittingNodeMap = new Dictionary<ObjectId, (int NodeId, Point3d Position)>();
            int nextNodeId = 1;

            // Collect all fitting IDs referenced by pipes
            var allFittingIds = new HashSet<ObjectId>();
            foreach (var pipe in rawPipes)
            {
                if (!pipe.StartFittingId.IsNull) allFittingIds.Add(pipe.StartFittingId);
                if (!pipe.EndFittingId.IsNull) allFittingIds.Add(pipe.EndFittingId);
            }

            // Resolve each fitting to a position and assign a node ID
            foreach (var fitId in allFittingIds)
            {
                try
                {
                    var fitObj = tr.GetObject(fitId, OpenMode.ForRead);
                    if (fitObj != null && PropertyExtractor.TryGetPosition(fitObj, out var pos))
                        fittingNodeMap[fitId] = (nextNodeId++, pos);
                }
                catch { }
            }

            fittingCount = fittingNodeMap.Count;
            ed.WriteMessage($"\nFound {pipeCount} pipes, {fittingCount} fittings as nodes.");

            // For pipe endpoints with no fitting (dead ends), use spatial clustering
            var orphanNodeMap = new Dictionary<int, Point3d>();

            // Build WsproCsvRecords
            var records = new List<WsproCsvRecord>();
            foreach (var pipe in rawPipes)
            {
                // Resolve start node: fitting position if available, else pipe endpoint
                string usId;
                Point3d usPoint;
                if (!pipe.StartFittingId.IsNull && fittingNodeMap.TryGetValue(pipe.StartFittingId, out var startNode))
                {
                    usId = startNode.NodeId.ToString();
                    usPoint = startNode.Position;
                }
                else
                {
                    int orphanId = FindOrCreateOrphanNode(pipe.StartPoint, orphanNodeMap, ref nextNodeId);
                    usId = orphanId.ToString();
                    usPoint = pipe.StartPoint;
                }

                // Resolve end node: fitting position if available, else pipe endpoint
                string dsId;
                Point3d dsPoint;
                if (!pipe.EndFittingId.IsNull && fittingNodeMap.TryGetValue(pipe.EndFittingId, out var endNode))
                {
                    dsId = endNode.NodeId.ToString();
                    dsPoint = endNode.Position;
                }
                else
                {
                    int orphanId = FindOrCreateOrphanNode(pipe.EndPoint, orphanNodeMap, ref nextNodeId);
                    dsId = orphanId.ToString();
                    dsPoint = pipe.EndPoint;
                }

                var record = new WsproCsvRecord
                {
                    UsId = usId,
                    DsId = dsId,
                    UsX = WsproCsvRecord.FmtCoord(usPoint.X),
                    UsY = WsproCsvRecord.FmtCoord(usPoint.Y),
                    DsX = WsproCsvRecord.FmtCoord(dsPoint.X),
                    DsY = WsproCsvRecord.FmtCoord(dsPoint.Y),
                    ElevationUs = WsproCsvRecord.Fmt(usPoint.Z),
                    ElevationDs = WsproCsvRecord.Fmt(dsPoint.Z),
                    Diameter = WsproCsvRecord.Fmt(pipe.DiameterMm, 0),
                    Length = WsproCsvRecord.Fmt(pipe.Length),
                    Material = pipe.Material,
                    PipeId = "1",
                    PipeStatus = "open",
                    Vertices = $"{WsproCsvRecord.FmtCoord(usPoint.X)} {WsproCsvRecord.FmtCoord(usPoint.Y)}|{WsproCsvRecord.FmtCoord(dsPoint.X)} {WsproCsvRecord.FmtCoord(dsPoint.Y)}",
                    UsIl = WsproCsvRecord.Fmt(usPoint.Z - (pipe.DiameterMm / 2000.0)),
                    DsIl = WsproCsvRecord.Fmt(dsPoint.Z - (pipe.DiameterMm / 2000.0)),
                };

                if (!pipe.EntityId.IsNull)
                    PropertySetManager.ReadValues(pipe.EntityId, tr, record);

                records.Add(record);
            }

            // Build export nodes from fittings + dead-end nodes
            foreach (var kvp in fittingNodeMap.OrderBy(k => k.Value.NodeId))
            {
                exportNodes.Add(new ExportNode
                {
                    Id = kvp.Value.NodeId.ToString(),
                    X = kvp.Value.Position.X,
                    Y = kvp.Value.Position.Y,
                    Z = kvp.Value.Position.Z
                });
            }
            foreach (var kvp in orphanNodeMap.OrderBy(k => k.Key))
            {
                exportNodes.Add(new ExportNode
                {
                    Id = kvp.Key.ToString(),
                    X = kvp.Value.X,
                    Y = kvp.Value.Y,
                    Z = kvp.Value.Z
                });
            }

            ed.WriteMessage($"\nGenerated {exportNodes.Count} nodes ({fittingNodeMap.Count} from fittings, {orphanNodeMap.Count} dead-ends).");

            return records;
        }

        // ── Internal pipe data ──

        private class RawPipe
        {
            public ObjectId EntityId;
            public Point3d StartPoint;
            public Point3d EndPoint;
            public ObjectId StartFittingId;
            public ObjectId EndFittingId;
            public double DiameterMm;
            public double Length;
            public string Material;
        }

        // ── Read pipes from network ──

        private static List<RawPipe> ReadPipes(DBObject network, Transaction tr, Editor ed)
        {
            var pipes = new List<RawPipe>();

            // Try multiple known property/method names for pipe IDs
            var pipeIds = GetObjectIdCollection(network, "PipeIds", "GetPipeIds")
                       ?? GetObjectIdCollection(network, "PressurePipeIds", "GetPressurePipeIds")
                       ?? GetObjectIdCollection(network, "LinePipeIds", "GetLinePipeIds");

            if (pipeIds == null)
            {
                ed.WriteMessage("\nCould not find PipeIds on network. Probing all properties...");

                // Dump all properties/methods on the network for diagnostics
                DumpNetworkMembers(network, ed);

                pipeIds = ProbeForObjectIds(network, tr, "Pipe");
                if (pipeIds == null)
                    pipeIds = ProbeForObjectIds(network, tr, "Line");
            }

            if (pipeIds == null || pipeIds.Count == 0)
            {
                ed.WriteMessage("\nNo pipes found in network after probing.");
                return pipes;
            }

            ed.WriteMessage($"\nFound {pipeIds.Count} pipe IDs.");

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
                    string material = PropertyExtractor.GetMaterialCode(pipeObj, tr);

                    // Get center-to-center length (preferred for EPANET)
                    PropertyExtractor.TryGetDouble(pipeObj, "Length2DCenterToCenter", out double lenC2C);
                    PropertyExtractor.TryGetLength(pipeObj, out double len);
                    double finalLen = lenC2C > 0 ? lenC2C : (len > 0 ? len : startPt.DistanceTo(endPt));

                    // Get fitting IDs at each end
                    ObjectId startFitId = ObjectId.Null;
                    ObjectId endFitId = ObjectId.Null;
                    try
                    {
                        if (PropertyExtractor.TryGetString(pipeObj, "StartFittingId", out var sfStr)) { }
                        var sfProp = pipeObj.GetType().GetProperty("StartFittingId", BindingFlags.Instance | BindingFlags.Public);
                        if (sfProp != null) { var v = sfProp.GetValue(pipeObj, null); if (v is ObjectId oid) startFitId = oid; }

                        var efProp = pipeObj.GetType().GetProperty("EndFittingId", BindingFlags.Instance | BindingFlags.Public);
                        if (efProp != null) { var v = efProp.GetValue(pipeObj, null); if (v is ObjectId oid) endFitId = oid; }

                        // Also try StartPartId / EndPartId as fallback
                        if (startFitId.IsNull)
                        {
                            var spProp = pipeObj.GetType().GetProperty("StartPartId", BindingFlags.Instance | BindingFlags.Public);
                            if (spProp != null) { var v = spProp.GetValue(pipeObj, null); if (v is ObjectId oid && !oid.IsNull) startFitId = oid; }
                        }
                        if (endFitId.IsNull)
                        {
                            var epProp = pipeObj.GetType().GetProperty("EndPartId", BindingFlags.Instance | BindingFlags.Public);
                            if (epProp != null) { var v = epProp.GetValue(pipeObj, null); if (v is ObjectId oid && !oid.IsNull) endFitId = oid; }
                        }
                    }
                    catch { }

                    pipes.Add(new RawPipe
                    {
                        EntityId = pipeId,
                        StartPoint = startPt,
                        EndPoint = endPt,
                        StartFittingId = startFitId,
                        EndFittingId = endFitId,
                        DiameterMm = dia < 10 ? dia * 1000 : dia,
                        Length = finalLen,
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

        // ── Orphan node handling (dead-end pipe endpoints with no fitting) ──

        private static int FindOrCreateOrphanNode(Point3d point, Dictionary<int, Point3d> orphanMap, ref int nextId)
        {
            // Check if an existing orphan node is close enough
            foreach (var kvp in orphanMap)
            {
                if (Distance2D(point, kvp.Value) < NodeTolerance)
                    return kvp.Key;
            }

            // Create new orphan node
            int id = nextId++;
            orphanMap[id] = point;
            return id;
        }

        // ── Node ID generation via spatial clustering (kept for backward compat) ──

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

        private static void DumpNetworkMembers(DBObject network, Editor ed)
        {
            var type = network.GetType();
            ed.WriteMessage($"\n  Network type: {type.FullName}");

            // Properties that return ObjectIdCollection or IEnumerable
            var props = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.GetIndexParameters().Length == 0)
                .OrderBy(p => p.Name)
                .ToList();

            ed.WriteMessage($"\n  Properties ({props.Count}):");
            foreach (var p in props.Take(30))
            {
                string val = "";
                try
                {
                    var v = p.GetValue(network, null);
                    if (v is ObjectIdCollection col)
                        val = $"ObjectIdCollection(count={col.Count})";
                    else if (v is IEnumerable enumerable && v is not string)
                    {
                        int count = 0;
                        foreach (var _ in enumerable) count++;
                        val = $"Enumerable(count={count})";
                    }
                    else
                        val = v?.ToString() ?? "<null>";
                }
                catch { val = "<error>"; }
                ed.WriteMessage($"\n    {p.Name} ({p.PropertyType.Name}) = {val}");
            }

            // Methods that take 0 args and return something
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => !m.IsSpecialName && m.GetParameters().Length == 0 && m.ReturnType != typeof(void))
                .OrderBy(m => m.Name)
                .ToList();

            ed.WriteMessage($"\n  Methods ({methods.Count}):");
            foreach (var m in methods.Where(m =>
                m.Name.IndexOf("Pipe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                m.Name.IndexOf("Fitting", StringComparison.OrdinalIgnoreCase) >= 0 ||
                m.Name.IndexOf("Get", StringComparison.OrdinalIgnoreCase) >= 0 ||
                m.Name.IndexOf("Id", StringComparison.OrdinalIgnoreCase) >= 0).Take(20))
            {
                ed.WriteMessage($"\n    {m.Name}() -> {m.ReturnType.Name}");
            }
        }

        private static string GetNetworkName(DBObject network)
        {
            if (PropertyExtractor.TryGetString(network, "Name", out var name) && !string.IsNullOrEmpty(name))
                return name;
            return network.GetType().Name;
        }
    }
}
