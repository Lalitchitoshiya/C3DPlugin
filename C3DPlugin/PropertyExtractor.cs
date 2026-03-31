#nullable disable
using System;
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

            // Try direct InnerDiameter or NominalDiameter properties
            foreach (var name in new[] { "InnerDiameterOrWidth", "InnerDiameter", "NominalDiameter", "OuterDiameterOrWidth" })
            {
                if (TryGetDouble(pipe, name, out diameterMm) && diameterMm > 0)
                    return true;
            }

            // Try via PartSize
            try
            {
                var partSizeProp = pipe.GetType().GetProperty("PartSize", BindingFlags.Instance | BindingFlags.Public);
                if (partSizeProp != null)
                {
                    var partSize = partSizeProp.GetValue(pipe, null);
                    if (partSize != null)
                    {
                        // Try NominalDiameter on the part size
                        var nomProp = partSize.GetType().GetProperty("NominalDiameter", BindingFlags.Instance | BindingFlags.Public);
                        if (nomProp != null)
                        {
                            var val = nomProp.GetValue(partSize, null);
                            if (val is double d && d > 0) { diameterMm = d; return true; }
                        }

                        // Try GetProperty(PressurePartContextType.DiameterNominal)
                        var getPropertyMethod = partSize.GetType().GetMethod("GetProperty", BindingFlags.Instance | BindingFlags.Public);
                        if (getPropertyMethod != null)
                        {
                            var paramType = getPropertyMethod.GetParameters().FirstOrDefault()?.ParameterType;
                            if (paramType != null && paramType.IsEnum)
                            {
                                foreach (var enumVal in Enum.GetValues(paramType))
                                {
                                    if (enumVal.ToString().IndexOf("DiameterNominal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        enumVal.ToString().IndexOf("ConnectionPointNominalDiameter", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        var result = getPropertyMethod.Invoke(partSize, new[] { enumVal });
                                        if (result != null && double.TryParse(result.ToString(), out var d2) && d2 > 0)
                                        {
                                            diameterMm = d2;
                                            return true;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            return false;
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
