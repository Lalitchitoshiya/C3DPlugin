#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
            List<WsproCsvRecord> fullRecords = null;

            try
            {
                nodes = WsproCsvReader.ReadNodes(nodesCsv);
                pipes = WsproCsvReader.ReadPipes(pipesCsv);

                // Also read full 30-column records for PropertySet population
                try { fullRecords = WsproCsvReader.ReadFullRecords(pipesCsv); }
                catch { /* Non-critical: PropertySets just won't be populated */ }
            }
            catch (Exception ex)
            {
                ed.WriteMessage("\nFailed to read CSV files: " + ex.Message);
                return;
            }

            // Build lookup: (FromNodeId, ToNodeId) → full record for PropertySet data
            var recordLookup = new Dictionary<string, WsproCsvRecord>();
            if (fullRecords != null)
            {
                foreach (var r in fullRecords)
                {
                    string key = $"{r.UsId}|{r.DsId}";
                    if (!recordLookup.ContainsKey(key))
                        recordLookup[key] = r;
                }
            }

            if (nodes.Count == 0 || pipes.Count == 0)
            {
                ed.WriteMessage("\nNo nodes or pipes read from CSV. Check column mappings in WsproModels.cs.");
                return;
            }

            ed.WriteMessage($"\nRead {nodes.Count} nodes and {pipes.Count} pipes from WSPro CSVs.");

            using (var tr = doc.TransactionManager.StartTransaction())
            {
                // Ensure WSPro PropertySet definition exists for simulation data
                ObjectId propSetDefId = ObjectId.Null;
                if (fullRecords != null && fullRecords.Count > 0)
                {
                    propSetDefId = PropertySetManager.EnsureDefinition(doc.Database, tr, ed);
                }

                if (!TryGetFirstPartsListId(civilDoc, tr, ed, out ObjectId partsListId))
                {
                    tr.Commit();
                    return;
                }

                // Debug help: if nothing matches, we can quickly see what sizes exist.
                // (Callable separately via WSPRO_DIAG_PARTS as well.)

                // Prefer reusing an existing Pressure Pipe Network if one already exists in the drawing.
                bool isNewNetwork = false;
                if (!TryGetExistingPressureNetwork(civilDoc, tr, ed, out ObjectId networkId))
                {
                    if (!TryCreatePressureNetwork(civilDoc, "WSPro_Import", partsListId, ed, out networkId))
                    {
                        tr.Commit();
                        return;
                    }
                    isNewNetwork = true; // Fresh import — fittings need to be created
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

                // Track node connections for fitting placement
                var nodeConnectionCount = new Dictionary<string, int>();
                var nodeMaxDiameterMm = new Dictionary<string, double>();
                var nodeDirections = new Dictionary<string, List<Vector3d>>();

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

                            // Track connections at each node for fitting placement
                            IncrementNodeConnection(nodeConnectionCount, nodeMaxDiameterMm, pipe.FromNodeId, pipe.DiameterMm);
                            IncrementNodeConnection(nodeConnectionCount, nodeMaxDiameterMm, pipe.ToNodeId, pipe.DiameterMm);

                            // Track pipe directions at each node for fitting rotation
                            var dir = endPt - startPt;
                            if (!nodeDirections.ContainsKey(pipe.FromNodeId))
                                nodeDirections[pipe.FromNodeId] = new List<Vector3d>();
                            nodeDirections[pipe.FromNodeId].Add(dir);

                            if (!nodeDirections.ContainsKey(pipe.ToNodeId))
                                nodeDirections[pipe.ToNodeId] = new List<Vector3d>();
                            nodeDirections[pipe.ToNodeId].Add(-dir); // reverse direction at the other end

                            // Attach WSPro PropertySet with simulation data
                            if (!propSetDefId.IsNull)
                            {
                                string lookupKey = $"{pipe.FromNodeId}|{pipe.ToNodeId}";
                                if (recordLookup.TryGetValue(lookupKey, out var csvRecord))
                                    PropertySetManager.AttachAndPopulate(createdPipeId, propSetDefId, tr, csvRecord);
                            }
                        }
                    }
                    catch
                    {
                        // Skip pipe on failure, keep processing.
                    }
                }

                // --- Fitting placement at junction nodes ---
                int fittingsCreated = 0;
                int fittingsSkipped = 0;

                if (isNewNetwork)
                {
                    // Fresh import — no existing fittings, create them
                    bool hasFittings = false;
                    if (TryOpenPressurePartList(partsListId, tr, out var checkList))
                        hasFittings = GetFittingParts(checkList, tr).Any();

                    if (!hasFittings)
                    {
                        ed.WriteMessage("\nNo fitting parts in current parts list. Trying to add from catalog...");
                        hasFittings = TryAutoAddFittingsFromCatalog(partsListId, networkId, tr, ed);
                    }

                    if (hasFittings)
                    {
                        foreach (var kvp in nodeConnectionCount)
                        {
                            int connCount = kvp.Value;
                            if (connCount < 2)
                                continue;

                            if (!nodeDict.TryGetValue(kvp.Key, out var node))
                                continue;

                            var point = new Point3d(node.X, node.Y, node.Z);
                            double diamMm = nodeMaxDiameterMm.GetValueOrDefault(kvp.Key, 0);

                            if (!TryFindFittingPartSize(partsListId, tr, connCount, diamMm, out PressurePartSize fittingSize))
                            {
                                fittingsSkipped++;
                                continue;
                            }

                            try
                            {
                                if (TryAddFitting(networkId, tr, point, fittingSize, ed, out ObjectId fittingId) && !fittingId.IsNull)
                                {
                                    fittingsCreated++;

                                    // Rotate fitting to align with connecting pipes
                                    if (nodeDirections.TryGetValue(kvp.Key, out var dirs) && dirs.Count >= 2)
                                        TryRotateFitting(fittingId, tr, dirs, ed);
                                }
                                else
                                    fittingsSkipped++;
                            }
                            catch
                            {
                                fittingsSkipped++;
                            }
                        }
                    }
                    else
                    {
                        int junctionNodes = nodeConnectionCount.Count(kvp => kvp.Value >= 2);
                        fittingsSkipped = junctionNodes;

                        ed.WriteMessage($"\n{junctionNodes} junction nodes need fittings but no fitting parts are available.");
                        ed.WriteMessage("\nTo fix: In Civil 3D Toolspace > Settings > Pressure Networks > Parts Lists >");
                        ed.WriteMessage("\n  Right-click your parts list > Edit > Add Part Family > add Tee, Elbow, Cross families.");
                        ed.WriteMessage("\nThen re-run WSPRO_IMPORT_PRESSURE to place fittings at junctions.");
                    }
                }
                else
                {
                    // Re-import — existing network already has fittings, skip placement
                    ed.WriteMessage("\nSkipping fitting placement — using existing fittings from network.");
                }

                ed.WriteMessage($"\nCreated {pipeCreated} pressure pipes in network 'WSPro_Import'.");
                if (fittingsCreated > 0)
                    ed.WriteMessage($"\nCreated {fittingsCreated} fittings at junction nodes.");
                if (fittingsSkipped > 0)
                    ed.WriteMessage($"\nSkipped {fittingsSkipped} fittings (no matching part size or placement failed).");
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

                // Dump fitting parts info
                var fittingParts = GetFittingParts(openedPartsList, tr).ToList();
                ed.WriteMessage($"\nFitting parts found: {fittingParts.Count}");
                if (fittingParts.Count > 0)
                {
                    int shown = 0;
                    foreach (var fp in fittingParts)
                    {
                        if (shown >= 20) break;
                        string desc = "";
                        try { desc = fp.ToString(); } catch { }
                        TryGetPressurePartNominalDiameter(fp, out double d);
                        ed.WriteMessage($"\n  Fitting: dia={d:0.###} {desc}");
                        shown++;
                    }
                }

                // Dump catalog/factory types from AeccPressurePipesMgd
                var pressureAsm = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "AeccPressurePipesMgd", StringComparison.OrdinalIgnoreCase));

                if (pressureAsm != null)
                {
                    var catalogTypes = pressureAsm.GetTypes()
                        .Where(t => t.FullName != null &&
                                    (t.FullName.IndexOf("Catalog", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     t.FullName.IndexOf("Factory", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     (t.FullName.IndexOf("PartList", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                      t.FullName.IndexOf("Full", StringComparison.OrdinalIgnoreCase) >= 0)))
                        .OrderBy(t => t.FullName)
                        .ToList();

                    ed.WriteMessage($"\nCatalog/Factory types in AeccPressurePipesMgd: {catalogTypes.Count}");
                    foreach (var ct in catalogTypes.Take(10))
                    {
                        ed.WriteMessage($"\n  {ct.FullName}");

                        // Show constructors
                        foreach (var ctor in ct.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Take(3))
                        {
                            string sig = string.Join(", ", ctor.GetParameters().Select(p => p.ParameterType.Name));
                            ed.WriteMessage($"\n    ctor({sig})");
                        }

                        // Show static methods
                        foreach (var m in ct.GetMethods(BindingFlags.Public | BindingFlags.Static)
                            .Where(m => !m.IsSpecialName).Take(5))
                        {
                            string sig = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name));
                            ed.WriteMessage($"\n    static {m.Name}({sig}) -> {m.ReturnType.Name}");
                        }
                    }

                    // Show catalog files found on disk
                    string catalogPath = TryGetCatalogPath();
                    ed.WriteMessage($"\nCatalog file on disk: {catalogPath ?? "<not found>"}");
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

        private static void TryRotateFitting(ObjectId fittingId, Transaction tr, List<Vector3d> pipeDirections, Editor ed)
        {
            if (fittingId.IsNull || pipeDirections == null || pipeDirections.Count < 2)
                return;

            // Calculate the rotation angle based on connecting pipe directions.
            // Project all directions to XY plane for rotation calculation.
            var xyDirs = pipeDirections
                .Select(d => new Vector2d(d.X, d.Y))
                .Where(d => d.Length > 1e-6)
                .Select(d => d / d.Length) // normalize
                .ToList();

            if (xyDirs.Count < 2)
                return;

            double rotationAngle;

            if (xyDirs.Count == 2)
            {
                // Elbow: align with the bisector of the two pipe directions
                // The fitting's "run" aligns with the first pipe direction
                rotationAngle = Math.Atan2(xyDirs[0].Y, xyDirs[0].X);
            }
            else if (xyDirs.Count == 3)
            {
                // Tee: find the two most "straight" pipes (most opposite = dot product closest to -1)
                // Those form the run; the third is the branch.
                double bestDot = 2.0;
                int runA = 0, runB = 1;
                for (int i = 0; i < xyDirs.Count; i++)
                {
                    for (int j = i + 1; j < xyDirs.Count; j++)
                    {
                        double dot = xyDirs[i].X * xyDirs[j].X + xyDirs[i].Y * xyDirs[j].Y;
                        if (dot < bestDot) // most negative = most opposite
                        {
                            bestDot = dot;
                            runA = i;
                            runB = j;
                        }
                    }
                }

                // Align rotation with the run direction (use runA's angle)
                rotationAngle = Math.Atan2(xyDirs[runA].Y, xyDirs[runA].X);
            }
            else
            {
                // Cross (4+): find a pair of most-opposite pipes for the primary run
                double bestDot = 2.0;
                int runA = 0;
                for (int i = 0; i < xyDirs.Count; i++)
                {
                    for (int j = i + 1; j < xyDirs.Count; j++)
                    {
                        double dot = xyDirs[i].X * xyDirs[j].X + xyDirs[i].Y * xyDirs[j].Y;
                        if (dot < bestDot)
                        {
                            bestDot = dot;
                            runA = i;
                        }
                    }
                }

                rotationAngle = Math.Atan2(xyDirs[runA].Y, xyDirs[runA].X);
            }

            // Apply rotation to the fitting object
            try
            {
                var fittingObj = tr.GetObject(fittingId, OpenMode.ForWrite);
                if (fittingObj == null) return;

                // Try direct Rotation property
                var rotProp = fittingObj.GetType().GetProperty("Rotation", BindingFlags.Instance | BindingFlags.Public);
                if (rotProp != null && rotProp.CanWrite && rotProp.PropertyType == typeof(double))
                {
                    rotProp.SetValue(fittingObj, rotationAngle, null);
                    return;
                }

                // Try setting rotation via TransformBy with a rotation matrix
                var transformMethod = fittingObj.GetType().GetMethod("TransformBy", BindingFlags.Instance | BindingFlags.Public);
                if (transformMethod != null)
                {
                    var position = Point3d.Origin;
                    // Get the fitting's position
                    var posProp = fittingObj.GetType().GetProperty("Position", BindingFlags.Instance | BindingFlags.Public);
                    if (posProp != null)
                    {
                        var posVal = posProp.GetValue(fittingObj, null);
                        if (posVal is Point3d pt) position = pt;
                    }

                    var rotMatrix = Matrix3d.Rotation(rotationAngle, Vector3d.ZAxis, position);
                    transformMethod.Invoke(fittingObj, new object[] { rotMatrix });
                }
            }
            catch
            {
                // Rotation failed - fitting stays at default orientation
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

        private static void IncrementNodeConnection(Dictionary<string, int> countMap, Dictionary<string, double> diameterMap, string nodeId, double diamMm)
        {
            if (string.IsNullOrEmpty(nodeId)) return;

            countMap[nodeId] = countMap.GetValueOrDefault(nodeId, 0) + 1;

            if (!diameterMap.TryGetValue(nodeId, out double existing) || diamMm > existing)
                diameterMap[nodeId] = diamMm;
        }

        private static bool TryAutoAddFittingsFromCatalog(ObjectId partsListId, ObjectId networkId, Transaction tr, Editor ed)
        {
            var pressureAsm = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "AeccPressurePipesMgd", StringComparison.OrdinalIgnoreCase));
            if (pressureAsm == null) return false;

            Autodesk.AutoCAD.DatabaseServices.Database acDb = null;
            try { acDb = Application.DocumentManager.MdiActiveDocument?.Database; } catch { }

            // --- Strategy 1: Catalog factory → get fitting parts → AddPart to parts list ---
            ed.WriteMessage("\n[Fitting Strategy 1] Probing PressurePartCatalog...");
            if (TryAddFittingsViaCatalogFactory(pressureAsm, partsListId, acDb, tr, ed))
                return true;

            // --- Strategy 2: Try creating a "Full" parts list (includes all catalog content) ---
            ed.WriteMessage("\n[Fitting Strategy 2] Trying full parts list creation...");
            if (TryCreateFullPartsList(pressureAsm, networkId, acDb, tr, ed, out ObjectId fullPlId))
            {
                // Update the partsListId used for fitting lookup
                ed.WriteMessage("\nCreated full parts list with fitting content.");
                return true;
            }

            // --- Strategy 3: Check other parts lists in the drawing for one that has fittings ---
            ed.WriteMessage("\n[Fitting Strategy 3] Scanning alternate parts lists...");
            if (TryFindAlternatePartsListWithFittings(partsListId, networkId, tr, ed))
                return true;

            // --- Strategy 4: Clear instructions (no auto-dialog) ---
            ed.WriteMessage("\nAll automatic fitting strategies failed.");
            ed.WriteMessage("\nManual fix: Toolspace > Settings > Pressure Network > Parts Lists >");
            ed.WriteMessage("\n  Edit your parts list > Information tab > Load new catalog:");
            ed.WriteMessage("\n  C:\\ProgramData\\Autodesk\\C3D 2026\\enu\\Pressure Pipes Catalog\\Metric\\Metric_Ductile_Iron.sqlite");
            ed.WriteMessage("\n  Then go to Fittings tab > right-click > Add Part Family > add all.");
            ed.WriteMessage("\n  Click OK, then re-run WSPRO_IMPORT_PRESSURE.");

            return false;
        }

        // ── Strategy 1: Use PressurePartCatalogFactory to get catalog, enumerate fittings, add to parts list ──

        private static bool TryAddFittingsViaCatalogFactory(System.Reflection.Assembly pressureAsm, ObjectId partsListId, Autodesk.AutoCAD.DatabaseServices.Database acDb, Transaction tr, Editor ed)
        {
            // Find all Catalog-related types
            var catalogTypes = pressureAsm.GetTypes()
                .Where(t => t.FullName != null &&
                            (t.FullName.IndexOf("Catalog", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             t.FullName.IndexOf("Factory", StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderByDescending(t => t.Name.IndexOf("Factory", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0)
                .ThenBy(t => t.Name.Length)
                .ToList();

            ed.WriteMessage($"\n  Catalog/Factory types found: {catalogTypes.Count}");
            foreach (var ct in catalogTypes.Take(8))
                ed.WriteMessage($"\n    {ct.FullName}");

            // Try to get a catalog instance from any of these types
            object catalog = null;
            foreach (var catType in catalogTypes)
            {
                if (TryGetCatalogInstance(catType, acDb, ed, out catalog))
                    break;
            }

            if (catalog == null)
            {
                ed.WriteMessage("\n  Could not obtain catalog instance.");
                return false;
            }

            ed.WriteMessage($"\n  Opened catalog: {catalog.GetType().FullName}");

            // Try to set catalog path to a known catalog on disk
            TrySetCatalogPath(catalog, ed);

            // Dump catalog methods for diagnostics
            DumpObjectMethods(catalog, "Catalog", ed);

            // Open parts list for write
            PressurePartList partsListWrite = null;
            try { partsListWrite = tr.GetObject(partsListId, OpenMode.ForWrite) as PressurePartList; }
            catch { }
            if (partsListWrite == null) return false;

            // Dump parts list Add* methods
            var addMethods = partsListWrite.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.Name.IndexOf("Add", StringComparison.OrdinalIgnoreCase) >= 0 && !m.IsSpecialName)
                .ToList();

            ed.WriteMessage($"\n  PartsList Add* methods: {addMethods.Count}");
            foreach (var am in addMethods.Take(10))
            {
                var ps = am.GetParameters();
                string sig = string.Join(", ", ps.Select(p => $"{p.ParameterType.Name} {p.Name}"));
                ed.WriteMessage($"\n    {am.Name}({sig}) -> {am.ReturnType.Name}");
            }

            // Try to get fitting parts from catalog and add them
            return TryEnumerateCatalogAndAddFittings(catalog, partsListWrite, addMethods, tr, ed);
        }

        private static bool TryGetCatalogInstance(Type catType, Autodesk.AutoCAD.DatabaseServices.Database acDb, Editor ed, out object catalog)
        {
            catalog = null;

            // Try static properties (Instance, Current, Default)
            foreach (var sp in catType.GetProperties(BindingFlags.Public | BindingFlags.Static)
                .Where(p => p.GetIndexParameters().Length == 0))
            {
                try { catalog = sp.GetValue(null, null); } catch { }
                if (catalog != null) return true;
            }

            // Try static factory methods (parameterless, or taking Database)
            foreach (var sm in catType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.GetParameters().Length <= 1)
                .OrderBy(m => m.GetParameters().Length))
            {
                var ps = sm.GetParameters();
                try
                {
                    if (ps.Length == 0)
                        catalog = sm.Invoke(null, null);
                    else if (acDb != null && ps[0].ParameterType.IsInstanceOfType(acDb))
                        catalog = sm.Invoke(null, new object[] { acDb });
                }
                catch { }
                if (catalog != null) return true;
            }

            // Try constructors (parameterless, or taking Database)
            foreach (var ctor in catType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderBy(c => c.GetParameters().Length))
            {
                var ps = ctor.GetParameters();
                try
                {
                    if (ps.Length == 0)
                        catalog = ctor.Invoke(null);
                    else if (ps.Length == 1 && acDb != null && ps[0].ParameterType.IsInstanceOfType(acDb))
                        catalog = ctor.Invoke(new object[] { acDb });
                }
                catch { }
                if (catalog != null) return true;
            }

            return false;
        }

        private static void TrySetCatalogPath(object catalog, Editor ed)
        {
            if (catalog == null) return;

            string catalogPath = TryGetCatalogPath();
            if (string.IsNullOrEmpty(catalogPath))
            {
                ed.WriteMessage("\n  No catalog file found on disk.");
                return;
            }

            ed.WriteMessage($"\n  Catalog file: {catalogPath}");

            // Try methods like setCatalogFolder, SetCatalog, setCatalogGuid, Open, Load
            var catType = catalog.GetType();
            var setMethods = catType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => (m.Name.IndexOf("set", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             m.Name.IndexOf("open", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             m.Name.IndexOf("load", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             m.Name.IndexOf("init", StringComparison.OrdinalIgnoreCase) >= 0) &&
                            m.GetParameters().Length == 1 &&
                            m.GetParameters()[0].ParameterType == typeof(string))
                .ToList();

            string catalogFolder = Path.GetDirectoryName(catalogPath);

            foreach (var sm in setMethods)
            {
                try
                {
                    sm.Invoke(catalog, new object[] { catalogPath });
                    ed.WriteMessage($"\n  Called {sm.Name}(catalogPath) OK");
                    return;
                }
                catch { }

                try
                {
                    sm.Invoke(catalog, new object[] { catalogFolder });
                    ed.WriteMessage($"\n  Called {sm.Name}(catalogFolder) OK");
                    return;
                }
                catch { }
            }
        }

        private static string TryGetCatalogPath()
        {
            // Scan common Civil 3D pressure pipe catalog locations
            var searchRoots = new[]
            {
                @"C:\ProgramData\Autodesk\C3D 2026\enu\Pressure Pipes Catalog",
                @"C:\ProgramData\Autodesk\C3D 2026\enu\Pressure Pipe Catalog",
                @"C:\ProgramData\Autodesk\C3D 2025\enu\Pressure Pipes Catalog",
                @"C:\ProgramData\Autodesk\C3D 2026\enu\Data\Pressure Pipes Catalog",
            };

            // Also dynamically scan for any Autodesk C3D folder
            try
            {
                string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                string autodeskPath = Path.Combine(programData, "Autodesk");
                if (Directory.Exists(autodeskPath))
                {
                    var c3dDirs = Directory.GetDirectories(autodeskPath, "C3D*");
                    foreach (var c3dDir in c3dDirs.OrderByDescending(d => d))
                    {
                        var pressureDirs = new[]
                        {
                            Path.Combine(c3dDir, "enu", "Pressure Pipes Catalog"),
                            Path.Combine(c3dDir, "enu", "Pressure Pipe Catalog"),
                            Path.Combine(c3dDir, "enu", "Data", "Pressure Pipes Catalog"),
                        };

                        foreach (var pd in pressureDirs)
                        {
                            if (Directory.Exists(pd) && !searchRoots.Contains(pd))
                                searchRoots = searchRoots.Append(pd).ToArray();
                        }
                    }
                }
            }
            catch { }

            // Search for .sqlite catalog files, prefer Metric Ductile Iron
            foreach (var root in searchRoots)
            {
                if (!Directory.Exists(root)) continue;

                try
                {
                    // Look for Metric/Metric_Ductile_Iron.sqlite or similar
                    var sqliteFiles = Directory.GetFiles(root, "*.sqlite", SearchOption.AllDirectories);

                    // Prefer Ductile Iron
                    var diFile = sqliteFiles.FirstOrDefault(f =>
                        f.IndexOf("Ductile_Iron", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        f.IndexOf("Metric", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (diFile != null) return diFile;

                    // Any metric catalog
                    var metricFile = sqliteFiles.FirstOrDefault(f =>
                        f.IndexOf("Metric", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (metricFile != null) return metricFile;

                    // Any catalog
                    if (sqliteFiles.Length > 0) return sqliteFiles[0];

                    // Also look for .dwg catalog files
                    var dwgFiles = Directory.GetFiles(root, "*.dwg", SearchOption.AllDirectories);
                    if (dwgFiles.Length > 0)
                        return dwgFiles[0];
                }
                catch { }
            }

            return null;
        }

        private static bool TryEnumerateCatalogAndAddFittings(object catalog, PressurePartList partsList, List<MethodInfo> addMethods, Transaction tr, Editor ed)
        {
            var catType = catalog.GetType();

            // Probe ALL methods on the catalog and try those that might return fitting content
            var allMethods = catType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.ReturnType != typeof(void) &&
                            !m.IsSpecialName &&
                            m.GetParameters().Length <= 1)
                .OrderBy(m => m.Name)
                .ToList();

            foreach (var method in allMethods)
            {
                object result = null;
                var ps = method.GetParameters();

                try
                {
                    if (ps.Length == 0)
                    {
                        result = method.Invoke(catalog, null);
                    }
                    else if (ps[0].ParameterType.IsEnum)
                    {
                        // Try each enum value, prefer "Fitting" values
                        foreach (var enumVal in Enum.GetValues(ps[0].ParameterType))
                        {
                            string eName = enumVal.ToString();
                            if (eName.IndexOf("Fitting", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                eName.IndexOf("Elbow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                eName.IndexOf("Tee", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                eName.IndexOf("Branch", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                try { result = method.Invoke(catalog, new[] { enumVal }); } catch { }
                                if (result != null) break;
                            }
                        }
                        // If no fitting-specific value found, try all enum values
                        if (result == null)
                        {
                            foreach (var enumVal in Enum.GetValues(ps[0].ParameterType))
                            {
                                try { result = method.Invoke(catalog, new[] { enumVal }); } catch { }
                                if (result != null) break;
                            }
                        }
                    }
                }
                catch { continue; }

                if (result == null) continue;

                // If result is enumerable, try to add each item as a fitting part
                if (result is IEnumerable enumerable && result is not string)
                {
                    int added = 0;
                    foreach (var item in enumerable)
                    {
                        if (TryAddFamilyToPartsList(partsList, item, addMethods, ed))
                            added++;
                        if (added >= 20) break;
                    }

                    if (added > 0)
                    {
                        ed.WriteMessage($"\n  Added {added} parts via catalog.{method.Name}()");
                        // Verify fittings appeared
                        if (GetFittingParts(partsList, tr).Any())
                            return true;
                    }
                }
                else
                {
                    // Single item - try adding directly
                    if (TryAddFamilyToPartsList(partsList, result, addMethods, ed))
                    {
                        if (GetFittingParts(partsList, tr).Any())
                            return true;
                    }
                }
            }

            return false;
        }

        private static bool TryAddFamilyToPartsList(PressurePartList partsList, object family, List<MethodInfo> addMethods, Editor ed)
        {
            if (family == null || partsList == null) return false;

            // Extract identifiers from the family object
            Guid? familyGuid = null;
            ObjectId familyId = ObjectId.Null;
            string familyName = null;

            var familyType = family.GetType();
            foreach (var propName in new[] { "Guid", "FamilyGuid", "PartGuid", "Id", "PartId", "ObjectId" })
            {
                try
                {
                    var prop = familyType.GetProperty(propName, BindingFlags.Instance | BindingFlags.Public);
                    if (prop == null) continue;
                    var val = prop.GetValue(family, null);
                    if (val is Guid g && g != Guid.Empty) familyGuid = g;
                    else if (val is string s && Guid.TryParse(s, out var pg)) familyGuid = pg;
                    else if (val is ObjectId oid && !oid.IsNull) familyId = oid;
                }
                catch { }
            }

            try
            {
                familyName = family.ToString();
            }
            catch { }

            // Try each Add* method with compatible arguments
            foreach (var addMethod in addMethods)
            {
                var ps = addMethod.GetParameters();
                try
                {
                    if (ps.Length == 1)
                    {
                        if (ps[0].ParameterType.IsInstanceOfType(family))
                        {
                            addMethod.Invoke(partsList, new[] { family });
                            return true;
                        }
                        if (familyGuid.HasValue && ps[0].ParameterType == typeof(Guid))
                        {
                            addMethod.Invoke(partsList, new object[] { familyGuid.Value });
                            return true;
                        }
                        if (!familyId.IsNull && ps[0].ParameterType == typeof(ObjectId))
                        {
                            addMethod.Invoke(partsList, new object[] { familyId });
                            return true;
                        }
                        if (familyName != null && ps[0].ParameterType == typeof(string))
                        {
                            addMethod.Invoke(partsList, new object[] { familyName });
                            return true;
                        }
                    }
                    else if (ps.Length == 2)
                    {
                        // Some AddPart methods take (PressurePartType, object) or (Guid, PressurePartType)
                        if (ps[0].ParameterType.IsEnum && ps[1].ParameterType.IsInstanceOfType(family))
                        {
                            foreach (var enumVal in Enum.GetValues(ps[0].ParameterType))
                            {
                                if (enumVal.ToString().IndexOf("Fitting", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    addMethod.Invoke(partsList, new[] { enumVal, family });
                                    return true;
                                }
                            }
                        }
                        if (familyGuid.HasValue && ps[0].ParameterType == typeof(Guid) && ps[1].ParameterType.IsEnum)
                        {
                            foreach (var enumVal in Enum.GetValues(ps[1].ParameterType))
                            {
                                if (enumVal.ToString().IndexOf("Fitting", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    addMethod.Invoke(partsList, new object[] { familyGuid.Value, enumVal });
                                    return true;
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            return false;
        }

        // ── Strategy 2: Create a "Full" parts list with all catalog content ──

        private static bool TryCreateFullPartsList(System.Reflection.Assembly pressureAsm, ObjectId networkId, Autodesk.AutoCAD.DatabaseServices.Database acDb, Transaction tr, Editor ed, out ObjectId fullPlId)
        {
            fullPlId = ObjectId.Null;

            // Look for PressurePartListFull or CreatePressurePartListFull
            var fullTypes = pressureAsm.GetTypes()
                .Where(t => t.FullName != null &&
                            t.FullName.IndexOf("PartList", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            (t.FullName.IndexOf("Full", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             t.FullName.IndexOf("Create", StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();

            ed.WriteMessage($"\n  Full parts list types: {fullTypes.Count}");
            foreach (var ft in fullTypes.Take(5))
                ed.WriteMessage($"\n    {ft.FullName}");

            // Also look for Create methods on PressurePartList collection / extension types
            var extTypes = pressureAsm.GetTypes()
                .Where(t => t.FullName != null &&
                            t.FullName.IndexOf("Extension", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            t.FullName.IndexOf("Pressure", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            var civilDoc = CivilApplication.ActiveDocument;
            object stylesRoot = null;
            try { stylesRoot = civilDoc?.Styles; } catch { }

            foreach (var extType in extTypes)
            {
                var createMethods = extType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name.IndexOf("Create", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                m.Name.IndexOf("PartList", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

                foreach (var cm in createMethods)
                {
                    ed.WriteMessage($"\n  Found: {extType.Name}.{cm.Name}({string.Join(", ", cm.GetParameters().Select(p => p.ParameterType.Name))})");

                    var ps = cm.GetParameters();
                    object result = null;
                    try
                    {
                        if (ps.Length == 1 && stylesRoot != null && ps[0].ParameterType.IsInstanceOfType(stylesRoot))
                            result = cm.Invoke(null, new[] { stylesRoot });
                        else if (ps.Length == 1 && acDb != null && ps[0].ParameterType.IsInstanceOfType(acDb))
                            result = cm.Invoke(null, new object[] { acDb });
                        else if (ps.Length == 2 && stylesRoot != null)
                        {
                            if (ps[0].ParameterType.IsInstanceOfType(stylesRoot) && ps[1].ParameterType == typeof(string))
                                result = cm.Invoke(null, new object[] { stylesRoot, "WSPro_Full" });
                        }
                    }
                    catch (Exception ex)
                    {
                        ed.WriteMessage($"\n  {cm.Name} failed: {ex.InnerException?.Message ?? ex.Message}");
                        continue;
                    }

                    if (result is ObjectId oid && !oid.IsNull)
                    {
                        fullPlId = oid;
                        // Assign to network
                        try
                        {
                            var netObj = tr.GetObject(networkId, OpenMode.ForWrite);
                            if (netObj is PressurePipeNetwork ppn)
                                ppn.PartsListId = fullPlId;
                        }
                        catch { }
                        return true;
                    }
                }
            }

            return false;
        }

        // ── Strategy 3: Find alternate parts list with fittings ──

        private static bool TryFindAlternatePartsListWithFittings(ObjectId currentPartsListId, ObjectId networkId, Transaction tr, Editor ed)
        {
            var civilDoc = CivilApplication.ActiveDocument;
            if (civilDoc == null) return false;

            var pressureAsm = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "AeccPressurePipesMgd", StringComparison.OrdinalIgnoreCase));
            if (pressureAsm == null) return false;

            Type docExt = pressureAsm.GetType("Autodesk.Civil.ApplicationServices.StylesRootPressurePipesExtension", throwOnError: false);
            if (docExt == null)
                docExt = pressureAsm.GetTypes()
                    .FirstOrDefault(t => t.FullName != null &&
                                         t.FullName.IndexOf("Extension", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                         t.FullName.IndexOf("Pressure", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                         t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                             .Any(m => m.Name.IndexOf("PartList", StringComparison.OrdinalIgnoreCase) >= 0));
            if (docExt == null) return false;

            object stylesRoot = null;
            try { stylesRoot = civilDoc.Styles; } catch { }

            var listMethods = docExt.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name.IndexOf("PartList", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            m.GetParameters().Length == 1)
                .ToList();

            foreach (var method in listMethods)
            {
                object result = null;
                var ps = method.GetParameters();
                try
                {
                    if (stylesRoot != null && ps[0].ParameterType.IsInstanceOfType(stylesRoot))
                        result = method.Invoke(null, new[] { stylesRoot });
                    else if (ps[0].ParameterType.IsInstanceOfType(civilDoc))
                        result = method.Invoke(null, new object[] { civilDoc });
                }
                catch { continue; }

                if (result is not IEnumerable enumerable) continue;

                foreach (var item in enumerable)
                {
                    if (item is not ObjectId plId || plId.IsNull || plId == currentPartsListId) continue;

                    if (!TryOpenPressurePartList(plId, tr, out var altList)) continue;

                    if (GetFittingParts(altList, tr).Any())
                    {
                        ed.WriteMessage($"\n  Found alternate parts list with fittings.");

                        try
                        {
                            var netObj = tr.GetObject(networkId, OpenMode.ForWrite);
                            if (netObj is PressurePipeNetwork ppn)
                            {
                                ppn.PartsListId = plId;
                                ed.WriteMessage("\n  Switched network to parts list with fitting content.");
                                return true;
                            }
                        }
                        catch { }
                    }
                }
            }

            return false;
        }

        // ── Helpers ──

        private static void DumpObjectMethods(object obj, string label, Editor ed)
        {
            if (obj == null) return;
            var type = obj.GetType();

            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => !m.IsSpecialName && m.GetParameters().Length <= 2)
                .OrderBy(m => m.Name)
                .Take(20)
                .ToList();

            ed.WriteMessage($"\n  {label} methods ({methods.Count}):");
            foreach (var m in methods)
            {
                var ps = m.GetParameters();
                string sig = string.Join(", ", ps.Select(p => $"{p.ParameterType.Name}"));
                ed.WriteMessage($"\n    {m.Name}({sig}) -> {m.ReturnType.Name}");
            }
        }

        private static bool TryAddFitting(ObjectId networkId, Transaction tr, Point3d point, PressurePartSize fittingSize, Editor ed, out ObjectId fittingId)
        {
            fittingId = ObjectId.Null;
            if (networkId.IsNull || fittingSize == null) return false;

            if (tr.GetObject(networkId, OpenMode.ForWrite) is not PressurePipeNetwork network)
                return false;

            // Try the direct API first
            try
            {
                fittingId = network.AddFitting(point, fittingSize);
                return !fittingId.IsNull;
            }
            catch
            {
                // fall through to reflection-based fallback
            }

            // Reflection fallback for version-specific API differences
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
                        var result = m.Invoke(network, new object[] { point, fittingSize });
                        if (result is ObjectId oid && !oid.IsNull)
                        {
                            fittingId = oid;
                            return true;
                        }
                    }
                }
                catch { }
            }

            return false;
        }

        private static bool TryFindFittingPartSize(ObjectId partsListId, Transaction tr, int connectionCount, double diameterMm, out PressurePartSize fittingSize)
        {
            fittingSize = null;
            if (partsListId.IsNull || diameterMm <= 0.0) return false;

            if (!TryOpenPressurePartList(partsListId, tr, out PressurePartList partsList))
                return false;

            // Determine fitting type keyword based on connection count
            string[] preferredKeywords;
            if (connectionCount == 2)
                preferredKeywords = new[] { "elbow", "bend", "45", "90" };
            else if (connectionCount == 3)
                preferredKeywords = new[] { "tee", "reducing tee", "t-piece" };
            else // 4+
                preferredKeywords = new[] { "cross" };

            var targets = new[]
            {
                diameterMm,
                diameterMm / 25.4,
                diameterMm / 1000.0
            };

            const double relativeTol = 0.10; // slightly more tolerant for fittings
            double bestRel = double.MaxValue;
            PressurePartSize bestPart = null;
            bool bestMatchedKeyword = false;

            foreach (var candidate in GetFittingParts(partsList, tr))
            {
                if (!TryGetPressurePartNominalDiameter(candidate, out double nomDia))
                    continue;

                // Check diameter match
                double candidateRel = double.MaxValue;
                foreach (var t in targets)
                {
                    if (t <= 0) continue;
                    double rel = Math.Abs(nomDia - t) / t;
                    if (rel < candidateRel)
                        candidateRel = rel;
                }

                if (candidateRel > relativeTol)
                    continue;

                // Check if fitting type matches by keyword in description/name
                bool keywordMatch = FittingMatchesKeyword(candidate, preferredKeywords);

                // Prefer keyword-matched fittings; among those, pick closest diameter
                if (keywordMatch && !bestMatchedKeyword)
                {
                    bestRel = candidateRel;
                    bestPart = candidate;
                    bestMatchedKeyword = true;
                }
                else if (keywordMatch == bestMatchedKeyword && candidateRel < bestRel)
                {
                    bestRel = candidateRel;
                    bestPart = candidate;
                }
            }

            if (bestPart != null)
            {
                fittingSize = bestPart;
                return true;
            }

            return false;
        }

        private static bool FittingMatchesKeyword(PressurePartSize fitting, string[] keywords)
        {
            if (fitting == null || keywords == null) return false;

            // Try to get a description or name from the fitting
            string desc = "";
            try
            {
                var descProp = fitting.GetType().GetProperty("Description", BindingFlags.Instance | BindingFlags.Public);
                if (descProp != null)
                    desc += descProp.GetValue(fitting, null)?.ToString() ?? "";
            }
            catch { }

            try
            {
                var nameProp = fitting.GetType().GetProperty("Name", BindingFlags.Instance | BindingFlags.Public);
                if (nameProp != null)
                    desc += " " + (nameProp.GetValue(fitting, null)?.ToString() ?? "");
            }
            catch { }

            try
            {
                var familyProp = fitting.GetType().GetProperty("FamilyName", BindingFlags.Instance | BindingFlags.Public);
                if (familyProp != null)
                    desc += " " + (familyProp.GetValue(fitting, null)?.ToString() ?? "");
            }
            catch { }

            try
            {
                desc += " " + fitting.ToString();
            }
            catch { }

            if (string.IsNullOrWhiteSpace(desc))
                return false;

            string lower = desc.ToLowerInvariant();
            foreach (var kw in keywords)
            {
                if (lower.Contains(kw.ToLowerInvariant()))
                    return true;
            }

            return false;
        }

        private static IEnumerable<PressurePartSize> GetFittingParts(PressurePartList partsList, Transaction tr)
        {
            if (partsList == null)
                yield break;

            var seen = new HashSet<int>();

            // First, collect pipe part hashes so we can exclude them from fitting results
            var pipeParts = new HashSet<int>();
            try
            {
                var pp = partsList.GetParts(PressurePartType.PressurePipe);
                if (pp != null)
                    foreach (var p in pp)
                        pipeParts.Add(RuntimeHelpers.GetHashCode(p));
            }
            catch { }

            // Dynamically enumerate ALL PressurePartType enum values and try each one
            // This avoids hardcoding enum values that may differ by Civil 3D version
            Array allPartTypes = null;
            try
            {
                allPartTypes = Enum.GetValues(typeof(PressurePartType));
            }
            catch { }

            if (allPartTypes != null)
            {
                foreach (PressurePartType pt in allPartTypes)
                {
                    // Skip the pipe type - we only want fittings/appurtenances
                    if (pt == PressurePartType.PressurePipe)
                        continue;

                    List<PressurePartSize> parts = null;
                    try { parts = partsList.GetParts(pt); } catch { continue; }
                    if (parts == null) continue;

                    foreach (var part in parts)
                    {
                        // Skip if this is actually a pipe part (some APIs return same parts for multiple types)
                        if (pipeParts.Contains(RuntimeHelpers.GetHashCode(part)))
                            continue;
                        if (TryRememberPressurePartSize(part, seen))
                            yield return part;
                    }
                }
            }

            // Fallback: also try integer enum values 1-10 in case the enum has more values
            // than what's visible to our compile-time reference
            for (int i = 1; i <= 10; i++)
            {
                List<PressurePartSize> parts = null;
                try { parts = partsList.GetParts((PressurePartType)i); } catch { continue; }
                if (parts == null) continue;

                foreach (var part in parts)
                {
                    if (pipeParts.Contains(RuntimeHelpers.GetHashCode(part)))
                        continue;
                    if (TryRememberPressurePartSize(part, seen))
                        yield return part;
                }
            }
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

