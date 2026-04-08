# Civil 3D / WSPro Plugin — Complete Workflow

---

## File Map

```
myCommands.cs ─────── Entry point (receives user command)
WsproModels.cs ────── CSV reader (text → C# objects)
WsproCsvRecord.cs ─── 30-column data container
WsproImporter.cs ──── Import brain (CSV → Civil 3D objects)
WsproExporter.cs ──── Export coordinator
NetworkReader.cs ──── Reads Civil 3D network (pipes, fittings → CSV records)
CsvWriter.cs ──────── Writes WSPro CSV files
EpanetWriter.cs ───── Writes EPANET .inp files
PropertyExtractor.cs ─ Reads pipe properties via reflection (diameter, length, etc.)
PropertySetManager.cs ─ Creates/reads/writes simulation data on pipes (Properties palette)
PluginExtension.cs ─── Plugin lifecycle (empty — required by AutoCAD)
```

---

## EXPORT FLOW (Civil 3D → WSPro/EPANET)

### Command: `WSPRO_EXPORT_PRESSURE`

```
USER types WSPRO_EXPORT_PRESSURE
│
▼
╔══════════════════════════════════════════════════════════════════╗
║  myCommands.cs → WsproExportPressure()                          ║
║                                                                  ║
║  • Prompts: "Enter output folder path"                          ║
║  • Calls: WsproExporter.ExportNetwork(ed, outputFolder)         ║
╚══════════════════════════════╦═══════════════════════════════════╝
                               │
                               ▼
╔══════════════════════════════════════════════════════════════════╗
║  WsproExporter.cs → ExportNetwork()                             ║
║                                                                  ║
║  • Gets active document                                         ║
║  • Creates output folder if needed                              ║
║  • Opens transaction (required for reading Civil 3D database)   ║
║  • Calls NetworkReader, CsvWriter, EpanetWriter                 ║
║  • Commits transaction                                          ║
╚══════════════════════════════╦═══════════════════════════════════╝
                               │
              ┌────────────────┼────────────────────┐
              │                │                    │
              ▼                ▼                    ▼
╔════════════════╗ ╔═══════════════════╗ ╔════════════════════╗
║ NetworkReader  ║ ║   CsvWriter.cs    ║ ║  EpanetWriter.cs   ║
║    .cs         ║ ║                   ║ ║                    ║
║ ReadNetwork()  ║ ║ Write()           ║ ║ Write()            ║
║                ║ ║ → WSPro_Export.csv║ ║ → WSPro_Export.inp ║
╚═══════╦════════╝ ╚═══════════════════╝ ╚════════════════════╝
        │
        │ INSIDE NetworkReader.ReadNetwork():
        │
        ▼
┌──────────────────────────────────────────────────────────────┐
│                                                              │
│  STEP 1: Find Network                                       │
│  TryGetNetworkId()                                          │
│  → reflection: AeccPressurePipesMgd                         │
│  → CivilDocumentPressurePipesExtension                      │
│    .GetPressurePipeNetworkIds()                              │
│  → returns first network ObjectId                           │
│                                                              │
│  STEP 2: Read Pipes                                         │
│  ReadPipes()                                                │
│  → GetObjectIdCollection(network, "PipeIds")                │
│  → For each pipe ObjectId:                                  │
│      tr.GetObject(pipeId, OpenMode.ForRead)                 │
│      │                                                      │
│      ├── PropertyExtractor.TryGetStartPoint()               │
│      ├── PropertyExtractor.TryGetEndPoint()                 │
│      ├── PropertyExtractor.TryGetDiameter()                 │
│      │   → tries 8 approaches (reflection)                  │
│      │   → converts meters → mm (×1000)                     │
│      ├── PropertyExtractor.TryGetDouble("Length2DCenterToCenter")
│      ├── PropertyExtractor.GetMaterialCode()                │
│      │   → "Ductile Iron" → "DI"                           │
│      ├── reflection: pipe.StartFittingId → ObjectId         │
│      └── reflection: pipe.EndFittingId → ObjectId           │
│                                                              │
│  STEP 3: Build Node Map (fitting-based)                     │
│  → Collect all fitting IDs from pipes                       │
│  → For each fitting:                                        │
│      tr.GetObject(fittingId, OpenMode.ForRead)              │
│      PropertyExtractor.TryGetPosition()                     │
│      → fitting center position = EPANET node                │
│  → Dead-end pipes → FindOrCreateOrphanNode()                │
│                                                              │
│  STEP 4: Build CSV Records                                  │
│  → For each pipe:                                           │
│      Has StartFitting? → use fitting position as US node    │
│      No fitting?       → use pipe endpoint as US node       │
│      Has EndFitting?   → use fitting position as DS node    │
│      No fitting?       → use pipe endpoint as DS node       │
│      │                                                      │
│      Create WsproCsvRecord (30 columns):                    │
│      UsId, DsId, Diameter, Length, Material,                │
│      coordinates, invert levels, vertices                   │
│      │                                                      │
│      PropertySetManager.ReadValues(pipeId, tr, record)      │
│      → reads simulation data from previous import           │
│      → populates: MaxPres, SurgeMax, VelocityFlag, etc.    │
│                                                              │
│  STEP 5: Build Export Nodes                                 │
│  → Fitting nodes + orphan nodes → ExportNode list           │
│                                                              │
│  Returns: List<WsproCsvRecord> + List<ExportNode>           │
└──────────────────────────────────────────────────────────────┘
        │
        ▼
┌──────────────────────────────────────────────────────────────┐
│  CsvWriter.cs → Write()                                     │
│                                                              │
│  StreamWriter → UTF-8 file                                  │
│  Line 1: "US Invert Level","DS Invert Level",...  (headers) │
│  Line 2: "0.00","0.00","150",...              (pipe 1 data) │
│  Line 3: "0.00","0.00","300",...              (pipe 2 data) │
│  All values double-quoted                                   │
│                                                              │
│  Output: WSPro_Export.csv                                   │
└──────────────────────────────────────────────────────────────┘
        │
        ▼
┌──────────────────────────────────────────────────────────────┐
│  EpanetWriter.cs → Write()                                  │
│                                                              │
│  [TITLE]       → "Exported from Civil 3D"                   │
│  [JUNCTIONS]   → Node ID, Elevation, Demand                │
│  [PIPES]       → Pipe ID, Node1, Node2, Length, Diameter    │
│  [COORDINATES] → Node X, Y positions                        │
│  [OPTIONS]     → Units: LPS, Headloss: Hazen-Williams      │
│  [END]                                                      │
│                                                              │
│  Output: WSPro_Export.inp                                   │
└──────────────────────────────────────────────────────────────┘
        │
        ▼
OUTPUT: Two files ready for WSPro/EPANET import
```

