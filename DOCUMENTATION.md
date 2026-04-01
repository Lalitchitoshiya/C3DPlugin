# Civil 3D / InfoWorks WSPro Integration Plugin

> Bi-directional data exchange between Autodesk Civil 3D 2026 and Innovyze InfoWorks WSPro for pressure pipe network design and hydraulic simulation.

---

## Table of Contents

- [Overview](#overview)
- [Workflow](#workflow)
- [Commands Reference](#commands-reference)
- [Architecture](#architecture)
- [Module Reference](#module-reference)
- [Data Flow](#data-flow)
- [File Formats](#file-formats)
- [API Details](#api-details)
- [Configuration & Setup](#configuration--setup)
- [Troubleshooting](#troubleshooting)
- [Project Structure](#project-structure)

---

## Overview

This plugin enables engineers to seamlessly exchange pressure pipe network data between **Autodesk Civil 3D** and **InfoWorks WSPro** hydraulic simulation software. It supports:

- **Export** Civil 3D pressure networks to WSPro CSV and EPANET (.inp) formats
- **Import** WSPro simulation results (surge, pressure, velocity) into Civil 3D as native pressure pipe objects with fittings
- **Property Sets** that display WSPro simulation results directly in the Civil 3D Properties palette
- **Round-trip** preservation of simulation data across export/import cycles

| Metric | Value |
|--------|-------|
| Total Lines of Code | ~5,100 |
| Source Files | 11 C# files |
| Commands | 4 WSPro commands |
| Target Platform | AutoCAD Civil 3D 2026 (x64) |
| Framework | .NET 8.0 Windows |

---

## Workflow

```
 CIVIL 3D                                             INFOWORKS WSPRO
 --------                                             ---------------
                                                      
 [1] Create/Open                                      
     Pressure Network                                 
         |                                            
         v                                            
 [2] WSPRO_EXPORT_PRESSURE  ------>  CSV + EPANET     
                                         |            
                                         v            
                                    [3] Import into   
                                        WSPro model   
                                         |            
                                         v            
                                    [4] Run simulation
                                        (surge, pressure,
                                         velocity)    
                                         |            
                                         v            
                                    [5] Export results 
                                        to CSV        
                                         |            
 [6] WSPRO_IMPORT_PRESSURE  <------     CSV           
         |                                            
         v                                            
 [7] Pipes + Fittings +                               
     Simulation Results                               
     in Properties Palette                            
```

| Step | Action | Tool |
|------|--------|------|
| 1 | Create pressure pipe network | Civil 3D (manual or CREATEPRESSURENETWORK) |
| 2 | Export to CSV + EPANET | **Plugin** (WSPRO_EXPORT_PRESSURE) |
| 3 | Import into WSPro | Manual (InfoWorks WSPro) |
| 4 | Run hydraulic simulation | Manual (InfoWorks WSPro) |
| 5 | Export simulation results | Manual (InfoWorks WSPro) |
| 6 | Import results to Civil 3D | **Plugin** (WSPRO_IMPORT_PRESSURE) |
| 7 | View results in Properties palette | Civil 3D (click any pipe) |

---

## Commands Reference

### WSPRO_IMPORT_PRESSURE

Imports a WSPro pressure pipe network from CSV files into Civil 3D.

```
Command: WSPRO_IMPORT_PRESSURE
  Select WSPro Nodes CSV (cssv4.CSV): [file dialog]
  Select WSPro Pipes CSV (PIPES_EXPORT.CSV): [file dialog]
```

**What it does:**
1. Reads node coordinates (X, Y, Z) from nodes CSV
2. Reads pipe connectivity, diameter, material from pipes CSV
3. Reads all 30 WSPro columns for PropertySet data
4. Creates or reuses a `PressurePipeNetwork` in the drawing
5. Creates pressure pipes via `PressurePipeNetwork.AddLinePipe()`
6. Auto-places fittings (Tees, Elbows, Crosses) at junction nodes
7. Rotates fittings to align with connecting pipe directions
8. Attaches "WSPro Simulation Results" PropertySet to each pipe

**Output:**
```
Read 92 nodes and 117 pipes from WSPro CSVs.
Created property set definition: 'WSPro Simulation Results' with 15 properties.
Created 61 pressure pipes in network 'WSPro_Import'.
Created 41 fittings at junction nodes.
```

---

### WSPRO_EXPORT_PRESSURE

Exports a Civil 3D pressure pipe network to WSPro CSV and EPANET formats.

```
Command: WSPRO_EXPORT_PRESSURE
  Enter output folder path (or press Enter for drawing folder): [path]
```

**What it does:**
1. Finds the first pressure pipe network in the drawing
2. Reads all pipes: start/end points, diameter, length, material
3. Reads all fittings: positions for node ID generation
4. Generates unique node IDs via spatial clustering
5. Reads PropertySet values (simulation data from previous imports)
6. Writes `WSPro_Export.csv` (30-column WSPro format)
7. Writes `WSPro_Export.inp` (EPANET format)

**Output files:**
| File | Format | Purpose |
|------|--------|---------|
| `WSPro_Export.csv` | WSPro CSV (30 columns) | Import into InfoWorks WSPro |
| `WSPro_Export.inp` | EPANET input file | Alternative hydraulic analysis |

---

### WSPRO_DIAG_PARTS

Diagnostic tool to inspect the pressure parts list configuration.

```
Command: WSPRO_DIAG_PARTS
```

**Shows:**
- Parts list ID and type
- Available pipe sizes (nominal diameters)
- Available fitting parts (Tees, Elbows, Crosses)
- Catalog types in AeccPressurePipesMgd
- Catalog file path on disk

---

### WSPRO_DIAG_PRESSURE

Diagnostic tool to inspect the Civil 3D pressure pipe API surface.

```
Command: WSPRO_DIAG_PRESSURE
```

**Shows:**
- CivilDocument runtime type
- AeccPressurePipesMgd assembly version
- Pressure-related properties on CivilDocument
- Extension types and methods
- PressurePipeNetwork.Create() overloads

---

## Architecture

```
+------------------------------------------------------------------+
|                      C3DPlugin Assembly                            |
|                                                                    |
|  +------------------+                                              |
|  |   myCommands.cs  |  Command entry points                       |
|  +--------+---------+                                              |
|           |                                                        |
|     +-----+------+                                                 |
|     |            |                                                 |
|     v            v                                                 |
|  +--------+  +----------+                                          |
|  | IMPORT |  |  EXPORT   |                                         |
|  +--------+  +----------+                                          |
|  |Importer|  |Exporter   |                                         |
|  |CsvReader| |NetReader  |                                         |
|  |        |  |CsvWriter  |                                         |
|  |        |  |EpanetWriter|                                        |
|  +---+----+  +-----+-----+                                        |
|      |              |                                              |
|      v              v                                              |
|  +---------------------------------------+                        |
|  |          SHARED SERVICES               |                        |
|  |  PropertyExtractor  (reflection)       |                        |
|  |  PropertySetManager (OPM props)        |                        |
|  |  WsproCsvRecord     (30-col model)     |                        |
|  +---------------------------------------+                        |
+------------------------------------------------------------------+
         |                           |
         v                           v
  Civil 3D Drawing              Output Files
  (PressurePipeNetwork,         (CSV, EPANET .inp)
   Pipes, Fittings,
   PropertySets)
```

---

## Module Reference

### WsproImporter.cs (2,719 lines)

The largest module. Handles the complete import pipeline.

**Public Methods:**

| Method | Purpose |
|--------|---------|
| `ImportNetwork(ed, nodesCsv, pipesCsv)` | Main import orchestrator |
| `DiagnosePressureApi(ed)` | API surface diagnostic |
| `DiagnosePartsList(ed)` | Parts list diagnostic |

**Key Internal Operations:**

| Operation | Method | Description |
|-----------|--------|-------------|
| Parts List Discovery | `TryGetFirstPartsListId()` | Multi-strategy: existing network, extensions, CivilDocument scan |
| Network Creation | `TryCreatePressureNetwork()` | Reflection-based with argument matching |
| Pipe Creation | `TryCreatePipe()` | `AddLinePipe(LineSegment3d, PressurePartSize)` with fallback |
| Fitting Placement | `TryAddFitting()` | `AddFitting(Point3d, PressurePartSize)` at junction nodes |
| Fitting Rotation | `TryRotateFitting()` | Aligns fitting with pipe directions via vector math |
| Fitting Auto-Add | `TryAutoAddFittingsFromCatalog()` | 4-strategy catalog discovery and part addition |
| Diameter Matching | `TryFindPipePartSize()` | Fuzzy matching with unit conversion (mm/inches/meters) |
| Fitting Selection | `TryFindFittingPartSize()` | Matches by connection count + diameter + keyword |

**Fitting Placement Logic:**

| Connections | Fitting Type | Selection Keywords |
|-------------|-------------|-------------------|
| 2 pipes | Elbow/Bend | "elbow", "bend", "45", "90" |
| 3 pipes | Tee | "tee", "reducing tee", "t-piece" |
| 4+ pipes | Cross | "cross" |

**Fitting Rotation Algorithm:**
- Elbow (2 pipes): align with first pipe direction
- Tee (3 pipes): find most-opposite pair (run), third is branch
- Cross (4+): find most-opposite pair for primary axis

---

### NetworkReader.cs (465 lines)

Reads Civil 3D pressure pipe networks for export.

**Public Methods:**

| Method | Purpose |
|--------|---------|
| `ReadNetwork(ed, tr, out pipeCount, out fittingCount)` | Returns `List<WsproCsvRecord>` |

**Node ID Generation Algorithm:**
1. Collect all pipe endpoints + fitting positions
2. Spatial clustering with 0.01 unit tolerance (2D distance)
3. Points within tolerance = same node
4. Assign sequential integer IDs starting at 1
5. PIPE_ID = `"{US_ID}_{DS_ID}"`

**Unit Conversion:**
- Civil 3D stores diameter in drawing units (meters for metric templates)
- Values < 10 are treated as meters and converted to mm (x1000)

---

### PropertySetManager.cs (621 lines)

Manages AEC Property Sets for displaying WSPro simulation results in the Properties palette.

**Public Methods:**

| Method | Purpose |
|--------|---------|
| `EnsureDefinition(db, tr, ed)` | Creates PropertySet definition if missing |
| `AttachAndPopulate(entityId, defId, tr, record)` | Attaches PropertySet to pipe + writes values |
| `ReadValues(entityId, tr, record)` | Reads PropertySet values for export round-trip |

**Property Set: "WSPro Simulation Results" (15 properties)**

| Property | Type | CSV Column |
|----------|------|------------|
| Pipe ID | Text | PIPE_ID |
| Max Pressure | Real | MAX_PRES |
| Max Velocity | Real | MAX_VEL |
| Surge Max | Real | SURGE_MAX |
| Surge Min | Real | SURGE_MIN |
| Pressure Class | Text | PRES_CLASS |
| Roughness | Real | ROUGHNESS |
| Velocity Flag | Text | VELOCITY_FLAG |
| Surge Flag | Text | SURGE_FLAG |
| PN Class | Text | CIVIL3D_PN_CLASS |
| System Type | Text | SYSTEM_TYPE |
| Lining | Text | LINING |
| Joint Type | Text | JOINT_TYPE |
| Install Year | Text | INSTALL_YEAR |
| Notes | Text | NOTES |

**AEC API Used:**
- `AecPropDataMgd.dll` (loaded explicitly at runtime)
- `PropertySetDefinition` - definition stored in `AEC_PROPERTY_SET_DEFS` dictionary
- `PropertyDefinition` - individual property with DataType (Real/Text)
- `PropertyDataServices.AddPropertySet()` - attaches to entity
- `PropertyDataServices.GetPropertySets()` - retrieves attached sets
- `PropertySet.SetAt(index, value)` / `PropertySet.GetAt(index)` - read/write values

---

### PropertyExtractor.cs (451 lines)

Shared reflection utilities for reading Civil 3D entity properties.

**Public Methods:**

| Method | Purpose |
|--------|---------|
| `TryGetPoint3d(obj, name, out result)` | Read Point3d property |
| `TryGetDouble(obj, name, out result)` | Read numeric property |
| `TryGetString(obj, name, out result)` | Read string property |
| `TryGetStartPoint(pipe, out result)` | Pipe start point (multiple fallbacks) |
| `TryGetEndPoint(pipe, out result)` | Pipe end point (multiple fallbacks) |
| `TryGetDiameter(pipe, tr, out diameterMm)` | Diameter extraction with string parsing |
| `TryGetLength(pipe, out length)` | Length extraction or computation |
| `GetMaterialCode(pipe, tr)` | Material name to WSPro code mapping |
| `TryGetPosition(obj, out result)` | Entity position (for fittings) |
| `DiagnoseDiameter(pipe, tr, ed)` | Diagnostic dump of diameter properties |

**Diameter Extraction Chain:**
1. Direct double properties: `InnerDiameter`, `NominalDiameter`, `OuterDiameter`, etc.
2. PartSize object properties
3. `PartSize.GetProperty(PressurePartContextType.DiameterNominal)`
4. Parse from string: `"150 mm x 150 mm"` -> `150`
5. Parse from Description: `"pipe-150 mm-push on-ductile..."` -> `150`
6. Brute force: scan all properties with "diam" in name

**String Diameter Parser** (`ParseDiameterFromString`):
- `"150 mm x 150 mm"` -> `150` (regex: `(\d+\.?\d*)\s*mm`)
- `"DN300"` -> `300` (regex: `DN\s*(\d+\.?\d*)`)
- `"pipe-150 mm-push on"` -> `150` (first number before "mm")

**Material Code Mapping:**

| Civil 3D Description | WSPro Code |
|---------------------|------------|
| Ductile Iron, DI | DI |
| PVC | PVC |
| HDPE, Polyethylene, PE | PE |
| Steel | Steel |
| Concrete | Concrete |
| Cast Iron, CI | CI |

---

### WsproCsvRecord.cs (109 lines)

Data model representing all 30 columns of the WSPro CSV format.

**CSV Column Order (exact WSPro import format):**

| # | Header | Property | Description |
|---|--------|----------|-------------|
| 1 | US Invert Level | UsIl | Upstream invert level |
| 2 | DS Invert Level | DsIl | Downstream invert level |
| 3 | Diameter | Diameter | Pipe diameter (mm) |
| 4 | Material | Material | Material code |
| 5 | Pressure Class | PresClass | PN rating |
| 6 | Max Pressure | MaxPres | Maximum pressure (bar) |
| 7 | Max Velocity | MaxVel | Maximum velocity (m/s) |
| 8 | Surge Max | SurgeMax | Maximum surge pressure |
| 9 | Surge Min | SurgeMin | Minimum surge pressure |
| 10 | From Node ID | UsId | Upstream node |
| 11 | To Node ID | DsId | Downstream node |
| 12 | Elevation US | ElevationUs | Upstream elevation |
| 13 | Elevation DS | ElevationDs | Downstream elevation |
| 14 | Roughness | Roughness | Roughness coefficient |
| 15 | Length | Length | Pipe length |
| 16 | US X | UsX | Upstream X coordinate |
| 17 | US Y | UsY | Upstream Y coordinate |
| 18 | DS X | DsX | Downstream X coordinate |
| 19 | DS Y | DsY | Downstream Y coordinate |
| 20 | Vertices | Vertices | Pipe vertex string |
| 21 | Suffix | PipeId | Pipe suffix identifier |
| 22 | System Type | SystemType | System classification |
| 23 | Pipe Status | PipeStatus | Open/closed status |
| 24 | Lining | Lining | Pipe lining type |
| 25 | Joint Type | JointType | Joint type |
| 26 | Install Year | InstallYear | Installation year |
| 27 | Velocity Flag | VelocityFlag | Velocity check result |
| 28 | Surge Flag | SurgeFlag | Surge check result |
| 29 | PN Class | Civil3dPnClass | Civil 3D PN classification |
| 30 | Notes | Notes | Additional notes |

---

### CsvWriter.cs (34 lines)

Writes WSPro-format CSV with double-quoted values.

**Format:** All values wrapped in double quotes, comma-separated, UTF-8 encoding.

---

### EpanetWriter.cs (97 lines)

Generates standard EPANET 2.0 input files.

**Sections generated:**

| Section | Content |
|---------|---------|
| [TITLE] | "Exported from Civil 3D via WSPro Plugin" |
| [JUNCTIONS] | Node ID, Elevation, Demand (0.0) |
| [PIPES] | Pipe ID, Node1, Node2, Length, Diameter, Roughness, MinorLoss, Status |
| [COORDINATES] | Node ID, X-Coord, Y-Coord |
| [OPTIONS] | Units: LPS, Headloss: H-W |
| [TIMES] | Duration: 0:00 |
| [END] | End marker |

**Defaults:**
- Units: LPS (Liters Per Second)
- Headloss formula: Hazen-Williams
- Default roughness: C = 130 (if not specified)
- Demand: 0.0 (user sets in EPANET/WSPro)

---

## Data Flow

### Import Flow (WSPro CSV -> Civil 3D)

```
Nodes CSV -----> WsproCsvReader.ReadNodes()
                      |
                      v
                 List<WsproNode>
                      |                    Pipes CSV -----> WsproCsvReader.ReadPipes()
                      |                                          |
                      |                                          v
                      |                                    List<WsproPipe>
                      |                                          |
                      +------------------+-----------------------+
                                         |
                                         v
                                  WsproImporter.ImportNetwork()
                                         |
                      +------------------+------------------+
                      |                  |                  |
                      v                  v                  v
               TryCreatePipe()    TryAddFitting()    PropertySetManager
               (AddLinePipe)      (AddFitting)        .AttachAndPopulate()
                      |                  |                  |
                      v                  v                  v
               PressurePipe       PressureFitting    PropertySet
               objects            objects             on each pipe
```

### Export Flow (Civil 3D -> CSV + EPANET)

```
Civil 3D Drawing
       |
       v
NetworkReader.ReadNetwork()
       |
       +-----> Read PipeIds from PressurePipeNetwork
       |       Extract: StartPoint, EndPoint, Diameter, Length, Material
       |
       +-----> Read FittingIds
       |       Extract: Positions for node clustering
       |
       +-----> PropertySetManager.ReadValues()
       |       Read: simulation data from previous imports
       |
       +-----> BuildNodeMap()
       |       Spatial clustering -> sequential integer IDs
       |
       v
List<WsproCsvRecord>
       |
       +-----> CsvWriter.Write() --------> WSPro_Export.csv
       |
       +-----> EpanetWriter.Write() -----> WSPro_Export.inp
```

---

## File Formats

### WSPro CSV Import Format

**Filename convention:** File must end with table name for WSPro import:
- `*_wn_pipe.csv` - Pipe data table
- `*_wn_node.csv` - Node data table

**Key fields for WSPro import:**
- `From Node ID`, `To Node ID`, `Suffix` - Pipe key (required)
- `Node ID` - Node key (required)

### WSPro CSV Export Format (from WSPro)

Different column names than import:
- `US_ID`, `DS_ID`, `PIPE_ID` - Pipe identifiers
- `ASSET_ID` - Node identifier

The plugin handles both naming conventions in its CSV reader.

### EPANET .inp Format

Standard EPANET 2.0 text input format. Compatible with:
- EPANET 2.2
- InfoWorks WSPro (EPANET import)
- WaterGEMS / WaterCAD
- Other hydraulic modeling software

---

## API Details

### Civil 3D Pressure Pipe API (via Reflection)

The plugin uses runtime reflection because the pressure pipe API differs across Civil 3D versions.

**Key Types (from AeccPressurePipesMgd.dll):**

| Type | Purpose |
|------|---------|
| `PressurePipeNetwork` | Network container |
| `PressurePipe` | Individual pipe entity |
| `PressureFitting` | Fitting at junction (Tee, Elbow, Cross) |
| `PressurePartList` | Available pipe/fitting sizes |
| `PressurePartSize` | Specific part configuration |
| `PressurePartType` | Enum: PressurePipe=0, Fitting=1, Appurtenance=2 |
| `PressurePartContextType` | Enum for part property access (DiameterNominal, etc.) |

**Key API Calls:**

```
// Network discovery
CivilDocumentPressurePipesExtension.GetPressurePipeNetworkIds(CivilDocument)

// Parts list access
StylesRootPressurePipesExtension.GetPressurePartLists(StylesRoot)

// Network creation
PressurePipeNetwork.Create(Database, String)

// Pipe creation
PressurePipeNetwork.AddLinePipe(LineSegment3d, PressurePartSize)

// Fitting creation
PressurePipeNetwork.AddFitting(Point3d, PressurePartSize)

// Part size diameter
PressurePartSize.GetProperty(PressurePartContextType.DiameterNominal)
```

### AEC Property Set API (via Reflection)

**Key Types (from AecPropDataMgd.dll):**

| Type | Purpose |
|------|---------|
| `PropertySetDefinition` | Schema definition (stored in AEC_PROPERTY_SET_DEFS) |
| `PropertyDefinition` | Individual property schema |
| `PropertyDefinitionCollection` | Collection within a definition |
| `PropertySet` | Instance attached to an entity |
| `PropertyDataServices` | Static utility for attach/retrieve |
| `DataType` | Enum: Real, Text, Integer, TrueFalse |

**Key API Calls:**

```
// Attach property set to entity
PropertyDataServices.AddPropertySet(DBObject entity, ObjectId definitionId)

// Get attached property sets
PropertyDataServices.GetPropertySets(DBObject entity) -> ObjectIdCollection

// Read/write property values
PropertySet.PropertyNameToId(string name) -> int index
PropertySet.SetAt(int index, object value)
PropertySet.GetAt(int index) -> object
```

**Important:** `AecPropDataMgd.dll` is not auto-loaded by Civil 3D. The plugin explicitly loads it from:
```
C:\Program Files\Autodesk\AutoCAD 2026\ACA\AecPropDataMgd.dll
```

---

## Configuration & Setup

### Prerequisites

- Autodesk Civil 3D 2026 (x64)
- .NET 8.0 Runtime
- Pressure pipe parts list with pipe AND fitting families configured

### Building

```bash
cd D:\Plugin
dotnet build C3DPlugin/C3DPlugin.csproj -p:Platform=x64
```

**Output:** `D:\Plugin\C3DPlugin\bin\x64\Debug\net8.0-windows\C3DPlugin.dll`

### Loading in Civil 3D

```
Command: NETLOAD
Browse to: D:\Plugin\C3DPlugin\bin\x64\Debug\net8.0-windows\C3DPlugin.dll
```

**Note:** Civil 3D locks the DLL while loaded. To update:
1. Close Civil 3D completely
2. Rebuild (`dotnet build`)
3. Reopen Civil 3D
4. NETLOAD again

### Parts List Setup

For fitting import to work, the parts list must include fitting families:

1. Toolspace > Settings > Pressure Network > Parts Lists
2. Edit your parts list > Information tab > Load new catalog:
   ```
   C:\ProgramData\Autodesk\C3D 2026\enu\Pressure Pipes Catalog\Metric\Metric_Ductile_Iron.sqlite
   ```
3. Fittings tab > right-click > Add Part Family > add all
4. Ensure pipe sizes match your data (e.g., 150, 200, 250, 300, 400mm)

---

## Troubleshooting

### "AEC Property Set API not available"
**Cause:** AecPropDataMgd.dll not loaded.
**Fix:** Ensure Civil 3D (not plain AutoCAD) is running. The plugin auto-loads from the ACA folder.

### Diameter shows 0 in export
**Cause:** Civil 3D stores diameter in meters (e.g., 0.15), format rounds to 0.
**Fix:** Fixed in code - values < 10 are multiplied by 1000 to convert to mm.

### "No fitting parts in current parts list"
**Cause:** Parts list only has pipe families, no fitting families.
**Fix:** Load Metric_Ductile_Iron catalog and add fitting families.

### Skipped pipes due to no matching part size
**Cause:** CSV diameter doesn't match any size in the parts list.
**Fix:** Add missing pipe sizes to the parts list, or run WSPRO_DIAG_PARTS to see available sizes.

### DLL not updating after rebuild
**Cause:** Civil 3D locks the DLL file.
**Fix:** Close Civil 3D completely before rebuilding.

### Build produces two DLLs
**Cause:** Different platform configurations.
**Fix:** Always build with `-p:Platform=x64` and load from `bin\x64\Debug\`.

---

## Project Structure

```
D:\Plugin\
|
|-- C3DPlugin.slnx                    Solution file
|-- Pipe_Export_2.csv                  Sample WSPro export (test data)
|-- DOCUMENTATION.md                  This file
|
+-- C3DPlugin/
    |-- C3DPlugin.csproj              Project configuration
    |
    |-- myCommands.cs                 [134 lines]  Command definitions
    |-- PluginExtension.cs            [ 22 lines]  Plugin lifecycle
    |
    |-- WsproModels.cs                [297 lines]  Data models + CSV reader
    |-- WsproCsvRecord.cs             [109 lines]  30-column CSV model
    |
    |-- WsproImporter.cs             [2719 lines]  Import orchestrator
    |-- WsproExporter.cs              [ 75 lines]  Export orchestrator
    |-- NetworkReader.cs              [465 lines]  C3D network reader
    |
    |-- CsvWriter.cs                  [ 34 lines]  WSPro CSV writer
    |-- EpanetWriter.cs               [ 97 lines]  EPANET .inp writer
    |
    |-- PropertyExtractor.cs          [451 lines]  Reflection property helpers
    |-- PropertySetManager.cs         [621 lines]  AEC PropertySet manager
    |
    +-- bin\x64\Debug\net8.0-windows\
        +-- C3DPlugin.dll             Built plugin DLL
```

### Assembly References

| Reference | Source | Purpose |
|-----------|--------|---------|
| AutoCAD.NET v25.1.0 | NuGet | AutoCAD core API |
| acdbmgd.dll | AutoCAD 2026 | Database services |
| acmgd.dll | AutoCAD 2026 | Application services |
| AeccDbMgd.dll | Civil 3D | Civil 3D database |
| AeccPressurePipesMgd.dll | Civil 3D | Pressure pipe API |
| AecBaseMgd.dll | ACA | AEC base classes |
| AecPropDataMgd.dll | ACA | Property Set API |
| CaMgdContentData.dll | Civil 3D | Content catalog data |

---

*Built for Autodesk Civil 3D 2026 with InfoWorks WSPro integration.*
