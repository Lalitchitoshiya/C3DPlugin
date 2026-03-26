using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;

namespace C3DPlugin
{
    public static class WsproImporter
    {
        public static void DiagnosePressureApi(Editor ed)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                ed.WriteMessage("\nNo active document.");
                return;
            }

            var civilDoc = CivilApplication.ActiveDocument;
            if (civilDoc == null)
            {
                ed.WriteMessage("\nNo active Civil 3D document.");
                return;
            }

            ed.WriteMessage("\n--- WSPro Pressure API DIAG ---");
            ed.WriteMessage("\nCivilDocument runtime type: " + civilDoc.GetType().FullName);

            var pressureAsm = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "AeccPressurePipesMgd", StringComparison.OrdinalIgnoreCase));

            ed.WriteMessage("\nAeccPressurePipesMgd loaded: " + (pressureAsm != null));
            if (pressureAsm != null)
                ed.WriteMessage("\nAeccPressurePipesMgd version: " + pressureAsm.GetName().Version);

            // 1) Dump CivilDocument properties that look pressure-related
            var props = civilDoc.GetType()
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.Name.IndexOf("Pressure", StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(p => p.Name)
                .ToList();

            ed.WriteMessage($"\nCivilDocument pressure-related properties: {props.Count}");
            foreach (var p in props)
            {
                object val = null;
                string valInfo = "";
                try
                {
                    val = p.GetValue(civilDoc, null);
                }
                catch (Exception ex)
                {
                    valInfo = " <get failed: " + ex.GetType().Name + ">";
                }

                if (val == null)
                {
                    ed.WriteMessage($"\n- {p.Name} : {p.PropertyType.FullName} = <null>{valInfo}");
                    continue;
                }

                int count = TryCountEnumerable(val, out int c) ? c : -1;
                if (count >= 0)
                    ed.WriteMessage($"\n- {p.Name} : {p.PropertyType.FullName} (count={count})");
                else
                    ed.WriteMessage($"\n- {p.Name} : {p.PropertyType.FullName}");
            }

            // 2) Dump candidate extension types/methods in AeccPressurePipesMgd
            if (pressureAsm != null)
            {
                var extTypes = pressureAsm.GetTypes()
                    .Where(t => t.IsAbstract && t.IsSealed &&
                                t.FullName != null &&
                                t.FullName.IndexOf("Pressure", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                t.FullName.IndexOf("Extension", StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(t => t.FullName)
                    .ToList();

                ed.WriteMessage($"\nAeccPressurePipesMgd *Extension types: {extTypes.Count}");
                foreach (var t in extTypes)
                {
                    ed.WriteMessage("\n- " + t.FullName);

                    var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .Where(m =>
                        {
                            var ps = m.GetParameters();
                            return ps.Length == 1 && ps[0].ParameterType.IsInstanceOfType(civilDoc);
                        })
                        .OrderBy(m => m.Name)
                        .ToList();

                    foreach (var m in methods)
                    {
                        ed.WriteMessage($"\n    {m.Name}({m.GetParameters()[0].ParameterType.Name}) -> {m.ReturnType.Name}");
                    }
                }

                // 3) Dump PressurePipeNetwork.Create(...) overloads so we can see exact signature
                var netType = pressureAsm
                    .GetTypes()
                    .FirstOrDefault(t =>
                        t.IsClass &&
                        (t.Name.IndexOf("PressurePipeNetwork", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         t.Name.IndexOf("PressureNetwork", StringComparison.OrdinalIgnoreCase) >= 0));

                if (netType != null)
                {
                    ed.WriteMessage("\nPressure network type: " + netType.FullName);
                    var createMethods = netType
                        .GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .Where(m => m.Name.Equals("Create", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(m => m.GetParameters().Length)
                        .ToList();

                    ed.WriteMessage($"\nCreate overloads on {netType.Name}: {createMethods.Count}");
                    foreach (var m in createMethods)
                    {
                        var ps = m.GetParameters();
                        string sig = string.Join(", ", ps.Select(p => p.ParameterType.Name + " " + p.Name));
                        ed.WriteMessage($"\n- Create({sig}) -> {m.ReturnType.Name}");
                    }
                }
                else
                {
                    ed.WriteMessage("\nNo PressurePipeNetwork/PressureNetwork type found in AeccPressurePipesMgd.");
                }
            }

            ed.WriteMessage("\n--- END DIAG ---");
        }

        public static void ImportNetwork(Editor ed, string nodesCsv, string pipesCsv)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                ed.WriteMessage("\nNo active document.");
                return;
            }

            var civilDoc = CivilApplication.ActiveDocument;
            if (civilDoc == null)
            {
                ed.WriteMessage("\nNo active Civil 3D document.");
                return;
            }

            List<WsproNode> nodes;
            List<WsproPipe> pipes;

            try
            {
                nodes = WsproCsvReader.ReadNodes(nodesCsv);
                pipes = WsproCsvReader.ReadPipes(pipesCsv);
            }
            catch (Exception ex)
            {
                ed.WriteMessage("\nFailed to read CSV files: " + ex.Message);
                return;
            }

            if (nodes.Count == 0 || pipes.Count == 0)
            {
                ed.WriteMessage("\nNo nodes or pipes read from CSV. Check column mappings in WsproModels.cs.");
                return;
            }

            ed.WriteMessage($"\nRead {nodes.Count} nodes and {pipes.Count} pipes from WSPro CSVs.");

            using (var tr = doc.TransactionManager.StartTransaction())
            {
                if (!TryGetFirstPartsListId(civilDoc, tr, ed, out ObjectId partsListId))
                {
                    tr.Commit();
                    return;
                }

                // Debug help: if nothing matches, we can quickly see what sizes exist.
                // (Callable separately via WSPRO_DIAG_PARTS as well.)

                // Prefer reusing an existing Pressure Pipe Network if one already exists in the drawing.
                if (!TryGetExistingPressureNetwork(civilDoc, tr, ed, out ObjectId networkId))
                {
                    if (!TryCreatePressureNetwork(civilDoc, "WSPro_Import", partsListId, ed, out networkId))
                    {
                        tr.Commit();
                        return;
                    }
                }

                if (networkId.IsNull)
                {
                    tr.Commit();
                    return;
                }

                var nodeDict = nodes
                    .Where(n => !string.IsNullOrEmpty(n.Id))
                    .GroupBy(n => n.Id)
                    .ToDictionary(g => g.Key, g => g.First());

                int pipeCreated = 0;
                int pipeSkippedNoSize = 0;
                int pipeSkippedMissingNode = 0;
                var noSizeDiameters = new HashSet<double>();
                var nodeCreatedPipeCount = new Dictionary<string, int>(StringComparer.Ordinal);

                foreach (var pipe in pipes)
                {
                    if (!nodeDict.TryGetValue(pipe.FromNodeId ?? string.Empty, out var fromNode) ||
                        !nodeDict.TryGetValue(pipe.ToNodeId ?? string.Empty, out var toNode))
                    {
                        pipeSkippedMissingNode++;
                        continue;
                    }

                    var startPt = new Point3d(fromNode.X, fromNode.Y, fromNode.Z);
                    var endPt = new Point3d(toNode.X, toNode.Y, toNode.Z);

                    if (!TryFindPipePartSize(partsListId, tr, pipe.DiameterMm, out PressurePartSize partSize))
                    {
                        pipeSkippedNoSize++;
                        if (noSizeDiameters.Count < 8)
                            noSizeDiameters.Add(pipe.DiameterMm);
                        continue;
                    }

                    try
                    {
                        if (TryCreatePipe(networkId, tr, partSize, startPt, endPt, ed, out ObjectId createdPipeId) &&
                            !createdPipeId.IsNull)
                        {
                            pipeCreated++;
                            var fromId = pipe.FromNodeId ?? string.Empty;
                            var toId = pipe.ToNodeId ?? string.Empty;
                            if (!string.IsNullOrEmpty(fromId))
                                nodeCreatedPipeCount[fromId] = nodeCreatedPipeCount.GetValueOrDefault(fromId) + 1;
                            if (!string.IsNullOrEmpty(toId))
                                nodeCreatedPipeCount[toId] = nodeCreatedPipeCount.GetValueOrDefault(toId) + 1;
                        }
                    }
                    catch
                    {
                        // Skip pipe on failure, keep processing.
                    }
                }

                ed.WriteMessage($"\nCreated {pipeCreated} pressure pipes in network 'WSPro_Import'.");
                if (pipeSkippedMissingNode > 0)
                    ed.WriteMessage($"\nSkipped {pipeSkippedMissingNode} pipes due to missing node IDs.");
                if (pipeSkippedNoSize > 0)
                {
                    ed.WriteMessage($"\nSkipped {pipeSkippedNoSize} pipes due to no matching part size (check diameter units / parts list).");
                    if (noSizeDiameters.Count > 0)
                    {
                        var sample = string.Join(", ", noSizeDiameters.OrderBy(x => x).Select(x => x.ToString("0.###")));
                        ed.WriteMessage($"\nUnmatched DIAMETER sample(s): {sample}");
                        ed.WriteMessage("\nTip: run WSPRO_DIAG_PARTS to print available part sizes in the current Parts List.");
                    }
                }

                PostProcessJunctionFittings(networkId, partsListId, nodeDict, pipes, tr, ed, nodeCreatedPipeCount);

                tr.Commit();
            }
        }

        /// <summary>
        /// Lists pressure fittings in the first pressure network (sample positions and part names).
        /// </summary>
        public static void DiagnoseFittings(Editor ed)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                ed.WriteMessage("\nNo active document.");
                return;
            }

            var civilDoc = CivilApplication.ActiveDocument;
            if (civilDoc == null)
            {
                ed.WriteMessage("\nNo active Civil 3D document.");
                return;
            }

            using (var tr = doc.TransactionManager.StartTransaction())
            {
                if (!TryGetExistingPressureNetwork(civilDoc, tr, ed, out ObjectId networkId) || networkId.IsNull)
                {
                    ed.WriteMessage("\nNo Pressure Pipe Network found in the drawing. Create or import one first.");
                    tr.Commit();
                    return;
                }

                if (tr.GetObject(networkId, OpenMode.ForRead) is not PressurePipeNetwork network)
                {
                    tr.Commit();
                    return;
                }

                ed.WriteMessage("\n--- WSPro FITTINGS DIAG ---");
                ed.WriteMessage($"\nNetwork ObjectId: {networkId}");

                object fittingIdsObj = null;
                try
                {
                    fittingIdsObj = network.GetFittingIds();
                }
                catch (Exception ex)
                {
                    ed.WriteMessage("\nGetFittingIds failed: " + ex.Message);
                    tr.Commit();
                    return;
                }

                int count = 0;
                if (fittingIdsObj is IEnumerable enumerableIds)
                {
                    foreach (var _ in enumerableIds)
                        count++;
                }

                ed.WriteMessage($"\nFitting count: {count}");

                int sample = 0;
                const int maxSample = 15;
                if (fittingIdsObj is IEnumerable fittingIds)
                {
                    foreach (ObjectId fid in fittingIds)
                    {
                        if (fid.IsNull || sample >= maxSample)
                            break;

                        try
                        {
                            if (tr.GetObject(fid, OpenMode.ForRead) is PressurePart part)
                            {
                                var pos = part.Position;
                                var fam = part.PartFamilyName ?? string.Empty;
                                var desc = part.PartDescription ?? string.Empty;
                                ed.WriteMessage($"\n  [{sample}] Position=({pos.X:0.###},{pos.Y:0.###},{pos.Z:0.###}) Family={fam} Desc={desc}");
                                sample++;
                            }
                        }
                        catch
                        {
                            // ignore sample failures
                        }
                    }
                }

                if (count > maxSample)
                    ed.WriteMessage($"\n... ({count - maxSample} more not shown)");

                ed.WriteMessage("\n--- END FITTINGS DIAG ---");
                tr.Commit();
            }
        }

        public static void DiagnosePartsList(Editor ed)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var civilDoc = CivilApplication.ActiveDocument;
            if (civilDoc == null) return;

            using (var tr = doc.TransactionManager.StartTransaction())
            {
                if (!TryGetFirstPartsListId(civilDoc, tr, ed, out ObjectId partsListId))
                {
                    tr.Commit();
                    return;
                }

                var sizes = GetAllPipeNominalDiameters(partsListId, tr);
                ed.WriteMessage($"\n--- WSPro PARTS DIAG ---");
                ed.WriteMessage($"\nPartsListId: {partsListId}");
                if (TryOpenPressurePartList(partsListId, tr, out PressurePartList openedPartsList))
                {
                    ed.WriteMessage($"\nParts list runtime type: {openedPartsList.GetType().FullName}");
                    DumpPressurePartListMembers(openedPartsList, tr, ed);
                    DumpFirstPressurePartSize(openedPartsList, tr, ed);

                    var fitTypeCandidates = GetPressureFittingPartTypesToQuery(openedPartsList);
                    var fitTypeNames = fitTypeCandidates.Select(x => x.ToString()).OrderBy(x => x).Take(28).ToList();
                    ed.WriteMessage($"\nPressurePartType candidates for fittings: {fitTypeCandidates.Count}");
                    if (fitTypeNames.Count > 0)
                        ed.WriteMessage($"\n  Types: {string.Join(", ", fitTypeNames)}");

                    var fittingDiameters = new List<double>();
                    foreach (var fp in GetPressureFittingParts(openedPartsList))
                    {
                        if (TryGetPressurePartNominalDiameter(fp, out double fd))
                            fittingDiameters.Add(fd);
                    }

                    ed.WriteMessage($"\nFitting part sizes in catalog (API): {fittingDiameters.Count}");
                    if (fittingDiameters.Count > 0)
                    {
                        var uniq = fittingDiameters.Distinct().OrderBy(x => x).Take(30).Select(x => x.ToString("0.###"));
                        ed.WriteMessage($"\nFitting nominal diameters (sample): {string.Join(", ", uniq)}");
                    }
                    else
                    {
                        ed.WriteMessage("\nTip: If this is 0, add fitting families/sizes under Toolspace → Pressure Network → Parts Lists (Fittings tab), or pick a template whose catalog includes elbows/tees for your diameters.");
                    }
                }

                ed.WriteMessage($"\nPipe sizes found: {sizes.Count}");
                if (sizes.Count > 0)
                {
                    var ordered = sizes.OrderBy(x => x).ToList();
                    ed.WriteMessage($"\nMin/Max nominal diameter: {ordered.First():0.###} / {ordered.Last():0.###}");
                    ed.WriteMessage($"\nFirst 30 sizes: {string.Join(", ", ordered.Take(30).Select(x => x.ToString("0.###")))}");
                }

                ed.WriteMessage($"\n--- END PARTS DIAG ---");
                tr.Commit();
            }
        }

        private static List<double> GetAllPipeNominalDiameters(ObjectId partsListId, Transaction tr)
        {
            var result = new List<double>();
            if (partsListId.IsNull) return result;

            if (!TryOpenPressurePartList(partsListId, tr, out PressurePartList partsList))
                return result;

            foreach (var size in GetPressurePipeParts(partsList, tr))
            {
                if (TryGetPressurePartNominalDiameter(size, out double d))
                    result.Add(d);
            }

            return result;
        }

        private static void DumpPressurePartListMembers(PressurePartList partsList, Transaction tr, Editor ed)
        {
            if (partsList == null || ed == null)
                return;

            var type = partsList.GetType();
            ed.WriteMessage("\nParts-list member probe:");

            var props = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.GetIndexParameters().Length == 0 && LooksLikePartsMember(p.Name))
                .OrderBy(p => p.Name)
                .Take(20)
                .ToList();

            foreach (var prop in props)
            {
                object value = null;
                string note;
                try
                {
                    value = prop.GetValue(partsList, null);
                    note = DescribeProbeValue(value, tr);
                }
                catch (Exception ex)
                {
                    note = "<get failed: " + ex.GetType().Name + ">";
                }

                ed.WriteMessage($"\n  PROP {prop.Name} -> {note}");
            }

            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => !m.IsSpecialName &&
                            m.ReturnType != typeof(void) &&
                            LooksLikePartsMember(m.Name) &&
                            (m.GetParameters().Length == 0 ||
                             (m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum)))
                .OrderBy(m => m.Name)
                .Take(20)
                .ToList();

            foreach (var method in methods)
            {
                string note = "<no value>";
                foreach (var value in InvokeCandidatePartMethods(partsList, method))
                {
                    note = DescribeProbeValue(value, tr);
                    break;
                }

                ed.WriteMessage($"\n  METH {method.Name} -> {note}");
            }
        }

        private static void DumpFirstPressurePartSize(PressurePartList partsList, Transaction tr, Editor ed)
        {
            if (partsList == null || ed == null)
                return;

            var first = GetPressurePipeParts(partsList, tr).FirstOrDefault();
            if (first == null)
            {
                ed.WriteMessage("\nFirst part-size probe: <none>");
                return;
            }

            ed.WriteMessage("\nFirst part-size probe:");
            ed.WriteMessage($"\n  Type: {first.GetType().FullName}");

            if (TryGetPressurePartNominalDiameter(first, out double diameter))
                ed.WriteMessage($"\n  Resolved nominal diameter: {diameter:0.###}");
            else
                ed.WriteMessage("\n  Resolved nominal diameter: <unavailable>");

            var props = first.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.GetIndexParameters().Length == 0 &&
                            (p.Name.IndexOf("diam", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             p.Name.IndexOf("nom", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             p.Name.IndexOf("size", StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(p => p.Name)
                .Take(12)
                .ToList();

            foreach (var prop in props)
            {
                string note;
                try
                {
                    note = DescribeScalarValue(prop.GetValue(first, null));
                }
                catch (Exception ex)
                {
                    note = "<get failed: " + ex.GetType().Name + ">";
                }

                ed.WriteMessage($"\n  PROP {prop.Name} -> {note}");
            }

            DumpPressurePartContexts(first, ed);
        }

        private static string DescribeProbeValue(object value, Transaction tr)
        {
            if (value == null)
                return "<null>";

            if (value is PressurePartSize partSize)
            {
                return TryGetPressurePartNominalDiameter(partSize, out double d)
                    ? $"PressurePartSize dia={d:0.###}"
                    : "PressurePartSize";
            }

            if (TryOpenObjectFromUnknown(value, tr, out object opened))
            {
                return $"ObjectId->{opened.GetType().Name}";
            }

            if (value is IEnumerable enumerable && value is not string)
            {
                int count = 0;
                string first = null;
                foreach (var item in enumerable)
                {
                    count++;
                    if (first == null)
                        first = item?.GetType().Name ?? "<null>";
                    if (count >= 5)
                        break;
                }

                return $"Enumerable count>={count}" + (first != null ? $" first={first}" : "");
            }

            return value.GetType().Name;
        }

        private static bool TryGetFirstPartsListId(CivilDocument civilDoc, Transaction tr, Editor ed, out ObjectId partsListId)
        {
            partsListId = ObjectId.Null;

            // Strategy:
            // 1) Try to get parts list IDs via AeccPressurePipesMgd extension entry points.
            // 2) Fall back to scanning CivilDocument public properties.

            var pressureAsm = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "AeccPressurePipesMgd", StringComparison.OrdinalIgnoreCase));

            if (pressureAsm != null)
            {
                // Prefer the parts list actually assigned to an existing network in the drawing.
                // Some templates expose multiple styles-root parts lists and the first one may be empty.
                if (TryGetPartsListIdFromExistingPressureNetwork(pressureAsm, civilDoc, tr, ed, out partsListId))
                    return true;

                if (TryGetPartsListIdViaPressureExtension(pressureAsm, civilDoc, ed, out partsListId))
                    return true;
            }

            // Fallback: Civil 3D pressure pipe APIs differ by version; scan CivilDocument.
            var docType = civilDoc.GetType();
            var candidateProps = docType
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p =>
                    p.PropertyType != typeof(string) &&
                    p.Name.IndexOf("Pressure", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    p.Name.IndexOf("Parts", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    p.Name.IndexOf("List", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            foreach (var prop in candidateProps)
            {
                if (TryGetFirstObjectIdFromEnumerable(() =>
                    prop.GetValue(civilDoc, null) as System.Collections.IEnumerable, out partsListId))
                {
                    ed.WriteMessage($"\nUsing parts list from CivilDocument property '{prop.Name}'.");
                    return true;
                }
            }

            ed.WriteMessage("\nCould not find a Pressure Pipe Parts List in this drawing.");
            ed.WriteMessage("\nFix option A (recommended): create a tiny Pressure Network in this drawing (any 2-point pipe) and pick a Parts List when prompted. Then re-run WSPRO_IMPORT_PRESSURE.");
            ed.WriteMessage("\nFix option B: create/assign a Pressure Parts List in Toolspace > Settings tab > Pressure Networks > Parts Lists.");
            ed.WriteMessage("\nDebug note: AeccPressurePipesMgd was " + (pressureAsm == null ? "NOT loaded." : "loaded, but no parts list accessor matched."));
            return false;
        }

        private static bool TryGetPartsListIdFromExistingPressureNetwork(System.Reflection.Assembly pressureAsm, CivilDocument civilDoc, Transaction tr, Editor ed, out ObjectId partsListId)
        {
            partsListId = ObjectId.Null;

            Type docExt = pressureAsm.GetType("Autodesk.Civil.ApplicationServices.CivilDocumentPressurePipesExtension", throwOnError: false);
            if (docExt == null)
                return false;

            var getIds = docExt.GetMethod("GetPressurePipeNetworkIds", BindingFlags.Public | BindingFlags.Static);
            if (getIds == null)
                return false;

            object result;
            try
            {
                result = getIds.Invoke(null, new object[] { civilDoc });
            }
            catch
            {
                return false;
            }

            if (result is not System.Collections.IEnumerable enumerable)
                return false;

            foreach (var item in enumerable)
            {
                if (item is not ObjectId netId || netId.IsNull)
                    continue;

                try
                {
                    // Open network object via the current drawing transaction; reflect PartsListId-like property.
                    var dbObj = tr.GetObject(netId, OpenMode.ForRead);
                    if (dbObj == null) continue;

                    var netType = dbObj.GetType();
                    var prop = netType.GetProperty("PartsListId", BindingFlags.Instance | BindingFlags.Public)
                               ?? netType.GetProperty("PartListId", BindingFlags.Instance | BindingFlags.Public)
                               ?? netType.GetProperty("PartsList", BindingFlags.Instance | BindingFlags.Public);

                    if (prop == null)
                        continue;

                    var val = prop.GetValue(dbObj, null);
                    if (val is ObjectId oid && !oid.IsNull)
                    {
                        partsListId = oid;
                        ed.WriteMessage("\nUsing parts list from existing Pressure Network in drawing.");
                        return true;
                    }

                    // Sometimes it's a DBObject with ObjectId property.
                    if (val != null)
                    {
                        var idProp = val.GetType().GetProperty("ObjectId", BindingFlags.Instance | BindingFlags.Public);
                        if (idProp != null && idProp.PropertyType == typeof(ObjectId))
                        {
                            var oid2 = (ObjectId)idProp.GetValue(val, null);
                            if (!oid2.IsNull)
                            {
                                partsListId = oid2;
                                ed.WriteMessage("\nUsing parts list from existing Pressure Network in drawing.");
                                return true;
                            }
                        }
                    }
                }
                catch
                {
                    // Keep searching other networks.
                }
            }

            return false;
        }

        private static bool TryGetExistingPressureNetwork(CivilDocument civilDoc, Transaction tr, Editor ed, out ObjectId networkId)
        {
            networkId = ObjectId.Null;

            var pressureAsm = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "AeccPressurePipesMgd", StringComparison.OrdinalIgnoreCase));
            if (pressureAsm == null)
                return false;

            Type docExt = pressureAsm.GetType("Autodesk.Civil.ApplicationServices.CivilDocumentPressurePipesExtension", throwOnError: false);
            if (docExt == null)
                return false;

            var getIds = docExt.GetMethod("GetPressurePipeNetworkIds", BindingFlags.Public | BindingFlags.Static);
            if (getIds == null)
                return false;

            object result;
            try
            {
                result = getIds.Invoke(null, new object[] { civilDoc });
            }
            catch
            {
                return false;
            }

            if (result is not System.Collections.IEnumerable enumerable)
                return false;

            foreach (var item in enumerable)
            {
                if (item is not ObjectId netId || netId.IsNull)
                    continue;

                // Return the first valid network.
                networkId = netId;
                ed.WriteMessage("\nReusing existing Pressure Pipe Network in drawing.");
                return true;
            }

            return false;
        }

        private static bool TryGetPartsListIdViaPressureExtension(System.Reflection.Assembly pressureAsm, CivilDocument civilDoc, Editor ed, out ObjectId partsListId)
        {
            partsListId = ObjectId.Null;

            // Civil 3D 2026 shows CivilDocumentPressurePipesExtension methods for network IDs,
            // but parts lists are often exposed via StylesRoot pressure-pipe extensions.
            // We don't hardcode exact method names; we probe all Pressure*Extension types for:
            // - public static method
            // - single parameter compatible with CivilDocument OR StylesRoot OR other reachable roots
            // - name suggests "PartsList"/"PartList"
            // - return value yields ObjectIds when enumerated

            object stylesRoot = null;
            try { stylesRoot = civilDoc.Styles; } catch { /* ignore */ }

            var candidateArgs = new List<object> { civilDoc };
            if (stylesRoot != null)
                candidateArgs.Add(stylesRoot);

            var extTypes = pressureAsm
                .GetTypes()
                .Where(t => t.IsAbstract && t.IsSealed &&
                            t.FullName != null &&
                            t.FullName.IndexOf("Pressure", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            t.FullName.IndexOf("Extension", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            foreach (var extType in extTypes)
            {
                var methods = extType
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m =>
                    {
                        if (m.Name.IndexOf("Part", StringComparison.OrdinalIgnoreCase) < 0) return false;
                        if (m.Name.IndexOf("List", StringComparison.OrdinalIgnoreCase) < 0) return false;
                        var ps = m.GetParameters();
                        return ps.Length == 1;
                    })
                    .ToList();

                foreach (var m in methods)
                {
                    var ps = m.GetParameters();
                    foreach (var arg in candidateArgs)
                    {
                        if (!ps[0].ParameterType.IsInstanceOfType(arg))
                            continue;

                        try
                        {
                            var result = m.Invoke(null, new object[] { arg });
                            if (result == null)
                                continue;

                            if (result is System.Collections.IEnumerable enumerable)
                            {
                                foreach (var item in enumerable)
                                {
                                    if (item is ObjectId oid && !oid.IsNull)
                                    {
                                        partsListId = oid;
                                        ed.WriteMessage($"\nUsing parts list via {extType.Name}.{m.Name}({ps[0].ParameterType.Name}).");
                                        return true;
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // try next overload/arg
                        }
                    }
                }
            }

            return false;
        }

        private static bool TryGetFirstObjectIdFromEnumerable(Func<System.Collections.IEnumerable> getEnumerable, out ObjectId objectId)
        {
            objectId = ObjectId.Null;
            try
            {
                var enumerable = getEnumerable();
                if (enumerable == null)
                    return false;

                foreach (var item in enumerable)
                {
                    if (item is ObjectId oid && !oid.IsNull)
                    {
                        objectId = oid;
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static bool TryCreatePressureNetwork(CivilDocument civilDoc, string name, ObjectId partsListId, Editor ed, out ObjectId networkId)
        {
            networkId = ObjectId.Null;

            // We intentionally avoid hard-coding Autodesk.Civil pressure pipe types here
            // because the managed API surface differs by release.
            // Instead, locate a pressure network type and its Create method at runtime.
            var pressureAsm = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "AeccPressurePipesMgd", StringComparison.OrdinalIgnoreCase));

            if (pressureAsm == null)
            {
                ed.WriteMessage("\nAeccPressurePipesMgd.dll is not loaded. Ensure you are running inside Civil 3D (not plain AutoCAD).");
                return false;
            }

            var networkType = pressureAsm
                .GetTypes()
                .FirstOrDefault(t =>
                    t.IsClass &&
                    (t.Name.Equals("PressurePipeNetwork", StringComparison.OrdinalIgnoreCase) ||
                     t.Name.Equals("PressureNetwork", StringComparison.OrdinalIgnoreCase) ||
                     t.FullName?.IndexOf("PressurePipeNetwork", StringComparison.OrdinalIgnoreCase) >= 0));

            if (networkType == null)
            {
                ed.WriteMessage("\nCould not find a Pressure Pipe Network type in AeccPressurePipesMgd.");
                return false;
            }

            // Civil 3D pressure API overloads vary. Instead of guessing an exact signature,
            // attempt any Create(...) overload whose parameters can be satisfied from known inputs.
            object stylesRoot = null;
            try { stylesRoot = civilDoc.Styles; } catch { /* ignore */ }

            // Civil 3D 2026: PressurePipeNetwork.Create(Database, string)
            Autodesk.AutoCAD.DatabaseServices.Database acDb = null;
            try
            {
                acDb = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument?.Database;
            }
            catch
            {
                // ignore
            }

            var available = new List<object>
            {
                civilDoc,
                name,
                partsListId,
                ObjectId.Null
            };
            if (stylesRoot != null)
                available.Add(stylesRoot);
            if (acDb != null)
                available.Add(acDb);

            var createMethods = networkType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name.Equals("Create", StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.GetParameters().Length)
                .ToList();

            foreach (var m in createMethods)
            {
                if (TryBuildArgs(m.GetParameters(), available, out object[] args))
                {
                    try
                    {
                        var result = m.Invoke(null, args);
                        if (result is ObjectId oid && !oid.IsNull)
                        {
                            networkId = oid;
                            TryAssignPartsListToNetwork(networkId, partsListId, ed);
                            ed.WriteMessage($"\nCreated Pressure Pipe Network via {networkType.Name}.Create({FormatParamList(m)})");
                            return true;
                        }

                        // Some overloads might return a DBObject with ObjectId.
                        if (result != null)
                        {
                            var idProp = result.GetType().GetProperty("ObjectId", BindingFlags.Instance | BindingFlags.Public);
                            if (idProp != null && idProp.PropertyType == typeof(ObjectId))
                            {
                                var oid2 = (ObjectId)idProp.GetValue(result, null);
                                if (!oid2.IsNull)
                                {
                                    networkId = oid2;
                                    TryAssignPartsListToNetwork(networkId, partsListId, ed);
                                    ed.WriteMessage($"\nCreated Pressure Pipe Network via {networkType.Name}.Create({FormatParamList(m)})");
                                    return true;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Try next overload
                    }
                }
            }

            ed.WriteMessage("\nFailed to create a Pressure Pipe Network (no compatible Create overload found).");
            return false;
        }

        private static bool TryBuildArgs(ParameterInfo[] parameters, List<object> available, out object[] args)
        {
            args = Array.Empty<object>();
            var used = new bool[available.Count];
            var built = new object[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                var pType = parameters[i].ParameterType;
                int matchIdx = -1;

                // Prefer exact / assignable matches.
                for (int j = 0; j < available.Count; j++)
                {
                    if (used[j]) continue;
                    var v = available[j];
                    if (v == null) continue;

                    // exact string match
                    if (pType == typeof(string) && v is string)
                    {
                        matchIdx = j;
                        break;
                    }
                    // exact ObjectId match
                    if (pType == typeof(ObjectId) && v is ObjectId)
                    {
                        matchIdx = j;
                        break;
                    }
                    // general assignable
                    if (pType.IsInstanceOfType(v))
                    {
                        matchIdx = j;
                        break;
                    }
                }

                if (matchIdx < 0)
                    return false;

                used[matchIdx] = true;
                built[i] = available[matchIdx];
            }

            args = built;
            return true;
        }

        private static string FormatParamList(MethodInfo m)
        {
            try
            {
                return string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name));
            }
            catch
            {
                return "?";
            }
        }

        private static bool TryCreatePipe(ObjectId networkId, Transaction tr, PressurePartSize partSize, Point3d startPt, Point3d endPt, Editor ed, out ObjectId pipeId)
        {
            pipeId = ObjectId.Null;

            if (networkId.IsNull || partSize == null)
                return false;

            if (tr.GetObject(networkId, OpenMode.ForWrite) is not PressurePipeNetwork network)
                return false;

            try
            {
                pipeId = network.AddLinePipe(new LineSegment3d(startPt, endPt), partSize);
                return !pipeId.IsNull;
            }
            catch
            {
                // fall through to the reflection-based fallback for any version-specific API differences
            }

            object netObj = network;
            var methods = network.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.Name.Equals("AddLinePipe", StringComparison.OrdinalIgnoreCase) ||
                            m.Name.Equals("AddCurvePipe", StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.GetParameters().Length)
                .ToList();

            foreach (var m in methods)
            {
                var ps = m.GetParameters();
                try
                {
                    if (m.Name.Equals("AddLinePipe", StringComparison.OrdinalIgnoreCase) &&
                        ps.Length == 2 &&
                        ps[0].ParameterType == typeof(LineSegment3d) &&
                        ps[1].ParameterType == typeof(PressurePartSize))
                    {
                        var result = m.Invoke(netObj, new object[] { new LineSegment3d(startPt, endPt), partSize });
                        if (result is ObjectId oid && !oid.IsNull)
                        {
                            pipeId = oid;
                            return true;
                        }
                    }
                }
                catch
                {
                    // try next overload
                }
            }

            ed.WriteMessage("\nFailed to create pipe (no compatible AddLinePipe overload found).");
            return false;
        }

        // Civil 3D does not insert fittings when pipes are created only via API (AddLinePipe).
        // Fittings appear only after AddFitting(...) or from Pressure Plan Layout / other network tools.

        private const double DefaultJunctionFittingTolerance = 0.01;
        private const double StraightCouplingAngleDegreesMin = 165.0;

        private enum WsproJunctionFittingKind
        {
            Tee,
            Elbow,
            Coupling,
            Cap,
            Cross,
            Generic
        }

        private static void PostProcessJunctionFittings(
            ObjectId networkId,
            ObjectId partsListId,
            IReadOnlyDictionary<string, WsproNode> nodeDict,
            IReadOnlyList<WsproPipe> pipes,
            Transaction tr,
            Editor ed,
            IReadOnlyDictionary<string, int> nodeCreatedPipeCount)
        {
            if (networkId.IsNull || partsListId.IsNull || tr == null || ed == null)
                return;

            if (tr.GetObject(networkId, OpenMode.ForRead) is not PressurePipeNetwork network)
                return;

            int fittingsAdded = 0;
            int junctionsSkippedHasFitting = 0;
            int junctionsSkippedLowDegree = 0;
            int junctionsSkippedNoCatalog = 0;
            int junctionsSkippedAddFailed = 0;
            var noCatalogReasons = new Dictionary<string, int>(StringComparer.Ordinal);
            var addFailedReasons = new Dictionary<string, int>(StringComparer.Ordinal);

            var existingFittingPoints = CollectFittingPositions(network, tr);
            var junctions = WsproJunctionInfo.BuildFrom(pipes, nodeDict);

            foreach (var j in junctions)
            {
                if (j.Degree < 2)
                {
                    junctionsSkippedLowDegree++;
                    continue;
                }

                if (nodeCreatedPipeCount.GetValueOrDefault(j.NodeId) < 2)
                {
                    junctionsSkippedLowDegree++;
                    continue;
                }

                if (PointWithinAnyFitting(j.Position, existingFittingPoints, DefaultJunctionFittingTolerance))
                {
                    junctionsSkippedHasFitting++;
                    continue;
                }

                var kind = ResolveJunctionFittingKind(j);
                var diameterMm = SelectPrimaryDiameterMm(j.DiametersMm);
                if (diameterMm <= 0.0)
                {
                    junctionsSkippedNoCatalog++;
                    BumpReason(noCatalogReasons, $"invalid diameter @ node {j.NodeId}");
                    continue;
                }

                if (!TryResolveFittingPartSize(partsListId, tr, diameterMm, kind, out PressurePartSize fittingSize, out WsproJunctionFittingKind resolvedKind))
                {
                    junctionsSkippedNoCatalog++;
                    BumpReason(noCatalogReasons, $"{kind} {diameterMm:0} mm (no catalog match)");
                    continue;
                }

                if (!TryCreateFitting(networkId, tr, fittingSize, j.Position, out ObjectId newFittingId) ||
                    newFittingId.IsNull)
                {
                    junctionsSkippedAddFailed++;
                    BumpReason(addFailedReasons, $"{resolvedKind} {diameterMm:0} mm");
                    continue;
                }

                fittingsAdded++;
                existingFittingPoints.Add(j.Position);
            }

            ed.WriteMessage($"\nPost-process fittings: added {fittingsAdded}.");
            if (junctionsSkippedHasFitting > 0)
                ed.WriteMessage($"\n  Skipped {junctionsSkippedHasFitting} junction(s) (fitting already near node).");
            if (junctionsSkippedLowDegree > 0)
                ed.WriteMessage($"\n  Skipped {junctionsSkippedLowDegree} junction(s) (need degree>=2 and at least 2 pipes created at node).");
            if (junctionsSkippedNoCatalog > 0)
            {
                ed.WriteMessage($"\n  Skipped {junctionsSkippedNoCatalog} junction(s) (no matching fitting part size).");
                DumpReasonSamples(ed, "    No catalog examples", noCatalogReasons, 10);
            }
            if (junctionsSkippedAddFailed > 0)
            {
                ed.WriteMessage($"\n  Skipped {junctionsSkippedAddFailed} junction(s) (AddFitting failed).");
                DumpReasonSamples(ed, "    AddFitting failed examples", addFailedReasons, 10);
            }
        }

        private static void BumpReason(Dictionary<string, int> dict, string key)
        {
            if (dict == null || string.IsNullOrEmpty(key))
                return;
            dict[key] = dict.GetValueOrDefault(key) + 1;
        }

        private static void DumpReasonSamples(Editor ed, string title, Dictionary<string, int> reasons, int maxLines)
        {
            if (ed == null || reasons == null || reasons.Count == 0)
                return;
            ed.WriteMessage($"\n{title}:");
            foreach (var kv in reasons.OrderByDescending(x => x.Value).Take(maxLines))
                ed.WriteMessage($"\n      [{kv.Value}x] {kv.Key}");
        }

        /// <summary>
        /// Prefer part matching resolved junction kind; then try common kinds; then any fitting at nominal diameter.
        /// </summary>
        private static bool TryResolveFittingPartSize(
            ObjectId partsListId,
            Transaction tr,
            double diameterMm,
            WsproJunctionFittingKind primaryKind,
            out PressurePartSize partSize,
            out WsproJunctionFittingKind resolvedKind)
        {
            partSize = null;
            resolvedKind = primaryKind;

            var tryKinds = new List<WsproJunctionFittingKind> { primaryKind };
            foreach (var k in new[]
                     {
                         WsproJunctionFittingKind.Tee,
                         WsproJunctionFittingKind.Coupling,
                         WsproJunctionFittingKind.Elbow,
                         WsproJunctionFittingKind.Cross,
                         WsproJunctionFittingKind.Cap
                     })
            {
                if (!tryKinds.Contains(k))
                    tryKinds.Add(k);
            }

            foreach (var k in tryKinds)
            {
                if (TryFindFittingPartSize(partsListId, tr, diameterMm, k, out partSize))
                {
                    resolvedKind = k;
                    return true;
                }
            }

            if (TryFindAnyFittingPartSizeAtDiameter(partsListId, tr, diameterMm, out partSize))
            {
                resolvedKind = WsproJunctionFittingKind.Generic;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Diameter-only match among catalog fitting parts (no keyword), slightly looser tolerance than kind-specific search.
        /// </summary>
        private static bool TryFindAnyFittingPartSizeAtDiameter(
            ObjectId partsListId,
            Transaction tr,
            double diameterMm,
            out PressurePartSize partSize)
        {
            partSize = null;
            if (partsListId.IsNull || diameterMm <= 0.0)
                return false;

            if (!TryOpenPressurePartList(partsListId, tr, out PressurePartList partsList))
                return false;

            var targets = new[]
            {
                diameterMm,
                diameterMm / 25.4,
                diameterMm / 1000.0
            };

            const double relativeTol = 0.30;
            double bestRel = double.MaxValue;
            PressurePartSize bestPart = null;

            foreach (var candidate in GetPressureFittingParts(partsList))
            {
                if (!TryBestRelativeErrorForFittingMm(candidate, diameterMm, targets, out double rel))
                    continue;

                if (rel < bestRel)
                {
                    bestRel = rel;
                    bestPart = candidate;
                }
            }

            if (bestPart != null && bestRel <= relativeTol)
            {
                partSize = bestPart;
                return true;
            }

            return false;
        }

        private static List<Point3d> CollectFittingPositions(PressurePipeNetwork network, Transaction tr)
        {
            var list = new List<Point3d>();
            if (network == null || tr == null)
                return list;

            object fittingIdsObj = null;
            try
            {
                fittingIdsObj = network.GetFittingIds();
            }
            catch
            {
                return list;
            }

            if (fittingIdsObj is not IEnumerable fittingIds)
                return list;

            foreach (ObjectId fid in fittingIds)
            {
                if (fid.IsNull)
                    continue;
                try
                {
                    if (tr.GetObject(fid, OpenMode.ForRead) is PressurePart part)
                        list.Add(part.Position);
                }
                catch
                {
                    // ignore
                }
            }

            return list;
        }

        private static bool PointWithinAnyFitting(Point3d p, IReadOnlyList<Point3d> fittingPoints, double tolerance)
        {
            if (fittingPoints == null || fittingPoints.Count == 0)
                return false;

            foreach (var q in fittingPoints)
            {
                if (p.DistanceTo(q) <= tolerance)
                    return true;
            }

            return false;
        }

        private static double SelectPrimaryDiameterMm(IReadOnlyList<double> diametersMm)
        {
            if (diametersMm == null || diametersMm.Count == 0)
                return 0.0;

            return diametersMm
                .GroupBy(d => d)
                .OrderByDescending(g => g.Count())
                .First()
                .Key;
        }

        private static WsproJunctionFittingKind ResolveJunctionFittingKind(WsproJunctionInfo j)
        {
            var t = j.NodeType ?? string.Empty;
            if (t.IndexOf("tee", StringComparison.OrdinalIgnoreCase) >= 0)
                return WsproJunctionFittingKind.Tee;
            if (t.IndexOf("cross", StringComparison.OrdinalIgnoreCase) >= 0)
                return WsproJunctionFittingKind.Cross;
            if (t.IndexOf("elbow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("bend", StringComparison.OrdinalIgnoreCase) >= 0)
                return WsproJunctionFittingKind.Elbow;
            if (t.IndexOf("cap", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("plug", StringComparison.OrdinalIgnoreCase) >= 0)
                return WsproJunctionFittingKind.Cap;
            if (t.IndexOf("coupl", StringComparison.OrdinalIgnoreCase) >= 0)
                return WsproJunctionFittingKind.Coupling;

            if (j.Degree >= 3)
                return WsproJunctionFittingKind.Tee;

            if (j.Degree == 2 && j.TurnAngleBetweenArmsDegrees.HasValue)
            {
                var ang = j.TurnAngleBetweenArmsDegrees.Value;
                if (ang >= StraightCouplingAngleDegreesMin)
                    return WsproJunctionFittingKind.Coupling;
                return WsproJunctionFittingKind.Elbow;
            }

            return WsproJunctionFittingKind.Generic;
        }

        private static bool TryFindFittingPartSize(
            ObjectId partsListId,
            Transaction tr,
            double diameterMm,
            WsproJunctionFittingKind kind,
            out PressurePartSize partSize)
        {
            partSize = null;
            if (partsListId.IsNull || diameterMm <= 0.0)
                return false;

            if (!TryOpenPressurePartList(partsListId, tr, out PressurePartList partsList))
                return false;

            var targets = new[]
            {
                diameterMm,
                diameterMm / 25.4,
                diameterMm / 1000.0
            };

            // Looser than pipe matching (catalog / API units vary more for fittings).
            const double relativeTol = 0.22;
            var candidates = new List<(PressurePartSize Part, double RelErr, int KeywordScore)>();

            foreach (var candidate in GetPressureFittingParts(partsList))
            {
                if (!TryBestRelativeErrorForFittingMm(candidate, diameterMm, targets, out double bestRel))
                    continue;

                if (bestRel > relativeTol)
                    continue;

                var text = GetPressurePartSizeSearchText(candidate);
                int score = ScoreFittingKeywordMatch(text, kind);
                candidates.Add((candidate, bestRel, score));
            }

            if (candidates.Count == 0)
                return false;

            var ordered = candidates
                .OrderBy(x => x.RelErr)
                .ThenByDescending(x => x.KeywordScore)
                .ToList();

            var bestScore = ordered[0].KeywordScore;
            if (bestScore > 0)
            {
                partSize = ordered.First(x => x.KeywordScore == bestScore).Part;
                return partSize != null;
            }

            partSize = ordered[0].Part;
            return partSize != null;
        }

        private static int ScoreFittingKeywordMatch(string text, WsproJunctionFittingKind kind)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            text = text.ToLowerInvariant();
            string[] keys = kind switch
            {
                WsproJunctionFittingKind.Tee => new[] { "tee", "tee ", " wye", "wye", "branch", "3-way", "3 way", "triple" },
                WsproJunctionFittingKind.Cross => new[] { "cross", "crossing" },
                WsproJunctionFittingKind.Elbow => new[] { "elbow", "bend", "deflection", "90", "45", "22" },
                WsproJunctionFittingKind.Coupling => new[] { "coupling", "coupler", "socket", "connector" },
                WsproJunctionFittingKind.Cap => new[] { "cap", "plug", "end cap" },
                WsproJunctionFittingKind.Generic => Array.Empty<string>(),
                _ => Array.Empty<string>()
            };

            int score = 0;
            foreach (var k in keys)
            {
                if (text.IndexOf(k, StringComparison.Ordinal) >= 0)
                    score += 2;
            }

            return score;
        }

        private static string GetPressurePartSizeSearchText(PressurePartSize size)
        {
            if (size == null)
                return string.Empty;

            var parts = new List<string>();
            try
            {
                parts.Add(size.ToString());
            }
            catch
            {
                // ignore
            }

            foreach (var prop in size.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (prop.GetIndexParameters().Length != 0)
                    continue;
                if (prop.PropertyType != typeof(string))
                    continue;
                if (prop.Name.IndexOf("name", StringComparison.OrdinalIgnoreCase) < 0 &&
                    prop.Name.IndexOf("desc", StringComparison.OrdinalIgnoreCase) < 0 &&
                    prop.Name.IndexOf("family", StringComparison.OrdinalIgnoreCase) < 0 &&
                    prop.Name.IndexOf("part", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                try
                {
                    var v = prop.GetValue(size, null) as string;
                    if (!string.IsNullOrWhiteSpace(v))
                        parts.Add(v);
                }
                catch
                {
                    // ignored
                }
            }

            return string.Join(" ", parts);
        }

        /// <summary>
        /// Line pipe only — never used as junction fitting via AddFitting.
        /// </summary>
        private static bool IsExcludedFromJunctionFittingsPartsList(PressurePartType v)
        {
            var s = v.ToString();
            if (string.Equals(s, "PressurePipe", StringComparison.OrdinalIgnoreCase))
                return true;
            if (s.IndexOf("Appurtenance", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return false;
        }

        /// <summary>
        /// Enum name contains "Fitting" (e.g. PressurePipeFitting). Excludes pipe and appurtenances.
        /// </summary>
        private static bool IsPressureFittingPartType(PressurePartType v)
        {
            if (IsExcludedFromJunctionFittingsPartsList(v))
                return false;
            return v.ToString().IndexOf("Fitting", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Civil often names types "Elbow", "Tee", "Coupling" with no "Fitting" substring — still junction parts.
        /// </summary>
        private static bool IsLikelyFittingPartSubtypeByEnumName(PressurePartType v)
        {
            if (IsExcludedFromJunctionFittingsPartsList(v))
                return false;

            var s = v.ToString();
            string[] keys =
            {
                "Elbow", "Tee", "Cross", "Coupling", "Coupler", "Bend", "Cap", "Wye",
                "Reducer", "Flange", "Adapter", "Branch", "Union", "Socket", "Lateral"
            };

            foreach (var k in keys)
            {
                if (s.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static bool TryCoerceToPressurePartType(object o, out PressurePartType pt)
        {
            pt = default;
            if (o == null)
                return false;

            if (o is PressurePartType direct)
            {
                pt = direct;
                return true;
            }

            try
            {
                if (o.GetType() == typeof(PressurePartType))
                {
                    pt = (PressurePartType)o;
                    return true;
                }
            }
            catch
            {
                // ignored
            }

            try
            {
                switch (o)
                {
                    case int i when Enum.IsDefined(typeof(PressurePartType), i):
                        pt = (PressurePartType)i;
                        return true;
                    case long l when l >= int.MinValue && l <= int.MaxValue && Enum.IsDefined(typeof(PressurePartType), (int)l):
                        pt = (PressurePartType)(int)l;
                        return true;
                }
            }
            catch
            {
                // ignored
            }

            return Enum.TryParse(o.ToString(), true, out pt);
        }

        /// <summary>
        /// Catalog may advertise part types via GetPartTypes() rather than every enum value.
        /// </summary>
        private static List<PressurePartType> GetPartTypesFromCatalog(PressurePartList partsList)
        {
            var list = new List<PressurePartType>();
            if (partsList == null)
                return list;

            try
            {
                var m = partsList.GetType().GetMethod("GetPartTypes", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                if (m == null)
                    return list;
                var result = m.Invoke(partsList, null);
                if (result is not IEnumerable enumerable)
                    return list;
                foreach (var o in enumerable)
                {
                    if (TryCoerceToPressurePartType(o, out var pt))
                        list.Add(pt);
                }
            }
            catch
            {
                // ignored
            }

            return list;
        }

        /// <summary>
        /// All <see cref="PressurePartType"/> values to query for <see cref="PressurePartSize"/> rows used with <c>AddFitting</c>.
        /// </summary>
        private static HashSet<PressurePartType> GetPressureFittingPartTypesToQuery(PressurePartList partsList)
        {
            var typesToQuery = new HashSet<PressurePartType>();

            foreach (var v in GetPartTypesFromCatalog(partsList))
            {
                if (!IsExcludedFromJunctionFittingsPartsList(v))
                    typesToQuery.Add(v);
            }

            foreach (PressurePartType v in Enum.GetValues(typeof(PressurePartType)))
            {
                if (IsPressureFittingPartType(v) || IsLikelyFittingPartSubtypeByEnumName(v))
                    typesToQuery.Add(v);
            }

            return typesToQuery;
        }

        private static IEnumerable<PressurePartSize> GetPressureFittingParts(PressurePartList partsList)
        {
            if (partsList == null)
                yield break;

            var seen = new HashSet<int>();

            foreach (var v in GetPressureFittingPartTypesToQuery(partsList))
            {
                List<PressurePartSize> parts = null;
                try
                {
                    parts = partsList.GetParts(v);
                }
                catch
                {
                    continue;
                }

                if (parts == null)
                    continue;

                foreach (var part in parts)
                {
                    if (TryRememberPressurePartSize(part, seen))
                        yield return part;
                }
            }
        }

        /// <summary>
        /// Hypotheses for nominal size from API (mm, m, inches, or raw).
        /// </summary>
        private static IEnumerable<double> NominalMmHypotheses(double raw)
        {
            if (raw <= 0)
                yield break;

            yield return raw;
            if (raw >= 0.02 && raw < 5.0)
                yield return raw * 1000.0;
            if (raw >= 3.0 && raw <= 80.0)
                yield return raw * 25.4;
        }

        /// <summary>
        /// Parse catalog strings like "300 mm", "DN300", "300mm x 300mm".
        /// </summary>
        private static bool TryParseMmFromCatalogText(string text, out double mm)
        {
            mm = 0.0;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var t = text.Trim();
            if (Regex.IsMatch(t, @"dn\s*\d", RegexOptions.IgnoreCase))
            {
                var m = Regex.Match(t, @"dn\s*(\d{2,4})\b", RegexOptions.IgnoreCase);
                if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out mm))
                    return mm >= 15 && mm <= 5000;
            }

            var mmMatch = Regex.Match(t, @"(\d{2,4}(?:\.\d+)?)\s*mm\b", RegexOptions.IgnoreCase);
            if (mmMatch.Success && double.TryParse(mmMatch.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out mm))
                return mm >= 15 && mm <= 5000;

            var xMatch = Regex.Match(t, @"(\d{2,4})\s*[xX]\s*(\d{2,4})\s*mm", RegexOptions.IgnoreCase);
            if (xMatch.Success)
            {
                if (double.TryParse(xMatch.Groups[1].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var a) &&
                    double.TryParse(xMatch.Groups[2].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var b))
                {
                    mm = Math.Max(a, b);
                    return mm >= 15 && mm <= 5000;
                }
            }

            return false;
        }

        private static bool TryBestRelativeErrorForFittingMm(
            PressurePartSize candidate,
            double diameterMm,
            double[] targets,
            out double bestRel)
        {
            bestRel = double.MaxValue;
            if (candidate == null || targets == null)
                return false;

            var text = GetPressurePartSizeSearchText(candidate);
            bool any = false;

            if (TryGetPressurePartNominalDiameter(candidate, out double raw) && raw > 0)
            {
                foreach (var candMm in NominalMmHypotheses(raw))
                {
                    foreach (var t in targets)
                    {
                        if (t <= 0)
                            continue;
                        double rel = Math.Abs(candMm - t) / t;
                        if (rel < bestRel)
                            bestRel = rel;
                        any = true;
                    }
                }
            }

            if (TryParseMmFromCatalogText(text, out double parsedMm))
            {
                foreach (var t in targets)
                {
                    if (t <= 0)
                        continue;
                    double rel = Math.Abs(parsedMm - t) / t;
                    if (rel < bestRel)
                        bestRel = rel;
                    any = true;
                }
            }

            return any && bestRel < double.MaxValue;
        }

        private static bool TryCreateFitting(ObjectId networkId, Transaction tr, PressurePartSize partSize, Point3d location, out ObjectId fittingId)
        {
            fittingId = ObjectId.Null;
            if (networkId.IsNull || partSize == null)
                return false;

            if (tr.GetObject(networkId, OpenMode.ForWrite) is not PressurePipeNetwork network)
                return false;

            try
            {
                fittingId = network.AddFitting(location, partSize);
                return !fittingId.IsNull;
            }
            catch
            {
                // fall through to reflection fallback
            }

            var methods = network.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.Name.Equals("AddFitting", StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.GetParameters().Length)
                .ToList();

            foreach (var m in methods)
            {
                var ps = m.GetParameters();
                try
                {
                    if (ps.Length == 2 &&
                        ps[0].ParameterType == typeof(Point3d) &&
                        ps[1].ParameterType == typeof(PressurePartSize))
                    {
                        var result = m.Invoke(network, new object[] { location, partSize });
                        if (result is ObjectId oid && !oid.IsNull)
                        {
                            fittingId = oid;
                            return true;
                        }
                    }
                }
                catch
                {
                    // try next overload
                }
            }

            return false;
        }

        private static bool TryFindPipePartSize(ObjectId partsListId, Transaction tr, double diameterMm, out PressurePartSize partSize)
        {
            partSize = null;
            if (partsListId.IsNull || diameterMm <= 0.0)
                return false;

            if (!TryOpenPressurePartList(partsListId, tr, out PressurePartList partsList))
                return false;

            // Civil 3D PartSize.NominalDiameter units are not always obvious (depends on drawing units / content).
            // We try matching against multiple interpretations of the CSV DIAMETER (which is in mm from WSPro):
            // - mm
            // - inches (mm / 25.4)
            // - meters (mm / 1000)
            // and we pick the closest within a relative tolerance.
            var targets = new[]
            {
                diameterMm,
                diameterMm / 25.4,
                diameterMm / 1000.0
            };

            const double relativeTol = 0.07; // 7% generally works with nominal sizes
            double bestRel = double.MaxValue;
            PressurePartSize bestPart = null;

            foreach (var candidate in GetPressurePipeParts(partsList, tr))
            {
                if (!TryGetPressurePartNominalDiameter(candidate, out double nominalDiameter))
                    continue;

                // Find best match across all candidate unit interpretations.
                foreach (var t in targets)
                {
                    if (t <= 0) continue;
                    double rel = Math.Abs(nominalDiameter - t) / t;
                    if (rel < bestRel)
                    {
                        bestRel = rel;
                        bestPart = candidate;
                    }
                }
            }

            if (bestPart != null && bestRel <= relativeTol)
            {
                partSize = bestPart;
                return true;
            }

            return false;
        }

        private static bool TryOpenPressurePartList(ObjectId partsListId, Transaction tr, out PressurePartList partsList)
        {
            partsList = null;
            if (partsListId.IsNull)
                return false;

            partsList = tr.GetObject(partsListId, OpenMode.ForRead) as PressurePartList;
            return partsList != null;
        }

        private static IEnumerable<PressurePartSize> GetPressurePipeParts(PressurePartList partsList, Transaction tr)
        {
            if (partsList == null)
                yield break;

            var seen = new HashSet<int>();

            List<PressurePartSize> parts = null;
            try
            {
                parts = partsList.GetParts(PressurePartType.PressurePipe);
            }
            catch
            {
                // Some Civil 3D builds expose pressure parts through families/ids instead.
            }

            if (parts != null)
            {
                foreach (var part in parts)
                {
                    if (TryRememberPressurePartSize(part, seen))
                        yield return part;
                }
            }

            foreach (var part in EnumeratePressurePartSizes(partsList, tr, depth: 0, seen))
            {
                yield return part;
            }
        }

        private static IEnumerable<PressurePartSize> EnumeratePressurePartSizes(object source, Transaction tr, int depth, HashSet<int> seen)
        {
            if (source == null || depth > 4)
                yield break;

            if (source is PressurePartSize directPart)
            {
                if (TryRememberPressurePartSize(directPart, seen))
                    yield return directPart;
                yield break;
            }

            if (TryOpenObjectFromUnknown(source, tr, out object opened))
            {
                foreach (var part in EnumeratePressurePartSizes(opened, tr, depth + 1, seen))
                    yield return part;
                yield break;
            }

            if (source is IEnumerable enumerable && source is not string)
            {
                foreach (var item in enumerable)
                {
                    foreach (var part in EnumeratePressurePartSizes(item, tr, depth + 1, seen))
                        yield return part;
                }
            }

            var type = source.GetType();
            foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (prop.GetIndexParameters().Length != 0)
                    continue;
                if (!LooksLikePartsMember(prop.Name))
                    continue;

                object value = null;
                try
                {
                    value = prop.GetValue(source, null);
                }
                catch
                {
                    continue;
                }

                foreach (var part in EnumeratePressurePartSizes(value, tr, depth + 1, seen))
                    yield return part;
            }

            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (method.IsSpecialName)
                    continue;
                if (method.ReturnType == typeof(void))
                    continue;
                if (!LooksLikePartsMember(method.Name))
                    continue;

                foreach (var value in InvokeCandidatePartMethods(source, method))
                {
                    foreach (var part in EnumeratePressurePartSizes(value, tr, depth + 1, seen))
                        yield return part;
                }
            }
        }

        private static bool LooksLikePartsMember(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            return name.IndexOf("Part", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Size", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Family", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Pipe", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TryOpenObjectFromUnknown(object value, Transaction tr, out object opened)
        {
            opened = null;
            if (value == null || tr == null)
                return false;

            if (value is ObjectId oid && !oid.IsNull && oid.IsValid)
            {
                try
                {
                    opened = tr.GetObject(oid, OpenMode.ForRead);
                    return opened != null;
                }
                catch
                {
                    return false;
                }
            }

            try
            {
                var idProp = value.GetType().GetProperty("ObjectId", BindingFlags.Instance | BindingFlags.Public);
                if (idProp != null && idProp.PropertyType == typeof(ObjectId))
                {
                    var nestedId = (ObjectId)idProp.GetValue(value, null);
                    if (!nestedId.IsNull && nestedId.IsValid)
                    {
                        opened = tr.GetObject(nestedId, OpenMode.ForRead);
                        return opened != null;
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static IEnumerable<object> InvokeCandidatePartMethods(object source, MethodInfo method)
        {
            if (source == null || method == null)
                yield break;

            var parameters = method.GetParameters();
            if (parameters.Length == 0)
            {
                object value = null;
                try
                {
                    value = method.Invoke(source, null);
                }
                catch
                {
                    yield break;
                }

                if (value != null)
                    yield return value;
                yield break;
            }

            if (parameters.Length != 1 || !parameters[0].ParameterType.IsEnum)
                yield break;

            foreach (var arg in GetEnumCandidates(parameters[0].ParameterType))
            {
                object value = null;
                try
                {
                    value = method.Invoke(source, new[] { arg });
                }
                catch
                {
                    continue;
                }

                if (value != null)
                    yield return value;
            }
        }

        private static IEnumerable<object> GetEnumCandidates(Type enumType)
        {
            Array values;
            try
            {
                values = Enum.GetValues(enumType);
            }
            catch
            {
                yield break;
            }

            var preferred = values.Cast<object>()
                .Where(v => string.Equals(v.ToString(), "PressurePipe", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(v.ToString(), "Pipe", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (preferred.Count > 0)
            {
                foreach (var value in preferred)
                    yield return value;
                yield break;
            }

            foreach (var value in values)
                yield return value;
        }

        private static bool TryRememberPressurePartSize(PressurePartSize part, HashSet<int> seen)
        {
            if (part == null)
                return false;

            int key = RuntimeHelpers.GetHashCode(part);
            return seen.Add(key);
        }

        private static bool TryGetPressurePartNominalDiameter(PressurePartSize size, out double diameter)
        {
            diameter = 0.0;
            if (size == null)
                return false;

            try
            {
                var value = size.GetProperty(PressurePartContextType.DiameterNominal);
                if (value != null && TryToDouble(value, out diameter))
                    return true;
            }
            catch
            {
                // ignore and fall back below
            }

            try
            {
                var prop = size.GetType().GetProperty("NominalDiameter", BindingFlags.Instance | BindingFlags.Public);
                if (prop != null)
                {
                    var value = prop.GetValue(size, null);
                    if (value != null && TryToDouble(value, out diameter))
                        return true;
                }
            }
            catch
            {
                // ignored
            }

            foreach (var candidate in GetPressurePartContextCandidates(size))
            {
                if (TryExtractDoubleRecursive(candidate.Value, out diameter, depth: 0))
                    return true;
            }

            var candidateMembers = size.GetType()
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.GetIndexParameters().Length == 0 &&
                            (p.Name.IndexOf("diam", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             p.Name.IndexOf("nom", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             p.Name.IndexOf("size", StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(p => ScoreDiameterCandidateName(p.Name))
                .ToList();

            foreach (var prop in candidateMembers)
            {
                try
                {
                    var value = prop.GetValue(size, null);
                    if (TryExtractDoubleRecursive(value, out diameter, depth: 0))
                        return true;
                }
                catch
                {
                    // ignored
                }
            }

            return false;
        }

        private static void DumpPressurePartContexts(PressurePartSize size, Editor ed)
        {
            if (size == null || ed == null)
                return;

            var contexts = GetPressurePartContextCandidates(size).Take(12).ToList();
            if (contexts.Count == 0)
            {
                ed.WriteMessage("\n  Context probe: <no readable context values>");
                return;
            }

            ed.WriteMessage("\n  Context probe:");
            foreach (var ctx in contexts)
            {
                ed.WriteMessage($"\n    {ctx.Name} -> {DescribeScalarValue(ctx.Value)}");
            }
        }

        private static IEnumerable<(string Name, object Value)> GetPressurePartContextCandidates(PressurePartSize size)
        {
            if (size == null)
                yield break;

            Array values;
            try
            {
                values = Enum.GetValues(typeof(PressurePartContextType));
            }
            catch
            {
                yield break;
            }

            foreach (PressurePartContextType ctx in values)
            {
                object value = null;
                try
                {
                    value = size.GetProperty(ctx);
                }
                catch
                {
                    continue;
                }

                if (value == null)
                    continue;

                string name = ctx.ToString();
                if (name.IndexOf("diam", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("nom", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("size", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("radius", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    yield return (name, value);
                }
            }
        }

        private static void TryAssignPartsListToNetwork(ObjectId networkId, ObjectId partsListId, Editor ed)
        {
            if (networkId.IsNull || partsListId.IsNull)
                return;

            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return;

            try
            {
                using (var tr = doc.TransactionManager.StartTransaction())
                {
                    if (tr.GetObject(networkId, OpenMode.ForWrite) is PressurePipeNetwork network)
                    {
                        network.PartsListId = partsListId;
                        tr.Commit();
                        return;
                    }

                    tr.Abort();
                }
            }
            catch
            {
                try
                {
                    using (var tr = doc.TransactionManager.StartTransaction())
                    {
                        var netObj = tr.GetObject(networkId, OpenMode.ForWrite);
                        var prop = netObj?.GetType().GetProperty("PartsListId", BindingFlags.Instance | BindingFlags.Public)
                                   ?? netObj?.GetType().GetProperty("CurrentPartListId", BindingFlags.Instance | BindingFlags.Public);

                        if (prop != null && prop.PropertyType == typeof(ObjectId))
                        {
                            prop.SetValue(netObj, partsListId, null);
                            tr.Commit();
                            return;
                        }

                        tr.Abort();
                    }
                }
                catch
                {
                    ed.WriteMessage("\nCreated network, but failed to assign the selected pressure parts list.");
                }
            }
        }

        private static bool TryToDouble(object value, out double result)
        {
            result = 0.0;
            try
            {
                if (value is double d)
                {
                    result = d;
                    return true;
                }

                if (value is float f)
                {
                    result = f;
                    return true;
                }

                if (value is int i)
                {
                    result = i;
                    return true;
                }

                if (value is long l)
                {
                    result = l;
                    return true;
                }

                if (value is decimal dec)
                {
                    result = (double)dec;
                    return true;
                }

                if (double.TryParse(value.ToString(), out var parsed))
                {
                    result = parsed;
                    return true;
                }
            }
            catch
            {
                // ignored
            }

            return false;
        }

        private static bool TryExtractDoubleRecursive(object value, out double result, int depth)
        {
            result = 0.0;
            if (value == null || depth > 3)
                return false;

            if (TryToDouble(value, out result))
                return true;

            var type = value.GetType();
            foreach (var name in new[] { "Value", "DoubleValue", "NominalDiameter", "Diameter", "Size", "InnerValue" })
            {
                try
                {
                    var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                    if (prop == null || prop.GetIndexParameters().Length != 0)
                        continue;

                    var nested = prop.GetValue(value, null);
                    if (TryExtractDoubleRecursive(nested, out result, depth + 1))
                        return true;
                }
                catch
                {
                    // ignored
                }
            }

            return false;
        }

        private static int ScoreDiameterCandidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return int.MaxValue;

            string n = name.ToLowerInvariant();
            if (n.Contains("nominal") && n.Contains("diam"))
                return 0;
            if (n.Contains("inner") && n.Contains("diam"))
                return 1;
            if (n.Contains("diam"))
                return 2;
            if (n.Contains("size"))
                return 3;
            return 4;
        }

        private static string DescribeScalarValue(object value)
        {
            if (value == null)
                return "<null>";

            if (TryExtractDoubleRecursive(value, out double d, depth: 0))
                return $"{value.GetType().Name} => {d:0.###}";

            return value.GetType().Name + " => " + value;
        }

        private static bool TryCountEnumerable(object obj, out int count)
        {
            count = 0;
            if (obj is string)
                return false;

            if (obj is System.Collections.IEnumerable enumerable)
            {
                try
                {
                    foreach (var _ in enumerable)
                        count++;
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }
    }
}

                        