---

## IMPORT FLOW (WSPro → Civil 3D)

### Command: `WSPRO_IMPORT_PRESSURE`

```
USER types WSPRO_IMPORT_PRESSURE
│
│  Dialog: "Select WSPro Nodes CSV" → user picks file
│  Dialog: "Select WSPro Pipes CSV" → user picks file
│
▼
╔══════════════════════════════════════════════════════════════════╗
║  myCommands.cs → WsproImportPressure()                          ║
║                                                                  ║
║  • Prompts for 2 CSV file paths                                 ║
║  • Calls: WsproImporter.ImportNetwork(ed, nodesCsv, pipesCsv)  ║
╚══════════════════════════════╦═══════════════════════════════════╝
                               │
                               ▼
╔══════════════════════════════════════════════════════════════════╗
║  WsproImporter.cs → ImportNetwork()                             ║
║                                                                  ║
║  The main brain — orchestrates the entire import                ║
╚══════════════════════════════╦═══════════════════════════════════╝
                               │
        ┌──────────────────────┼──────────────────────┐
        │                      │                      │
        ▼                      ▼                      ▼
╔═══════════════╗  ╔═══════════════════╗  ╔═══════════════════════╗
║ WsproModels   ║  ║ WsproModels       ║  ║ WsproModels           ║
║ .ReadNodes()  ║  ║ .ReadPipes()      ║  ║ .ReadFullRecords()    ║
║               ║  ║                   ║  ║                       ║
║ Nodes CSV     ║  ║ Pipes CSV         ║  ║ Pipes CSV             ║
║ → 5 columns   ║  ║ → 5 columns      ║  ║ → ALL 30 columns     ║
║               ║  ║                   ║  ║                       ║
║ Returns:      ║  ║ Returns:          ║  ║ Returns:              ║
║ List<WsproNode>║ ║ List<WsproPipe>   ║  ║ List<WsproCsvRecord>  ║
║ (92 nodes)    ║  ║ (117 pipes)       ║  ║ (117 full records)    ║
║               ║  ║                   ║  ║ includes SURGE_MAX,   ║
║ Id, X, Y, Z  ║  ║ FromNode, ToNode  ║  ║ MAX_PRES, FLAGS, etc. ║
║               ║  ║ Diameter, Material║  ║                       ║
╚═══════════════╝  ╚═══════════════════╝  ╚═══════════════════════╝
        │                      │                      │
        └──────────┬───────────┘                      │
                   │                                  │
                   ▼                                  ▼
        ┌─────────────────────┐            ┌─────────────────────┐
        │ nodeDict:           │            │ recordLookup:       │
        │ "101" → (X,Y,Z)    │            │ "101|103" → record  │
        │ "103" → (X,Y,Z)    │            │ "103|109" → record  │
        │ "109" → (X,Y,Z)    │            │  (for PropertySets) │
        └─────────┬───────────┘            └──────────┬──────────┘
                  │                                   │
                  ▼                                   │
┌──────────────────────────────────────────────────────────────┐
│  OPEN TRANSACTION                                            │
│  tr = doc.TransactionManager.StartTransaction()              │
│                                                              │
│  ┌────────────────────────────────────────────────────────┐  │
│  │  PropertySetManager.EnsureDefinition(db, tr, ed)       │  │
│  │                                                        │  │
│  │  → Loads AecPropDataMgd.dll (explicit Assembly.LoadFrom)│ │
│  │  → Creates "WSPro Simulation Results" definition       │  │
│  │    with 15 properties (Max Pressure, Surge Max, etc.)  │  │
│  │  → Stores in AEC_PROPERTY_SET_DEFS dictionary          │  │
│  │  → Returns: propSetDefId                               │  │
│  └────────────────────────────────────────────────────────┘  │
│                                                              │
│  ┌────────────────────────────────────────────────────────┐  │
│  │  Find or Create Network                                │  │
│  │                                                        │  │
│  │  TryGetExistingPressureNetwork()                       │  │
│  │  → reflection: GetPressurePipeNetworkIds()             │  │
│  │  → found? isNewNetwork = false                         │  │
│  │                                                        │  │
│  │  OR                                                    │  │
│  │                                                        │  │
│  │  TryCreatePressureNetwork("WSPro_Import")              │  │
│  │  → reflection: PressurePipeNetwork.Create()            │  │
│  │  → created? isNewNetwork = true                        │  │
│  └────────────────────────────────────────────────────────┘  │
│                                                              │
│  ┌────────────────────────────────────────────────────────┐  │
│  │  Find Parts List                                       │  │
│  │                                                        │  │
│  │  TryGetFirstPartsListId()                              │  │
│  │  → tries: existing network's parts list                │  │
│  │  → tries: StylesRootPressurePipesExtension             │  │
│  │  → tries: CivilDocument property scan                  │  │
│  │  → Returns: partsListId (pipe/fitting catalog)         │  │
│  └────────────────────────────────────────────────────────┘  │
│                                                              │
│  ┌────────────────────────────────────────────────────────┐  │
│  │  CREATE PIPES (loop for each CSV pipe)                 │  │
│  │                                                        │  │
│  │  For pipe "101" → "103":                               │  │
│  │  │                                                     │  │
│  │  │  nodeDict["101"] → startPt (X=3.95, Y=6.50, Z=13.1)│ │
│  │  │  nodeDict["103"] → endPt (X=5.38, Y=5.77, Z=6.19) │  │
│  │  │                                                     │  │
│  │  │  TryFindPipePartSize(partsListId, 400mm)            │  │
│  │  │  → scans parts list for 400mm pipe                  │  │
│  │  │  → fuzzy match (tries mm, inches, meters)           │  │
│  │  │  → returns PressurePartSize                         │  │
│  │  │                                                     │  │
│  │  │  TryCreatePipe(networkId, partSize, startPt, endPt) │  │
│  │  │  → network.AddLinePipe(LineSegment3d, PressurePartSize)
│  │  │  → Returns: createdPipeId (ObjectId)                │  │
│  │  │                                                     │  │
│  │  │  ┌──────────────────────────────────────────────┐   │  │
│  │  │  │  PropertySetManager.AttachAndPopulate()      │   │  │
│  │  │  │                                              │   │  │
│  │  │  │  recordLookup["101|103"] → WsproCsvRecord    │   │  │
│  │  │  │                                              │   │  │
│  │  │  │  PropertyDataServices.AddPropertySet(pipe,def)│  │  │
│  │  │  │  → attaches PropertySet to this pipe         │   │  │
│  │  │  │                                              │   │  │
│  │  │  │  PropertySet.SetAt(0, "101_103")  Pipe ID    │   │  │
│  │  │  │  PropertySet.SetAt(1, 48.87)   Max Pressure  │   │  │
│  │  │  │  PropertySet.SetAt(2, 0.83)    Max Velocity  │   │  │
│  │  │  │  PropertySet.SetAt(3, 48.59)   Surge Max     │   │  │
│  │  │  │  PropertySet.SetAt(4, 26.86)   Surge Min     │   │  │
│  │  │  │  PropertySet.SetAt(5, "PN16")  Pres Class    │   │  │
│  │  │  │  ... (15 properties total)                   │   │  │
│  │  │  └──────────────────────────────────────────────┘   │  │
│  │  │                                                     │  │
│  │  │  Track node connections (for fitting placement)     │  │
│  │  │  Track pipe directions (for fitting rotation)       │  │
│  │  │                                                     │  │
│  │  └── Next pipe...                                      │  │
│  └────────────────────────────────────────────────────────┘  │
│                                                              │
│  ┌────────────────────────────────────────────────────────┐  │
│  │  FITTING PLACEMENT (conditional)                       │  │
│  │                                                        │  │
│  │  if (isNewNetwork == true)                             │  │
│  │  │  → Fresh import, no fittings exist                  │  │
│  │  │                                                     │  │
│  │  │  Check parts list for fitting families              │  │
│  │  │  → TryAutoAddFittingsFromCatalog() if missing       │  │
│  │  │                                                     │  │
│  │  │  For each junction node (2+ pipes):                 │  │
│  │  │  ├── TryFindFittingPartSize()                       │  │
│  │  │  │   2 pipes → Elbow    ("elbow","bend")            │  │
│  │  │  │   3 pipes → Tee      ("tee","t-piece")           │  │
│  │  │  │   4 pipes → Cross    ("cross")                   │  │
│  │  │  │                                                  │  │
│  │  │  ├── TryAddFitting(networkId, point, fittingSize)   │  │
│  │  │  │   → network.AddFitting(Point3d, PressurePartSize)│  │
│  │  │  │                                                  │  │
│  │  │  └── TryRotateFitting(fittingId, directions)        │  │
│  │  │      → calculates rotation from pipe vectors        │  │
│  │  │      → sets Rotation property or TransformBy()      │  │
│  │  │                                                     │  │
│  │  else (isNewNetwork == false)                          │  │
│  │     → Re-import, fittings already exist                │  │
│  │     → "Skipping fitting placement"                     │  │
│  └────────────────────────────────────────────────────────┘  │
│                                                              │
│  COMMIT TRANSACTION                                          │
│  tr.Commit()                                                 │
└──────────────────────────────────────────────────────────────┘
        │
        ▼
CIVIL 3D DRAWING NOW CONTAINS:
┌──────────────────────────────────────────────────────────────┐
│                                                              │
│  PressurePipeNetwork "WSPro_Import"                          │
│  ├── PressurePipe objects (61 pipes)                        │
│  │   └── each has PropertySet "WSPro Simulation Results"    │
│  │       with 15 simulation properties                      │
│  └── PressureFitting objects (41 fittings)                  │
│      (Tees, Elbows, Crosses at junctions)                   │
│                                                              │
│  USER clicks any pipe → Properties palette shows:           │
│  ┌────────────────────────────────────────┐                 │
│  │ WSPro Simulation Results               │                 │
│  │  Max Pressure:    48.87 bar            │                 │
│  │  Max Velocity:    0.83 m/s             │                 │
│  │  Surge Max:       48.59 bar            │                 │
│  │  Surge Min:       26.86 bar            │                 │
│  │  Pressure Class:  PN16                │                 │
│  │  PN Class:        PN64                │                 │
│  │  Velocity Flag:   OK                  │                 │
│  │  Surge Flag:      OK                  │                 │
│  └────────────────────────────────────────┘                 │
└──────────────────────────────────────────────────────────────┘
```

