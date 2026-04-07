# Civil 3D / WSPro Integration Plugin — Video Script
### Duration: 3-4 minutes

---

## SCENE 1: The Problem (0:00 - 0:40)

**[Screen: Split view — Civil 3D on left, WSPro on right, both showing pipe networks]**

**NARRATOR:**

"Every day, water infrastructure engineers face the same frustrating challenge.

They design pressure pipe networks in Autodesk Civil 3D — laying out pipes, setting diameters, defining connections. But to validate their design — to check if pressures are safe, if velocities are within limits, if water hammer could damage the system — they need to run hydraulic simulations in InfoWorks WSPro.

The problem? These two tools don't talk to each other.

Today, engineers spend hours manually copying pipe data — node coordinates, diameters, lengths, elevations — from Civil 3D into WSPro. And when simulation results come back, they manually write those numbers back onto their Civil 3D drawings.

Every design change means repeating this entire process. Hours of work. Every time."

---

## SCENE 2: The Solution (0:40 - 1:10)

**[Screen: Civil 3D command line showing WSPRO_EXPORT_PRESSURE]**

**NARRATOR:**

"We built a plugin that eliminates this entirely.

Two commands. That's all it takes.

WSPRO_EXPORT_PRESSURE — reads your Civil 3D pressure network and instantly generates files that WSPro can import. Every pipe, every connection, every diameter — exported automatically in both WSPro CSV and EPANET format.

WSPRO_IMPORT_PRESSURE — takes your WSPro simulation results and brings them back into Civil 3D as native pressure pipe objects, complete with fittings at every junction.

No manual data entry. No copy-paste errors. No switching between tools to check a pressure value."

---

## SCENE 3: Live Walkthrough — Export (1:10 - 1:50)

**[Screen: Civil 3D with a pressure pipe network visible]**

**NARRATOR:**

"Let me show you how it works.

Here's a pressure pipe network designed in Civil 3D. Multiple pipe segments, different diameters, fittings at every junction.

I type WSPRO_EXPORT_PRESSURE..."

**[Screen: Command line shows export progress — reading pipes, generating nodes, writing files]**

"The plugin reads the entire network — pipes, fittings, connections. It generates unique node IDs, converts units, calculates invert levels.

In seconds, we have two output files: a WSPro-ready CSV with all 30 required columns, and an EPANET input file compatible with any hydraulic modeling software."

**[Screen: Show the exported EPANET file briefly — nodes, pipes, coordinates]**

"This data goes straight into InfoWorks WSPro. No manual typing. No errors."

---

## SCENE 4: Live Walkthrough — Simulate & Import (1:50 - 2:50)

**[Screen: WSPro showing the imported network with simulation results]**

**NARRATOR:**

"After importing into WSPro, the engineer sets up boundary conditions — reservoirs, demands — and runs the hydraulic simulation.

WSPro analyzes pressures, velocities, and surge conditions across the entire network. It flags problems — high velocity pipes, cavitation risks, pressure class requirements.

The engineer exports these results as a CSV."

**[Screen: Civil 3D — typing WSPRO_IMPORT_PRESSURE, selecting CSV files]**

"Now the magic. Back in Civil 3D, I type WSPRO_IMPORT_PRESSURE, select the results CSV..."

**[Screen: Command output showing pipes created, fittings placed, PropertySets attached]**

"The plugin creates native Civil 3D pressure pipes with proper fittings — tees, elbows, crosses — all placed automatically at junction nodes. But here's the key part..."

**[Screen: Click on a pipe — Properties palette opens showing WSPro Simulation Results]**

"Click any pipe. Right there in the Properties palette — Max Pressure: 45.62 bar. Max Velocity: 2.26 meters per second. Surge Max. Surge Min. Pressure Class PN16. Even the engineer's note: 'Velocity exceeds 2.0 m/s — upsize pipe.'

All of this, native inside Civil 3D. No switching windows. No looking up values in a separate tool. The simulation results live on the pipes themselves."

---

## SCENE 5: The Impact (2:50 - 3:20)

**[Screen: Side-by-side comparison graphic]**

**NARRATOR:**

"Let's put this in perspective.

A 100-pipe network — exporting manually takes 2 to 3 hours. With the plugin, 10 seconds.

Importing simulation results — 1 to 2 hours manually. With the plugin, 10 seconds.

A typical project goes through 5 design iterations. That's 20 hours of data transfer work — reduced to 10 minutes.

But it's not just about time. It's about accuracy. Zero manual data entry means zero transcription errors. The data in your simulation is exactly the data in your design. One source of truth."

---

## SCENE 6: Future & Close (3:20 - 3:50)

**[Screen: Roadmap graphic showing future phases]**

**NARRATOR:**

"And we're just getting started.

Next, we're building smart re-import — updating existing pipes with new simulation results instead of recreating them. Network validation before export. And eventually, direct API integration that eliminates the CSV step entirely.

The plugin is built on the EPANET standard, which means it works not just with WSPro, but with WaterGEMS, WaterCAD, and any EPANET-compatible hydraulic software."

**[Screen: Civil 3D with the pressure network and Properties palette visible]**

"Engineers should focus on engineering — not on transferring data between tools.

Civil 3D. WSPro. Connected."

**[Screen: Plugin title card with commands]**

```
Civil 3D / WSPro Integration Plugin

WSPRO_EXPORT_PRESSURE  →  Design to Simulation
WSPRO_IMPORT_PRESSURE  →  Simulation to Design
```

---

## Production Notes

| Timestamp | Visual | Audio |
|-----------|--------|-------|
| 0:00-0:40 | Split screen C3D/WSPro, manual workflow pain | Problem narration |
| 0:40-1:10 | Command line typing, file output | Solution intro |
| 1:10-1:50 | Live C3D export demo | Export walkthrough |
| 1:50-2:50 | WSPro sim → C3D import → Properties palette | Import walkthrough (key moment) |
| 2:50-3:20 | Comparison graphics, numbers | Impact/ROI |
| 3:20-3:50 | Roadmap → closing shot | Future & close |

**Key Visual Moments:**
- 2:35 — Properties palette showing simulation results (the "wow" moment)
- 1:25 — Export completing in seconds
- 3:15 — Before/after time comparison

**Background Music:** Professional, upbeat, not distracting

**Tone:** Confident, professional, problem-solving — not salesy
