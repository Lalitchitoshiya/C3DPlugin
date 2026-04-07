# Civil 3D / InfoWorks WSPro Integration Plugin

> Bi-directional data exchange between Autodesk Civil 3D 2026 and Innovyze InfoWorks WSPro for pressure pipe network design and hydraulic simulation.

**Branch:** `Fitting_Correction`
**Last Updated:** April 2026

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
- [Fitting Logic](#fitting-logic)
- [Configuration & Setup](#configuration--setup)
- [Troubleshooting](#troubleshooting)
- [Project Structure](#project-structure)
- [Commit History](#commit-history)

---

## Overview

This plugin enables engineers to seamlessly exchange pressure pipe network data between **Autodesk Civil 3D** and **InfoWorks WSPro** hydraulic simulation software. It supports:

- **Export** Civil 3D pressure networks to WSPro CSV and EPANET (.inp) formats
- **Import** WSPro simulation results (surge, pressure, velocity) into Civil 3D as native pressure pipe objects with fittings
- **Property Sets** that display WSPro simulation results directly in the Civil 3D Properties palette
- **Conditional fitting placement** — creates fittings on fresh imports, skips on re-imports to avoid duplicates
- **Fitting-based node generation** — uses fitting center positions as EPANET nodes for continuous network topology
- **Round-trip** preservation of simulation data across export/import cycles

| Metric | Value |
|--------|-------|
| Total Lines of Code | 5,260 |
| Source Files | 12 C# files |
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
| 3 | Import into WSPro | Manual (InfoWorks WSPro — EPANET import) |
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
6. **Fresh import only:** auto-places fittings (Tees, Elbows, Crosses) at junction nodes
7. **Re-import:** skips fitting placement (uses existing fittings)
8. Attaches "WSPro Simulation Results" PropertySet to each pipe with simulation data

**Output examples:**

Fresh import:
```
Created 61 pressure pipes in network 'WSPro_Import'.
Created 41 fittings at junction nodes.
```

Re-import (existing network):
```
Created 61 pressure pipes in network 'WSPro_Import'.
Skipping fitting placement — using existing fittings from network.
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
3. Reads pipe `StartFittingId`/`EndFittingId` to determine junction nodes
4. **Fitting positions become EPANET nodes** — ensures continuous network topology (no gaps)
5. Dead-end pipe endpoints become nodes via spatial clustering
6. Reads PropertySet values (simulation data from previous imports)
7. Converts diameter from meters to mm automatically
8. Writes WSPro CSV and EPANET .inp files

**Output files:**
| File | Format | Purpose |
|------|--------|---------|
| `WSPro_Export.csv` | WSPro CSV (30 columns) | Import into InfoWorks WSPro |
| `WSPro_Export.inp` | EPANET input file | Universal hydraulic analysis format |

**Node generation logic:**
```
Fitting exists at pipe end  → use fitting center position as node (shared by all pipes)
No fitting (dead end)       → use pipe endpoint as node
```

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
| Network Detection | `TryGetExistingPressureNetwork()` | Returns existing network + sets `isNewNetwork = false` |
| Network Creation | `TryCreatePressureNetwork()` | Creates new network + sets `isNewNetwork = true` |
| Pipe Creation | `TryCreatePipe()` | `AddLinePipe(LineSegment3d, PressurePartSize)` with fallback |
| Fitting Placement | `TryAddFitting()` | Conditional: runs only when `isNewNetwork == true` |
| Fitting Rotation | `TryRotateFitting()` | Aligns fitting with pipe directions via vector math |
| Fitting Auto-Add | `TryAutoAddFittingsFromCatalog()` | 4-strategy catalog discovery and part addition |
| Diameter Matching | `TryFindPipePartSize()` | Fuzzy matching with unit conversion (mm/inches/meters) |
| Fitting Selection | `TryFindFittingPartSize()` | Matches by connection count + diameter + keyword |
| PropertySet | `PropertySetManager.AttachAndPopulate()` | Attaches simulation data to each pipe |

---

### NetworkReader.cs (586 lines)

Reads Civil 3D pressure pipe networks for export. Uses fitting-based node generation.

**Public Methods:**

| Method | Purpose |
|--------|---------|
| `ReadNetwork(ed, tr, out pipeCount, out fittingCount, out exportNodes)` | Returns `List<WsproCsvRecord>` + node list |

**Node ID Generation (Fitting-Based):**
1. For each pipe, read `StartFittingId` / `EndFittingId` properties
2. Open each fitting → get `Position` (center point) → assign sequential node ID
3. All pipes at the same fitting share the **exact same node** — no gaps
4. Dead-end pipe endpoints (no fitting) → create orphan node via spatial clustering (0.01 tolerance)
5. PIPE_ID = `"1"` (suffix for WSPro key format)

**Unit Conversion:**
- Civil 3D stores diameter in drawing units (meters for metric templates)
- Values < 10 are treated as meters and multiplied by 1000 → mm
- `Length2DCenterToCenter` preferred for EPANET-correct pipe length

**Diameter Extraction Chain:**
1. Direct double properties: `InnerDiameter`, `NominalDiameter`, `OuterDiameter`
2. PartSize object properties
3. `PartSize.GetProperty(PressurePartContextType.DiameterNominal)` → parses string `"150 mm x 150 mm"` → 150
4. Parse from `Description`: `"pipe-150 mm-push on-ductile..."` → 150
5. Brute force: scan all properties with "diam" in name

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

**Runtime:** `AecPropDataMgd.dll` is explicitly loaded at runtime from `C:\Program Files\Autodesk\AutoCAD 2026\ACA\` since Civil 3D doesn't auto-load it.

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

**String Diameter Parser** (`ParseDiameterFromString`):
- `"150 mm x 150 mm"` → `150` (regex: `(\d+\.?\d*)\s*mm`)
- `"DN300"` → `300` (regex: `DN\s*(\d+\.?\d*)`)
- `"pipe-150 mm-push on"` → `150` (first number before "mm")

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

**CSV Column Order (WSPro import format):**

| # | Header | Property |
|---|--------|----------|
| 1 | US Invert Level | UsIl |
| 2 | DS Invert Level | DsIl |
| 3 | Diameter | Diameter |
| 4 | Material | Material |
| 5 | Pressure Class | PresClass |
| 6 | Max Pressure | MaxPres |
| 7 | Max Velocity | MaxVel |
| 8 | Surge Max | SurgeMax |
| 9 | Surge Min | SurgeMin |
| 10 | From Node ID | UsId |
| 11 | To Node ID | DsId |
| 12 | Elevation US | ElevationUs |
| 13 | Elevation DS | ElevationDs |
| 14 | Roughness | Roughness |
| 15 | Length | Length |
| 16 | US X | UsX |
| 17 | US Y | UsY |
| 18 | DS X | DsX |
| 19 | DS Y | DsY |
| 20 | Vertices | Vertices |
| 21 | Suffix | PipeId |
| 22 | System Type | SystemType |
| 23 | Pipe Status | PipeStatus |
| 24 | Lining | Lining |
| 25 | Joint Type | JointType |
| 26 | Install Year | InstallYear |
| 27 | Velocity Flag | VelocityFlag |
| 28 | Surge Flag | SurgeFlag |
| 29 | PN Class | Civil3dPnClass |
| 30 | Notes | Notes |

---

### CsvWriter.cs (34 lines)

Writes WSPro-format CSV with double-quoted values.

**Methods:**
- `WritePipes(path, records)` — 30-column pipe data
- `WriteNodes(path, nodes)` — Node ID, X, Y, Z, Type

**Filename convention for WSPro import:** `*_wn_pipe.csv`, `*_wn_node.csv`

---

### EpanetWriter.cs (97 lines)

Generates standard EPANET 2.0 input files.

**Sections:** TITLE, JUNCTIONS, PIPES, COORDINATES, OPTIONS, TIMES, END

**Defaults:** Units: LPS, Headloss: Hazen-Williams, Default roughness: C=130

---

### WsproExporter.cs (75 lines)

Export orchestrator — ties NetworkReader, CsvWriter, and EpanetWriter together.

---

## Data Flow

### Import Flow

```
Nodes CSV + Pipes CSV
    |
    v
WsproCsvReader.ReadNodes() + ReadPipes() + ReadFullRecords()
    |
    v
WsproImporter.ImportNetwork()
    |
    +---> isNewNetwork?
    |       |
    |      YES: TryCreatePressureNetwork() → AddLinePipe() → AddFitting() → PropertySet
    |       |
    |      NO:  TryGetExistingPressureNetwork() → AddLinePipe() → Skip Fittings → PropertySet
    |
    v
Civil 3D: PressurePipeNetwork + Pipes + Fittings + PropertySets
```

### Export Flow

```
Civil 3D Drawing
    |
    v
NetworkReader.ReadNetwork()
    |
    +---> Read pipes (StartPoint, EndPoint, Diameter, Length, Material)
    +---> Read StartFittingId/EndFittingId → fitting center = node position
    +---> Dead-end endpoints → orphan nodes
    +---> PropertySetManager.ReadValues() → simulation data
    |
    v
List<WsproCsvRecord> + List<ExportNode>
    |
    +---> CsvWriter.WritePipes() → WSPro_Export.csv
    +---> CsvWriter.WriteNodes() → WSPro_Export_wn_node.csv
    +---> EpanetWriter.Write() → WSPro_Export.inp
```

---

## Fitting Logic

### Conditional Fitting Placement

```csharp
bool isNewNetwork = false;
if (!TryGetExistingPressureNetwork(...))
{
    TryCreatePressureNetwork(...);
    isNewNetwork = true;  // fresh import
}

if (isNewNetwork)
    // Create fittings at junction nodes (Tees, Elbows, Crosses)
else
    // Skip — "using existing fittings from network"
```

| Scenario | `isNewNetwork` | Fitting Code | Result |
|----------|---------------|-------------|--------|
| Fresh import (blank drawing) | `true` | Runs | Fittings created at junctions |
| Re-import (existing network) | `false` | Skipped | Existing fittings preserved |

### Fitting Type Selection

| Connections at Node | Fitting Type | Selection Keywords |
|---------------------|-------------|-------------------|
| 2 pipes | Elbow/Bend | "elbow", "bend", "45", "90" |
| 3 pipes | Tee | "tee", "reducing tee", "t-piece" |
| 4+ pipes | Cross | "cross" |

### Fitting Rotation

After placement, fittings are rotated to align with connecting pipes:
- **Elbow:** Aligns with first pipe direction
- **Tee:** Finds two most-opposite pipes (run axis), third is branch
- **Cross:** Finds most-opposite pair for primary axis

### Fitting-Based Node Generation (Export)

In the export, fittings are used as EPANET nodes to ensure continuous topology:

```
Problem (old approach):
  Pipe1.EndPoint (10.001, 5.002)  → Node 5
  Pipe2.StartPoint (10.003, 4.998) → Node 6  ← GAP!

Solution (fitting-based):
  Pipe1.EndFitting.Position (10.002, 5.000)  → Node 5
  Pipe2.StartFitting.Position (10.002, 5.000) → Node 5  ← CONNECTED!
```

---

## File Formats

### WSPro CSV Import Format

**Filename convention:** `*_wn_pipe.csv` and `*_wn_node.csv` — WSPro recognizes the table from the filename suffix.

**Key fields:** `From Node ID`, `To Node ID`, `Suffix` (pipe key), `Node ID` (node key)

### WSPro CSV Export Format (from WSPro)

Different column names: `US_ID`, `DS_ID`, `PIPE_ID`, `ASSET_ID` — the plugin handles both naming conventions.

### EPANET .inp Format

Standard EPANET 2.0 text format. Compatible with: EPANET 2.2, InfoWorks WSPro, WaterGEMS, WaterCAD, and any EPANET-compatible software.

---

## API Details

### Civil 3D Pressure Pipe API (via Reflection)

| Type | Purpose |
|------|---------|
| `PressurePipeNetwork` | Network container |
| `PressurePipe` | Individual pipe entity |
| `PressureFitting` | Fitting at junction |
| `PressurePartList` | Available pipe/fitting sizes |
| `PressurePartSize` | Specific part configuration |
| `PressurePartType` | Enum: PressurePipe=0, Fitting=1, Appurtenance=2 |

**Key API Calls:**
```
CivilDocumentPressurePipesExtension.GetPressurePipeNetworkIds(CivilDocument)
StylesRootPressurePipesExtension.GetPressurePartLists(StylesRoot)
PressurePipeNetwork.Create(Database, String)
PressurePipeNetwork.AddLinePipe(LineSegment3d, PressurePartSize)
PressurePipeNetwork.AddFitting(Point3d, PressurePartSize)
PressurePartSize.GetProperty(PressurePartContextType.DiameterNominal)
```

### AEC Property Set API (via Reflection)

```
PropertyDataServices.AddPropertySet(DBObject entity, ObjectId definitionId)
PropertyDataServices.GetPropertySets(DBObject entity)
PropertySet.PropertyNameToId(string name) → int index
PropertySet.SetAt(int index, object value)
PropertySet.GetAt(int index) → object
```

**Important:** `AecPropDataMgd.dll` loaded explicitly from `C:\Program Files\Autodesk\AutoCAD 2026\ACA\AecPropDataMgd.dll`

### Key Pipe Properties (from diagnostic dump)

```
InnerDiameter = 0.15 (meters — multiply by 1000 for mm)
NominalDiameter = 0.15
OuterDiameter = 0.17
Length2DCenterToCenter = 20105.6
StartFittingId = (ObjectId)
EndFittingId = (ObjectId)
StartPoint = (x, y, z)
EndPoint = (x, y, z)
Description = "pipe-150 mm-push on-ductile iron-25 bar-AWWA C151"
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

**Note:** Civil 3D locks the DLL while loaded. To update: Close Civil 3D → Rebuild → Reopen → NETLOAD.

### Parts List Setup

1. Toolspace > Settings > Pressure Network > Parts Lists
2. Edit parts list > Information tab > Load catalog:
   ```
   C:\ProgramData\Autodesk\C3D 2026\enu\Pressure Pipes Catalog\Metric\Metric_Ductile_Iron.sqlite
   ```
3. Fittings tab > Add Part Family > add Tees, Elbows, Crosses
4. Pressure Pipes tab > ensure sizes match your data (150, 200, 250, 300, 400mm etc.)

---

## Troubleshooting

| Issue | Cause | Fix |
|-------|-------|-----|
| "AEC Property Set API not available" | AecPropDataMgd.dll not loaded | Plugin auto-loads from ACA folder. Ensure Civil 3D (not plain AutoCAD) |
| Diameter shows 0 in export | C3D stores in meters (0.15), format rounds to 0 | Fixed: values < 10 multiplied by 1000 |
| Network gaps in EPANET export | Pipe endpoints offset from fitting center | Fixed: fitting positions used as nodes |
| Duplicate fittings on re-import | Fitting code ran on existing network | Fixed: `isNewNetwork` flag skips fittings |
| "No fitting parts in parts list" | Parts list missing fitting families | Load Metric_Ductile_Iron catalog, add fitting families |
| Skipped pipes (no matching size) | CSV diameter not in parts list | Add missing pipe sizes to parts list |
| DLL not updating | Civil 3D locks the file | Close Civil 3D completely before rebuilding |
| Two DLLs with different sizes | Build config mismatch | Always use `-p:Platform=x64` and load from `bin\x64\Debug\` |
| WSPro CSV import fails | Wrong filename or column names | Use `_wn_pipe.csv` / `_wn_node.csv` suffix |

---

## Project Structure

```
D:\Plugin\
|-- C3DPlugin.slnx                    Solution file
|-- Pipe_Export_2.csv                  Sample WSPro export (test data)
|-- DOCUMENTATION.md                  This file
|-- PITCH_PRESENTATION.md             Pitch deck (18 slides)
|-- VIDEO_SCRIPT.md                   3-4 min video script
|
+-- C3DPlugin/
    |-- C3DPlugin.csproj              Project configuration
    |
    |-- myCommands.cs                 [  134 lines]  Command definitions
    |-- PluginExtension.cs            [   22 lines]  Plugin lifecycle
    |
    |-- WsproModels.cs                [  297 lines]  Data models + CSV reader
    |-- WsproCsvRecord.cs             [  109 lines]  30-column CSV model
    |
    |-- WsproImporter.cs             [2,719 lines]  Import orchestrator
    |-- WsproExporter.cs              [   75 lines]  Export orchestrator
    |-- NetworkReader.cs              [  586 lines]  C3D network reader (fitting-based nodes)
    |
    |-- CsvWriter.cs                  [   34 lines]  WSPro CSV writer
    |-- EpanetWriter.cs               [   97 lines]  EPANET .inp writer
    |
    |-- PropertyExtractor.cs          [  451 lines]  Reflection property helpers
    |-- PropertySetManager.cs         [  621 lines]  AEC PropertySet manager
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

## Commit History

| Commit | Description |
|--------|-------------|
| `7db2379` | Conditional fitting: fresh import creates fittings, re-import skips them |
| `002f1b1` | Fitting-based node generation: nodes at fitting centers for continuous EPANET topology |
| `6735c88` | Documentation updated |
| `2debecb` | Bug fix: diameter showing zero → now extracts from string "150 mm x 150 mm" and converts meters to mm |
| `8c53e88` | WSPro Simulation Results showing in Extended Property Palette (PropertySets) |
| `3e87f2d` | Exporter for CSV and EPANET for the pressure network |
| `5deb352` | Fitting between 2 pressure pipes added |
| `ea4e286` | First commit: native pressure pipe objects |

---

*Built for Autodesk Civil 3D 2026 with InfoWorks WSPro integration.*
*Branch: Fitting_Correction | Total: 5,260 lines of code*