---

## ROUND-TRIP FLOW (Export → Simulate → Import → Export)

```
PASS 1: Initial Export
    Civil 3D network (designed manually)
    → WSPRO_EXPORT_PRESSURE
    → CSV + EPANET (simulation columns EMPTY)

PASS 2: Simulation
    → Import EPANET into WSPro
    → Run hydraulic simulation
    → Export results CSV (simulation columns FILLED)

PASS 3: Import Results
    → WSPRO_IMPORT_PRESSURE with results CSV
    → Pipes created with PropertySets
    → Simulation data visible in Properties palette

PASS 4: Re-Export (round-trip)
    → WSPRO_EXPORT_PRESSURE
    → NetworkReader reads pipes
    → PropertySetManager.ReadValues() reads simulation data FROM PropertySets
    → CSV + EPANET (simulation columns PRESERVED from Pass 3)
```

**How round-trip data survives:**
```
Pass 3 Import:
  CSV "SURGE_MAX=48.87"
    → WsproCsvReader.ReadFullRecords()
    → WsproCsvRecord.SurgeMax = "48.87"
    → PropertySetManager.AttachAndPopulate()
    → PropertySet on pipe: "Surge Max" = 48.87  ← STORED IN DRAWING

Pass 4 Export:
  NetworkReader.ReadNetwork()
    → PropertySetManager.ReadValues(pipeId, tr, record)
    → record.SurgeMax = "48.87"  ← READ BACK FROM DRAWING
    → CsvWriter writes "48.87" to CSV
```

