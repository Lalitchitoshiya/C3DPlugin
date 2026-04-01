#nullable disable
using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace C3DPlugin
{
    /// <summary>
    /// Shared reflection helpers for extracting properties from Civil 3D pressure pipe entities.
    /// Uses the same defensive reflection pattern as WsproImporter.
    /// </summary>
    public static class PropertyExtractor
    {
        public static bool TryGetPoint3d(DBObject obj, string propertyName, out Point3d result)
        {
            result = Point3d.Origin;
            if (obj == null) return false;

            try
            {
                var prop = obj.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
                if (prop != null && prop.PropertyType == typeof(Point3d))
                {
                    result = (Point3d)prop.GetValue(obj, null);
                    return true;
                }
            }
            catch { }

            return false;
        }

        public static bool TryGetDouble(DBObject obj, string propertyName, out double result)
        {
            result = 0.0;
            if (obj == null) return false;

            try
            {
                var prop = obj.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
                if (prop == null) return false;

                var val = prop.GetValue(obj, null);
                if (val is double d) { result = d; return true; }
                if (val is float f) { result = f; return true; }
                if (val is int i) { result = i; return true; }
                if (val != null && double.TryParse(val.ToString(), out var parsed))
                {
                    result = parsed;
                    return true;
                }
            }
            catch { }

            return false;
        }

        public static bool TryGetString(DBObject obj, string propertyName, out string result)
        {
            result = "";
            if (obj == null) return false;

            try
            {
                var prop = obj.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
                if (prop == null) return false;

                var val = prop.GetValue(obj, null);
                if (val != null)
                {
                    result = val.ToString();
                    return true;
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Extract pipe start point. Tries StartPoint, then StartPoint2d, then geometric extents.
        /// </summary>
        public static bool TryGetStartPoint(DBObject pipe, out Point3d result)
        {
            if (TryGetPoint3d(pipe, "StartPoint", out result)) return true;
            if (TryGetPoint3d(pipe, "StartPoint3d", out result)) return true;

            // Fallback: try getting the curve and its start point
            try
            {
                var curveProp = pipe.GetType().GetProperty("BaseCurve", BindingFlags.Instance | BindingFlags.Public)
                             ?? pipe.GetType().GetProperty("Curve", BindingFlags.Instance | BindingFlags.Public);
                if (curveProp != null)
                {
                    var curve = curveProp.GetValue(pipe, null);
                    if (curve != null)
                    {
                        var startProp = curve.GetType().GetProperty("StartPoint", BindingFlags.Instance | BindingFlags.Public);
                        if (startProp != null)
                        {
                            result = (Point3d)startProp.GetValue(curve, null);
                            return true;
                        }
                    }
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Extract pipe end point.
        /// </summary>
        public static bool TryGetEndPoint(DBObject pipe, out Point3d result)
        {
            if (TryGetPoint3d(pipe, "EndPoint", out result)) return true;
            if (TryGetPoint3d(pipe, "EndPoint3d", out result)) return true;

            try
            {
                var curveProp = pipe.GetType().GetProperty("BaseCurve", BindingFlags.Instance | BindingFlags.Public)
                             ?? pipe.GetType().GetProperty("Curve", BindingFlags.Instance | BindingFlags.Public);
                if (curveProp != null)
                {
                    var curve = curveProp.GetValue(pipe, null);
                    if (curve != null)
                    {
                        var endProp = curve.GetType().GetProperty("EndPoint", BindingFlags.Instance | BindingFlags.Public);
                        if (endProp != null)
                        {
                            result = (Point3d)endProp.GetValue(curve, null);
                            return true;
                        }
                    }
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Extract nominal diameter in mm from a PressurePipe or its PartSize.
        /// </summary>
        public static bool TryGetDiameter(DBObject pipe, Transaction tr, out double diameterMm)
        {
            diameterMm = 0;

            // Try direct properties on the pipe object
            foreach (var name in new[]
            {
                "InnerDiameterOrWidth", "InnerDiameter", "NominalDiameter",
                "OuterDiameterOrWidth", "OuterDiameter", "Diameter",
                "PipeDiameterInside", "PipeDiameterOutside",
                "InsideDiameter", "OutsideDiameter"
            })
            {
                if (TryGetDouble(pipe, name, out diameterMm) && diameterMm > 0)
                    return true;
            }

            // Try via PartSize object
            try
            {
                var partSizeProp = pipe.GetType().GetProperty("PartSize", BindingFlags.Instance | BindingFlags.Public);
                if (partSizeProp != null)
                {
                    var partSize = partSizeProp.GetValue(pipe, null);
                    if (partSize != null)
                    {
                        // Try NominalDiameter on the part size
                        foreach (var name in new[] { "NominalDiameter", "Diameter", "InnerDiameter", "OuterDiameter" })
                        {
                            var prop = partSize.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                            if (prop != null)
                            {
                                var val = prop.GetValue(partSize, null);
                                if (val is double d && d > 0) { diameterMm = d; return true; }
                                if (val != null)
                                {
                                    string vs = val.ToString();
                                    if (double.TryParse(vs, NumberStyles.Any, CultureInfo.InvariantCulture, out var dp) && dp > 0)
                                    {
                                        diameterMm = dp; return true;
                                    }
                                    double parsed = ParseDiameterFromString(vs);
                                    if (parsed > 0) { diameterMm = parsed; return true; }
                                }
                            }
                        }

                        // Try GetProperty(PressurePartContextType enum) for all diameter-related values
                        var getPropertyMethod = partSize.GetType().GetMethod("GetProperty", BindingFlags.Instance | BindingFlags.Public);
                        if (getPropertyMethod != null)
                        {
                            var paramType = getPropertyMethod.GetParameters().FirstOrDefault()?.ParameterType;
                            if (paramType != null && paramType.IsEnum)
                            {
                                foreach (var enumVal in Enum.GetValues(paramType))
                                {
                                    string eName = enumVal.ToString();
                                    if (eName.IndexOf("Diameter", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        try
                                        {
                                            var result = getPropertyMethod.Invoke(partSize, new[] { enumVal });
                                            if (result == null) continue;
                                            string rs = result.ToString();
                                            if (double.TryParse(rs, NumberStyles.Any, CultureInfo.InvariantCulture, out var d2) && d2 > 0)
                                            {
                                                diameterMm = d2;
                                                return true;
                                            }
                                            // Parse from strings like "150 mm x 150 mm"
                                            double parsed = ParseDiameterFromString(rs);
                                            if (parsed > 0)
                                            {
                                                diameterMm = parsed;
                                                return true;
                                            }
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            // Try via PartSizeId → open the PartSize DBObject from the database
            try
            {
                var partSizeIdProp = pipe.GetType().GetProperty("PartSizeId", BindingFlags.Instance | BindingFlags.Public);
                if (partSizeIdProp != null)
                {
                    var psId = partSizeIdProp.GetValue(pipe, null);
                    if (psId is ObjectId oid && !oid.IsNull && tr != null)
                    {
                        var psObj = tr.GetObject(oid, OpenMode.ForRead);
                        if (psObj != null)
                        {
                            foreach (var name in new[] { "NominalDiameter", "Diameter", "InnerDiameter" })
                            {
                                if (TryGetDouble(psObj, name, out diameterMm) && diameterMm > 0)
                                    return true;
                            }
                        }
                    }
                }
            }
            catch { }

            // Brute force: scan ALL properties with "diam" in the name (double or string)
            try
            {
                foreach (var prop in pipe.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Where(p => (p.Name.IndexOf("diam", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 p.Name.IndexOf("size", StringComparison.OrdinalIgnoreCase) >= 0) &&
                                p.GetIndexParameters().Length == 0))
                {
                    try
                    {
                        var val = prop.GetValue(pipe, null);
                        if (val is double d && d > 0) { diameterMm = d; return true; }
                        if (val != null)
                        {
                            double parsed = ParseDiameterFromString(val.ToString());
                            if (parsed > 0) { diameterMm = parsed; return true; }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // Last resort: try Description property (e.g., "pipe-150 mm-push on-ductile...")
            if (TryGetString(pipe, "Description", out var desc) && !string.IsNullOrEmpty(desc))
            {
                double parsed = ParseDiameterFromString(desc);
                if (parsed > 0) { diameterMm = parsed; return true; }
            }

            return false;
        }

        /// <summary>
        /// Diagnostic: dumps all properties on a pipe object that contain "diam" in the name.
        /// Call this to debug diameter extraction issues.
        /// </summary>
        public static void DiagnoseDiameter(DBObject pipe, Transaction tr, Autodesk.AutoCAD.EditorInput.Editor ed)
        {
            if (pipe == null || ed == null) return;

            ed.WriteMessage($"\n  Pipe type: {pipe.GetType().FullName}");

            // All properties with "diam" in name
            foreach (var prop in pipe.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => (p.Name.IndexOf("diam", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             p.Name.IndexOf("size", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             p.Name.IndexOf("width", StringComparison.OrdinalIgnoreCase) >= 0) &&
                            p.GetIndexParameters().Length == 0)
                .OrderBy(p => p.Name))
            {
                string val = "<error>";
                try { val = prop.GetValue(pipe, null)?.ToString() ?? "<null>"; } catch (Exception ex) { val = ex.InnerException?.Message ?? ex.Message; }
                ed.WriteMessage($"\n    {prop.Name} ({prop.PropertyType.Name}) = {val}");
            }

            // PartSize info
            try
            {
                var psProp = pipe.GetType().GetProperty("PartSize", BindingFlags.Instance | BindingFlags.Public);
                if (psProp != null)
                {
                    var ps = psProp.GetValue(pipe, null);
                    if (ps != null)
                    {
                        ed.WriteMessage($"\n  PartSize type: {ps.GetType().FullName}");
                        foreach (var prop in ps.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                            .Where(p => p.GetIndexParameters().Length == 0)
                            .OrderBy(p => p.Name).Take(20))
                        {
                            string val = "<error>";
                            try { val = prop.GetValue(ps, null)?.ToString() ?? "<null>"; } catch { }
                            ed.WriteMessage($"\n    PS.{prop.Name} ({prop.PropertyType.Name}) = {val}");
                        }
                    }
                    else
                    {
                        ed.WriteMessage("\n  PartSize = <null>");
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Extract pipe length. Tries Length2D, Length3D, Length, or computes from endpoints.
        /// </summary>
        public static bool TryGetLength(DBObject pipe, out double length)
        {
            foreach (var name in new[] { "Length2D", "Length3D", "Length" })
            {
                if (TryGetDouble(pipe, name, out length) && length > 0)
                    return true;
            }

            // Compute from endpoints
            if (TryGetStartPoint(pipe, out var start) && TryGetEndPoint(pipe, out var end))
            {
                length = start.DistanceTo(end);
                return length > 0;
            }

            length = 0;
            return false;
        }

        /// <summary>
        /// Extract material name from pipe or its part family description.
        /// </summary>
        public static string GetMaterialCode(DBObject pipe, Transaction tr)
        {
            // Try Description, MaterialName, FamilyName
            foreach (var name in new[] { "MaterialName", "Material", "Description", "FamilyName" })
            {
                if (TryGetString(pipe, name, out var val) && !string.IsNullOrWhiteSpace(val))
                    return MapMaterial(val);
            }

            // Try via PartSize description
            try
            {
                var partSizeProp = pipe.GetType().GetProperty("PartSize", BindingFlags.Instance | BindingFlags.Public);
                if (partSizeProp != null)
                {
                    var partSize = partSizeProp.GetValue(pipe, null);
                    if (partSize != null)
                    {
                        var desc = partSize.ToString();
                        if (!string.IsNullOrWhiteSpace(desc))
                            return MapMaterial(desc);
                    }
                }
            }
            catch { }

            return "";
        }

        /// <summary>
        /// Map Civil 3D material descriptions to WSPro short codes.
        /// </summary>
        /// <summary>
        /// Parse diameter value from strings like "150 mm x 150 mm", "pipe-150 mm-push on", "DN150", "150".
        /// </summary>
        private static double ParseDiameterFromString(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;

            // Try "150 mm x 150 mm" → extract first number before "mm"
            var mmMatch = System.Text.RegularExpressions.Regex.Match(text, @"(\d+\.?\d*)\s*mm");
            if (mmMatch.Success && double.TryParse(mmMatch.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var mm))
                return mm;

            // Try "DN150" or "DN 150"
            var dnMatch = System.Text.RegularExpressions.Regex.Match(text, @"DN\s*(\d+\.?\d*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (dnMatch.Success && double.TryParse(dnMatch.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var dn))
                return dn;

            // Try first standalone number in the string
            var numMatch = System.Text.RegularExpressions.Regex.Match(text, @"(?<!\w)(\d+\.?\d*)(?!\w)");
            if (numMatch.Success && double.TryParse(numMatch.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var num) && num > 10 && num < 10000)
                return num;

            return 0;
        }

        private static string MapMaterial(string description)
        {
            if (string.IsNullOrWhiteSpace(description)) return "";

            string lower = description.ToLowerInvariant();
            if (lower.Contains("ductile") || lower.Contains(" di ") || lower == "di") return "DI";
            if (lower.Contains("pvc")) return "PVC";
            if (lower.Contains("hdpe") || lower.Contains("polyethylene") || lower.Contains(" pe")) return "PE";
            if (lower.Contains("steel")) return "Steel";
            if (lower.Contains("concrete")) return "Concrete";
            if (lower.Contains("cast iron") || lower.Contains("ci")) return "CI";

            return description.Length > 20 ? description.Substring(0, 20) : description;
        }

        /// <summary>
        /// Get fitting/pipe position (for node ID generation).
        /// </summary>
        public static bool TryGetPosition(DBObject obj, out Point3d result)
        {
            if (TryGetPoint3d(obj, "Position", out result)) return true;
            if (TryGetPoint3d(obj, "Location", out result)) return true;
            if (TryGetStartPoint(obj, out result)) return true;
            return false;
        }
    }
}
