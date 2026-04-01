#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace C3DPlugin
{
    /// <summary>
    /// Manages AEC Property Sets for storing WSPro simulation results on pressure pipe entities.
    /// Properties appear in the Civil 3D Properties palette under "Property Sets → WSPro Simulation Results".
    /// </summary>
    public static class PropertySetManager
    {
        public const string PropertySetName = "WSPro Simulation Results";

        // Property definitions: (name, isDouble, description)
        private static readonly (string Name, bool IsDouble, string Desc)[] PropertyDefs =
        {
            ("Pipe ID",          false, "WSPro Pipe identifier"),
            ("Max Pressure",     true,  "Maximum pressure (bar)"),
            ("Max Velocity",     true,  "Maximum velocity (m/s)"),
            ("Surge Max",        true,  "Maximum surge pressure (bar)"),
            ("Surge Min",        true,  "Minimum surge pressure (bar)"),
            ("Pressure Class",   false, "Pressure class (PN rating)"),
            ("Roughness",        true,  "Pipe roughness coefficient"),
            ("Velocity Flag",    false, "Velocity check result"),
            ("Surge Flag",       false, "Surge check result"),
            ("PN Class",         false, "Civil 3D PN classification"),
            ("System Type",      false, "System type"),
            ("Lining",           false, "Pipe lining"),
            ("Joint Type",       false, "Joint type"),
            ("Install Year",     false, "Installation year"),
            ("Notes",            false, "Additional notes"),
        };

        private static Assembly _aecPropAsm;
        private static bool _asmResolved;

        /// <summary>
        /// Ensures the WSPro property set definition exists. Creates it if missing.
        /// </summary>
        public static ObjectId EnsureDefinition(Database db, Transaction tr, Editor ed)
        {
            EnsureAssemblyLoaded();

            // Check if already exists
            ObjectId existingId = FindExistingDefinition(db, tr);
            if (!existingId.IsNull)
            {
                ed.WriteMessage($"\nUsing existing property set definition: '{PropertySetName}'.");
                return existingId;
            }

            if (_aecPropAsm == null)
            {
                ed.WriteMessage("\nAEC Property Set API not available. Cannot create property definitions.");
                ed.WriteMessage("\nEnsure AecPropDataMgd.dll is loaded (requires ACA/Civil 3D, not plain AutoCAD).");
                return ObjectId.Null;
            }

            try
            {
                return CreateDefinitionViaReflection(db, tr, ed);
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\nFailed to create property set definition: {ex.Message}");
                return ObjectId.Null;
            }
        }

        /// <summary>
        /// Attaches the property set to a pipe entity and populates values.
        /// </summary>
        public static bool AttachAndPopulate(ObjectId entityId, ObjectId propSetDefId, Transaction tr, WsproCsvRecord record)
        {
            if (entityId.IsNull || propSetDefId.IsNull || record == null) return false;
            EnsureAssemblyLoaded();
            if (_aecPropAsm == null) return false;

            try
            {
                var entity = tr.GetObject(entityId, OpenMode.ForWrite);
                if (entity == null) return false;

                // Call PropertyDataServices.AddPropertySet(entity, propSetDefId)
                var pdsType = _aecPropAsm.GetTypes().FirstOrDefault(t => t.Name == "PropertyDataServices");
                if (pdsType == null) return false;

                var addMethod = pdsType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "AddPropertySet" &&
                                         m.GetParameters().Length == 2 &&
                                         m.GetParameters()[1].ParameterType == typeof(ObjectId));
                if (addMethod != null)
                    addMethod.Invoke(null, new object[] { entity, propSetDefId });

                // Get property sets on this entity
                var getMethod = pdsType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "GetPropertySets" && m.GetParameters().Length == 1);
                if (getMethod == null) return false;

                var psIds = getMethod.Invoke(null, new object[] { entity });
                ObjectId propSetId = FindPropertySetByDef(psIds, propSetDefId, tr);
                if (propSetId.IsNull) return false;

                var propSet = tr.GetObject(propSetId, OpenMode.ForWrite);
                if (propSet == null) return false;

                // Populate values using SetAt(int index, object value)
                var values = BuildValueMap(record);
                foreach (var kvp in values)
                {
                    TrySetPropertyValue(propSet, kvp.Key, kvp.Value);
                }

                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Reads property set values from a pipe entity for export round-trip.
        /// </summary>
        public static void ReadValues(ObjectId entityId, Transaction tr, WsproCsvRecord record)
        {
            if (entityId.IsNull || record == null) return;
            EnsureAssemblyLoaded();
            if (_aecPropAsm == null) return;

            try
            {
                var entity = tr.GetObject(entityId, OpenMode.ForRead);
                if (entity == null) return;

                var pdsType = _aecPropAsm.GetTypes().FirstOrDefault(t => t.Name == "PropertyDataServices");
                if (pdsType == null) return;

                var getMethod = pdsType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "GetPropertySets" && m.GetParameters().Length == 1);
                if (getMethod == null) return;

                var psIds = getMethod.Invoke(null, new object[] { entity });
                ObjectId propSetId = FindPropertySetByDefName(psIds, tr);
                if (propSetId.IsNull) return;

                var propSet = tr.GetObject(propSetId, OpenMode.ForRead);
                if (propSet == null) return;

                record.PipeId = TryGetPropertyValue(propSet, "Pipe ID") ?? record.PipeId;
                record.MaxPres = TryGetPropertyValue(propSet, "Max Pressure") ?? record.MaxPres;
                record.MaxVel = TryGetPropertyValue(propSet, "Max Velocity") ?? record.MaxVel;
                record.SurgeMax = TryGetPropertyValue(propSet, "Surge Max") ?? record.SurgeMax;
                record.SurgeMin = TryGetPropertyValue(propSet, "Surge Min") ?? record.SurgeMin;
                record.PresClass = TryGetPropertyValue(propSet, "Pressure Class") ?? record.PresClass;
                record.Roughness = TryGetPropertyValue(propSet, "Roughness") ?? record.Roughness;
                record.VelocityFlag = TryGetPropertyValue(propSet, "Velocity Flag") ?? record.VelocityFlag;
                record.SurgeFlag = TryGetPropertyValue(propSet, "Surge Flag") ?? record.SurgeFlag;
                record.Civil3dPnClass = TryGetPropertyValue(propSet, "PN Class") ?? record.Civil3dPnClass;
                record.SystemType = TryGetPropertyValue(propSet, "System Type") ?? record.SystemType;
                record.Lining = TryGetPropertyValue(propSet, "Lining") ?? record.Lining;
                record.JointType = TryGetPropertyValue(propSet, "Joint Type") ?? record.JointType;
                record.InstallYear = TryGetPropertyValue(propSet, "Install Year") ?? record.InstallYear;
                record.Notes = TryGetPropertyValue(propSet, "Notes") ?? record.Notes;
            }
            catch { }
        }

        // ══════════════════════════════════════════════════════
        // Private implementation
        // ══════════════════════════════════════════════════════

        private static void EnsureAssemblyLoaded()
        {
            if (_asmResolved) return;
            _asmResolved = true;

            _aecPropAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "AecPropDataMgd", StringComparison.OrdinalIgnoreCase));

            if (_aecPropAsm == null)
            {
                var paths = new[]
                {
                    @"C:\Program Files\Autodesk\AutoCAD 2026\ACA\AecPropDataMgd.dll",
                    @"C:\Program Files\Autodesk\AutoCAD 2026\AecPropDataMgd.dll",
                };

                foreach (var p in paths)
                {
                    try
                    {
                        if (System.IO.File.Exists(p))
                        {
                            _aecPropAsm = Assembly.LoadFrom(p);
                            break;
                        }
                    }
                    catch { }
                }

                if (_aecPropAsm == null)
                {
                    try { _aecPropAsm = Assembly.Load("AecPropDataMgd"); } catch { }
                }
            }
        }

        private static ObjectId FindExistingDefinition(Database db, Transaction tr)
        {
            try
            {
                var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
                if (!nod.Contains("AEC_PROPERTY_SET_DEFS")) return ObjectId.Null;

                var aecDict = (DBDictionary)tr.GetObject(nod.GetAt("AEC_PROPERTY_SET_DEFS"), OpenMode.ForRead);
                foreach (var entry in aecDict)
                {
                    if (string.Equals(entry.Key, PropertySetName, StringComparison.OrdinalIgnoreCase))
                        return entry.Value;
                }
            }
            catch { }

            return ObjectId.Null;
        }

        private static ObjectId CreateDefinitionViaReflection(Database db, Transaction tr, Editor ed)
        {
            // Resolve types
            var propSetDefType = _aecPropAsm.GetTypes()
                .FirstOrDefault(t => t.Name == "PropertySetDefinition" && !t.IsAbstract);
            var propDefType = _aecPropAsm.GetTypes()
                .FirstOrDefault(t => t.Name == "PropertyDefinition" && !t.IsAbstract && t.Namespace != null &&
                                     t.Namespace.Contains("PropertyData"));

            if (propSetDefType == null)
            {
                ed.WriteMessage("\nPropertySetDefinition type not found in AecPropDataMgd.");
                return ObjectId.Null;
            }

            if (propDefType == null)
            {
                ed.WriteMessage("\nPropertyDefinition type not found. Listing candidates...");
                var candidates = _aecPropAsm.GetTypes()
                    .Where(t => t.Name.StartsWith("PropertyDef") && !t.IsAbstract)
                    .Take(10).ToList();
                foreach (var c in candidates)
                    ed.WriteMessage($"\n  {c.FullName}");
                return ObjectId.Null;
            }

            ed.WriteMessage($"\n  PropertySetDefinition: {propSetDefType.FullName}");
            ed.WriteMessage($"\n  PropertyDefinition: {propDefType.FullName}");

            // Find the DataType enum
            var dataTypeEnum = _aecPropAsm.GetTypes()
                .FirstOrDefault(t => t.IsEnum && t.Name == "DataType");

            object dataTypeReal = null;
            object dataTypeText = null;

            if (dataTypeEnum != null)
            {
                foreach (var val in Enum.GetValues(dataTypeEnum))
                {
                    string name = val.ToString().ToLowerInvariant();
                    if (name.Contains("real") || name.Contains("double") || name.Contains("number"))
                        dataTypeReal = val;
                    if (name.Contains("text") || name.Contains("string"))
                        dataTypeText = val;
                }

                ed.WriteMessage($"\n  DataType enum: {dataTypeEnum.FullName}");
                ed.WriteMessage($"\n  Real={dataTypeReal}, Text={dataTypeText}");

                if (dataTypeReal == null || dataTypeText == null)
                {
                    ed.WriteMessage("\n  All DataType values:");
                    foreach (var val in Enum.GetValues(dataTypeEnum))
                        ed.WriteMessage($"\n    {val} = {(int)val}");
                }
            }

            // Create PropertySetDefinition
            var propSetDef = Activator.CreateInstance(propSetDefType) as DBObject;
            if (propSetDef == null)
            {
                ed.WriteMessage("\nFailed to create PropertySetDefinition instance.");
                return ObjectId.Null;
            }

            // Set description
            TrySet(propSetDef, "Description", "WSPro hydraulic simulation results");

            // Get the Definitions collection
            var defsProp = propSetDefType.GetProperty("Definitions", BindingFlags.Instance | BindingFlags.Public);
            object defsCollection = null;
            if (defsProp != null)
                defsCollection = defsProp.GetValue(propSetDef, null);

            if (defsCollection == null)
            {
                ed.WriteMessage("\nCould not access Definitions collection on PropertySetDefinition.");
                // Try alternative: look for Add method directly on propSetDef
            }

            // Find Add method on the definitions collection
            MethodInfo addMethod = null;
            if (defsCollection != null)
            {
                addMethod = defsCollection.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "Add" &&
                                         m.GetParameters().Length == 1 &&
                                         m.GetParameters()[0].ParameterType.IsAssignableFrom(propDefType));

                if (addMethod == null)
                {
                    // Try any Add with 1 parameter
                    addMethod = defsCollection.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                        .FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length == 1);
                }

                ed.WriteMessage($"\n  Definitions collection: {defsCollection.GetType().Name}");
                ed.WriteMessage($"\n  Add method: {(addMethod != null ? addMethod.ToString() : "NOT FOUND")}");
            }

            // Add property definitions
            int addedCount = 0;
            foreach (var (name, isDouble, desc) in PropertyDefs)
            {
                try
                {
                    var propDef = Activator.CreateInstance(propDefType);
                    if (propDef == null) continue;

                    // Set Name
                    TrySet(propDef, "Name", name);

                    // Set Description
                    TrySet(propDef, "Description", desc);

                    // Set DataType
                    if (dataTypeEnum != null)
                    {
                        var dtProp = propDefType.GetProperty("DataType", BindingFlags.Instance | BindingFlags.Public);
                        if (dtProp != null && dtProp.CanWrite)
                        {
                            dtProp.SetValue(propDef, isDouble ? dataTypeReal : dataTypeText, null);
                        }
                    }

                    // Set DefaultData
                    var defaultProp = propDefType.GetProperty("DefaultData", BindingFlags.Instance | BindingFlags.Public);
                    if (defaultProp != null && defaultProp.CanWrite)
                    {
                        try
                        {
                            defaultProp.SetValue(propDef, isDouble ? (object)0.0 : (object)"", null);
                        }
                        catch { }
                    }

                    // Add to collection
                    if (addMethod != null && defsCollection != null)
                    {
                        addMethod.Invoke(defsCollection, new[] { propDef });
                        addedCount++;
                    }
                }
                catch (Exception ex)
                {
                    ed.WriteMessage($"\n  Failed to add property '{name}': {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            ed.WriteMessage($"\n  Added {addedCount}/{PropertyDefs.Length} property definitions.");

            // Set AppliesTo filter — make it apply to all AcDbEntity objects
            try
            {
                var appliesToProp = propSetDefType.GetProperty("AppliesToFilter", BindingFlags.Instance | BindingFlags.Public)
                                ?? propSetDefType.GetProperty("AppliesTo", BindingFlags.Instance | BindingFlags.Public);

                if (appliesToProp != null)
                {
                    var filter = appliesToProp.GetValue(propSetDef, null);
                    if (filter != null)
                    {
                        var filterAdd = filter.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                            .Where(m => m.Name == "Add" && m.GetParameters().Length == 1)
                            .ToList();

                        foreach (var fa in filterAdd)
                        {
                            var pt = fa.GetParameters()[0].ParameterType;
                            try
                            {
                                if (pt == typeof(string))
                                    fa.Invoke(filter, new object[] { "AcDbEntity" });
                                else if (pt == typeof(Autodesk.AutoCAD.Runtime.RXClass))
                                    fa.Invoke(filter, new object[] { Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(Entity)) });
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            // Register in the AEC_PROPERTY_SET_DEFS dictionary
            try
            {
                var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForWrite);

                DBDictionary aecDict;
                if (nod.Contains("AEC_PROPERTY_SET_DEFS"))
                {
                    aecDict = (DBDictionary)tr.GetObject(nod.GetAt("AEC_PROPERTY_SET_DEFS"), OpenMode.ForWrite);
                }
                else
                {
                    aecDict = new DBDictionary();
                    nod.SetAt("AEC_PROPERTY_SET_DEFS", aecDict);
                    tr.AddNewlyCreatedDBObject(aecDict, true);
                }

                // Remove old definition if exists
                if (aecDict.Contains(PropertySetName))
                {
                    var oldId = aecDict.GetAt(PropertySetName);
                    aecDict.Remove(PropertySetName);
                    var oldObj = tr.GetObject(oldId, OpenMode.ForWrite);
                    oldObj?.Erase();
                }

                ObjectId defId = aecDict.SetAt(PropertySetName, propSetDef);
                tr.AddNewlyCreatedDBObject(propSetDef, true);

                ed.WriteMessage($"\nCreated property set definition: '{PropertySetName}' with {addedCount} properties.");
                return defId;
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\nFailed to register definition: {ex.Message}");
                return ObjectId.Null;
            }
        }

        // ── Value get/set helpers ──

        private static Dictionary<string, object> BuildValueMap(WsproCsvRecord r)
        {
            return new Dictionary<string, object>
            {
                ["Pipe ID"] = r.PipeId ?? "",
                ["Max Pressure"] = ParseDouble(r.MaxPres),
                ["Max Velocity"] = ParseDouble(r.MaxVel),
                ["Surge Max"] = ParseDouble(r.SurgeMax),
                ["Surge Min"] = ParseDouble(r.SurgeMin),
                ["Pressure Class"] = r.PresClass ?? "",
                ["Roughness"] = ParseDouble(r.Roughness),
                ["Velocity Flag"] = r.VelocityFlag ?? "",
                ["Surge Flag"] = r.SurgeFlag ?? "",
                ["PN Class"] = r.Civil3dPnClass ?? "",
                ["System Type"] = r.SystemType ?? "",
                ["Lining"] = r.Lining ?? "",
                ["Joint Type"] = r.JointType ?? "",
                ["Install Year"] = r.InstallYear ?? "",
                ["Notes"] = r.Notes ?? "",
            };
        }

        private static void TrySetPropertyValue(DBObject propSet, string name, object value)
        {
            if (propSet == null) return;
            var psType = propSet.GetType();

            // Try PropertyNameToId + SetAt(int, object)
            try
            {
                var nameToId = psType.GetMethod("PropertyNameToId", BindingFlags.Instance | BindingFlags.Public);
                if (nameToId != null)
                {
                    var idx = nameToId.Invoke(propSet, new object[] { name });
                    if (idx is int intIdx && intIdx >= 0)
                    {
                        var setAt = psType.GetMethod("SetAt", BindingFlags.Instance | BindingFlags.Public,
                            null, new[] { typeof(int), typeof(object) }, null);
                        if (setAt != null)
                        {
                            setAt.Invoke(propSet, new[] { (object)intIdx, value });
                            return;
                        }
                    }
                }
            }
            catch { }

            // Fallback: try SetAt(string, object)
            try
            {
                var setAt = psType.GetMethod("SetAt", BindingFlags.Instance | BindingFlags.Public,
                    null, new[] { typeof(string), typeof(object) }, null);
                if (setAt != null)
                    setAt.Invoke(propSet, new[] { name, value });
            }
            catch { }
        }

        private static string TryGetPropertyValue(DBObject propSet, string name)
        {
            if (propSet == null) return null;
            var psType = propSet.GetType();

            try
            {
                var nameToId = psType.GetMethod("PropertyNameToId", BindingFlags.Instance | BindingFlags.Public);
                if (nameToId != null)
                {
                    var idx = nameToId.Invoke(propSet, new object[] { name });
                    if (idx is int intIdx && intIdx >= 0)
                    {
                        var getAt = psType.GetMethod("GetAt", BindingFlags.Instance | BindingFlags.Public,
                            null, new[] { typeof(int) }, null);
                        if (getAt != null)
                        {
                            var val = getAt.Invoke(propSet, new object[] { intIdx });
                            return val?.ToString();
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        // ── ObjectId helpers ──

        private static ObjectId FindPropertySetByDef(object psIds, ObjectId defId, Transaction tr)
        {
            foreach (ObjectId psId in EnumerateObjectIds(psIds))
            {
                try
                {
                    var ps = tr.GetObject(psId, OpenMode.ForRead);
                    var defProp = ps.GetType().GetProperty("PropertySetDefinition", BindingFlags.Instance | BindingFlags.Public);
                    if (defProp != null)
                    {
                        var val = defProp.GetValue(ps, null);
                        if (val is ObjectId oid && oid == defId) return psId;
                    }
                }
                catch { }
            }
            return ObjectId.Null;
        }

        private static ObjectId FindPropertySetByDefName(object psIds, Transaction tr)
        {
            foreach (ObjectId psId in EnumerateObjectIds(psIds))
            {
                try
                {
                    var ps = tr.GetObject(psId, OpenMode.ForRead);
                    var defProp = ps.GetType().GetProperty("PropertySetDefinition", BindingFlags.Instance | BindingFlags.Public);
                    if (defProp == null) continue;

                    var defIdVal = defProp.GetValue(ps, null);
                    if (defIdVal is not ObjectId defId || defId.IsNull) continue;

                    var def = tr.GetObject(defId, OpenMode.ForRead);
                    if (PropertyExtractor.TryGetString(def, "Name", out var name) &&
                        string.Equals(name, PropertySetName, StringComparison.OrdinalIgnoreCase))
                        return psId;
                }
                catch { }
            }
            return ObjectId.Null;
        }

        private static IEnumerable<ObjectId> EnumerateObjectIds(object collection)
        {
            if (collection is ObjectIdCollection oidCol)
            {
                foreach (ObjectId id in oidCol) yield return id;
                yield break;
            }
            if (collection is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                    if (item is ObjectId oid) yield return oid;
            }
        }

        // ── Utility ──

        private static void TrySet(object obj, string propName, object value)
        {
            try
            {
                var prop = obj.GetType().GetProperty(propName, BindingFlags.Instance | BindingFlags.Public);
                if (prop != null && prop.CanWrite)
                    prop.SetValue(obj, value, null);
            }
            catch { }
        }

        private static double ParseDouble(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0.0;
            double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v);
            return v;
        }
    }
}