---

## FILE RESPONSIBILITY SUMMARY

```
┌─────────────────────┬──────────────────────────────────────────┐
│ FILE                │ ONE-LINE RESPONSIBILITY                  │
├─────────────────────┼──────────────────────────────────────────┤
│ myCommands.cs       │ Receives command, prompts user, delegates│
│ PluginExtension.cs  │ Plugin init/shutdown (empty)             │
│ WsproModels.cs      │ Reads CSV text → C# objects             │
│ WsproCsvRecord.cs   │ Holds 30 columns of pipe data           │
│ WsproImporter.cs    │ CSV → Civil 3D pipes + fittings + props │
│ WsproExporter.cs    │ Coordinates export (read → write files) │
│ NetworkReader.cs    │ Civil 3D network → CSV records + nodes  │
│ CsvWriter.cs        │ Records → WSPro CSV file                │
│ EpanetWriter.cs     │ Records → EPANET .inp file              │
│ PropertyExtractor.cs│ Reads pipe properties via reflection    │
│ PropertySetManager.cs│ Simulation data ↔ Properties palette   │
└─────────────────────┴──────────────────────────────────────────┘
```

---

## WHICH FILE HANDLES WHAT — QUICK REFERENCE

| Question | Answer |
|----------|--------|
| "Where does the user command go?" | myCommands.cs |
| "Where is CSV parsed?" | WsproModels.cs |
| "Where are pipes created?" | WsproImporter.cs → `AddLinePipe()` |
| "Where are fittings created?" | WsproImporter.cs → `AddFitting()` |
| "Where is diameter extracted?" | PropertyExtractor.cs → `TryGetDiameter()` |
| "Where does Properties palette data come from?" | PropertySetManager.cs |
| "Where is the EPANET file generated?" | EpanetWriter.cs |
| "Where is the WSPro CSV generated?" | CsvWriter.cs |
| "Where is the network read for export?" | NetworkReader.cs |
| "Where are fittings turned into nodes?" | NetworkReader.cs → fitting-based node map |
| "Where is the transaction opened?" | WsproImporter.cs (import) or WsproExporter.cs (export) |
| "Where is reflection used?" | PropertyExtractor.cs + WsproImporter.cs + NetworkReader.cs |
| "Where is `AecPropDataMgd.dll` loaded?" | PropertySetManager.cs → `Assembly.LoadFrom()` |
