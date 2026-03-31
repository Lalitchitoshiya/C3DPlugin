#nullable disable
using System.Globalization;

namespace C3DPlugin
{
    /// <summary>
    /// Full 30-column WSPro CSV pipe record.
    /// Column order matches the WSPro export format exactly.
    /// </summary>
    public class WsproCsvRecord
    {
        // Geometry / Hydraulic
        public string UsIl { get; set; } = "";           // US_IL
        public string DsIl { get; set; } = "";           // DS_IL
        public string Diameter { get; set; } = "";       // DIAMETER (mm)
        public string Material { get; set; } = "";       // MATERIAL
        public string PresClass { get; set; } = "";      // PRES_CLASS

        // Simulation results
        public string MaxPres { get; set; } = "";        // MAX_PRES
        public string MaxVel { get; set; } = "";         // MAX_VEL
        public string SurgeMax { get; set; } = "";       // SURGE_MAX
        public string SurgeMin { get; set; } = "";       // SURGE_MIN

        // Connectivity
        public string UsId { get; set; } = "";           // US_ID
        public string DsId { get; set; } = "";           // DS_ID

        // Elevations
        public string ElevationUs { get; set; } = "";    // ELEVATION_US
        public string ElevationDs { get; set; } = "";    // ELEVATION_DS

        // Pipe properties
        public string Roughness { get; set; } = "";      // ROUGHNESS
        public string Length { get; set; } = "";          // LENGTH

        // Coordinates
        public string UsX { get; set; } = "";            // US_X
        public string UsY { get; set; } = "";            // US_Y
        public string DsX { get; set; } = "";            // DS_X
        public string DsY { get; set; } = "";            // DS_Y

        // Geometry
        public string Vertices { get; set; } = "";       // VERTICES ("x1 y1|x2 y2")

        // Identifiers
        public string PipeId { get; set; } = "";         // PIPE_ID

        // Metadata
        public string SystemType { get; set; } = "";     // SYSTEM_TYPE
        public string PipeStatus { get; set; } = "";     // PIPE_STATUS
        public string Lining { get; set; } = "";         // LINING
        public string JointType { get; set; } = "";      // JOINT_TYPE
        public string InstallYear { get; set; } = "";    // INSTALL_YEAR

        // Flags
        public string VelocityFlag { get; set; } = "";   // VELOCITY_FLAG
        public string SurgeFlag { get; set; } = "";      // SURGE_FLAG

        // Round-trip
        public string Civil3dPnClass { get; set; } = ""; // CIVIL3D_PN_CLASS
        public string Notes { get; set; } = "";           // NOTES

        /// <summary>
        /// Column headers in exact WSPro export order.
        /// </summary>
        public static readonly string[] Headers =
        {
            "US_IL", "DS_IL", "DIAMETER", "MATERIAL", "PRES_CLASS",
            "MAX_PRES", "MAX_VEL", "SURGE_MAX", "SURGE_MIN",
            "US_ID", "DS_ID", "ELEVATION_US", "ELEVATION_DS",
            "ROUGHNESS", "LENGTH", "US_X", "US_Y", "DS_X", "DS_Y",
            "VERTICES", "PIPE_ID", "SYSTEM_TYPE", "PIPE_STATUS",
            "LINING", "JOINT_TYPE", "INSTALL_YEAR",
            "VELOCITY_FLAG", "SURGE_FLAG", "CIVIL3D_PN_CLASS", "NOTES"
        };

        /// <summary>
        /// Returns all field values in exact WSPro column order.
        /// </summary>
        public string[] ToRow()
        {
            return new[]
            {
                UsIl, DsIl, Diameter, Material, PresClass,
                MaxPres, MaxVel, SurgeMax, SurgeMin,
                UsId, DsId, ElevationUs, ElevationDs,
                Roughness, Length, UsX, UsY, DsX, DsY,
                Vertices, PipeId, SystemType, PipeStatus,
                Lining, JointType, InstallYear,
                VelocityFlag, SurgeFlag, Civil3dPnClass, Notes
            };
        }

        /// <summary>
        /// Format a double for CSV output. Returns empty string for zero/NaN.
        /// </summary>
        public static string Fmt(double value, int decimals = 2)
        {
            if (double.IsNaN(value)) return "";
            return value.ToString($"F{decimals}", CultureInfo.InvariantCulture);
        }

        public static string FmtCoord(double value)
        {
            return value.ToString("F4", CultureInfo.InvariantCulture);
        }
    }
}
