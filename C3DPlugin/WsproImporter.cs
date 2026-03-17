using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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

