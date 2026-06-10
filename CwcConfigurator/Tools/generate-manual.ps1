$ErrorActionPreference = 'Stop'
$outPath = Join-Path $PSScriptRoot '..\..\Plant_Configurator_Manual.docx'
$outPath = [System.IO.Path]::GetFullPath($outPath)

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# ----------------------------------------------------------------------------
# Manual content. Custom mini-markdown:
#   # Heading 1 (each starts a new page, except the very first)
#   ## Heading 2
#   ### Heading 3
#   blank line = paragraph break
#   - line starts a bullet
#   plain line = paragraph
#   **bold** inline
#   ``` code block delimiter (lines between go in monospace)
# ----------------------------------------------------------------------------

$markdown = @'
# About Plant Configurator
## What it is
Plant Configurator is a Windows desktop application that lets process and automation engineers configure a plant I/O catalog, alarm and parameter databases, sequence logic and SCADA tag list in one place, then generates the corresponding Siemens TIA Portal data-block source files automatically. It is the supported replacement for the legacy **Configuration_Unit_List.xlsm** workbook.

The application is built around the same data shape engineers already know from the Excel reference, but with typed models, strict validation, undo/redo, and a JSON project file (.plantcfg) that is safe to keep in source control and diff like any other file.

## Why it exists
The Excel workbook is convenient as a reference but unsafe as the daily operator tool. A single accidental keystroke can break a formula, overwrite a helper cell, or shift a whole column out of alignment. In Plant Configurator:

- Tables have **fixed typed models** -- the columns and their meaning cannot drift.
- Calculated fields (struct names, formulas, repair suggestions) are produced by code, not editable formulas.
- **Validation** runs before file generation; if any value is out of range or too long, generation stops cleanly with a clear pointer to the offending cell.
- Missing external files (TIA exports, templates) produce an explicit error instead of an empty output.
- Project data lives in a versioned **.plantcfg** JSON file; multiple projects on disk are independent and easy to back up.
- **TIA Portal integration** lets you pull hardware IDs and database content from a running TIA project, edit them safely, and push back.

## System requirements
- Windows 10 (x64) or newer.
- .NET 8 Desktop Runtime (included as installer in the distribution).
- .NET Framework 4.8 (required by the TIA Portal helper; included as installer).
- Optional: TIA Portal V17 or newer with Openness API enabled, for pull/push features.
- Optional: Microsoft Excel installed if you want to inspect generated workbooks; not required for any core feature.

# Installation
## First-time setup
You will receive Plant Configurator as a zip archive (typically named **PlantConfigurator-<version>.zip**). Steps:

**1.** Unzip the archive anywhere you have write access -- for example **C:\PlantConfigurator\**. Do not install into Program Files unless you are happy running with elevation every time you save settings.

**2.** Open the unzipped folder and double-click **Start Plant Configurator.bat**. This launches a PowerShell script that checks whether the .NET 8 Desktop Runtime and .NET Framework 4.8 are present and silently installs them from the bundled Installers subfolder if either is missing. The installation step requires Administrator privileges; UAC will prompt you.

**3.** When the script finishes, double-click **PlantConfigurator.exe** to launch the application.

## Subsequent launches
Once prerequisites are installed, you never need to run the .bat again. Launch the application by double-clicking PlantConfigurator.exe directly, or pin it to the taskbar / Start menu for convenience.

## Where data lives
- Project files (**.plantcfg**) live wherever you save them. They are plain UTF-8 JSON.
- Recent-projects index: **%AppData%\PlantConfigurator\recent-projects.json**.
- Diagnostic log: **%LocalAppData%\PlantConfigurator\logs\plantconfigurator-YYYYMMDD.log** (one file per day).
- The application itself is stateless apart from those two files; you can copy the install folder to another machine, install prerequisites, and continue work.

## Uninstalling
Delete the unzipped folder. Optionally remove **%AppData%\PlantConfigurator\** and **%LocalAppData%\PlantConfigurator\** to clear the recent list and log files. The .NET 8 Desktop Runtime and .NET Framework 4.8 are shared system components and stay installed.

# First Run and the Welcome Screen
When you launch Plant Configurator with no project open, the **Welcome screen** covers the work area. It is split into two columns.

## Left column -- Recent projects
Every project you open or save is added to the recent list (up to twelve entries, ordered most-recent first). Each entry shows the project name in bold and the full path underneath. To open a recent project, double-click its row or select it and press Enter. If a previously opened file has been moved or deleted, opening it produces a clear error message; the entry is not removed automatically, giving you a chance to locate the file.

## Right column -- Primary actions
- **New project** -- creates an empty project with the default PLC database PLC 1 and shows the main workspace.
- **Open project...** -- file picker scoped to .plantcfg (and .json as a fallback). Use this when the project you want is not in the recent list.
- **Import database from Excel...** -- pick a .xlsm or .xlsx file in the legacy Configuration_Unit_List format. Plant Configurator will create a new project shape, then prompt for a target PLC database name. This is the recommended way to migrate an existing plant from the Excel workbook.

The Welcome screen returns when you choose **File -> Close Project**. The Welcome list is also where you will see new projects after a successful save.

# Main Window Tour
Once a project is open, the application window is organized into five horizontal bands stacked top to bottom: menu bar, toolbar, project title row, the work area (with the PLC pane on the left and the active tab on the right), and the status bar. The optional Problems pane appears between the work area and the status bar when validation has produced messages.

## Menu bar
The menu bar exposes every action available from a keyboard, including ones that are not on the toolbar:

- **File** -- New, Open project, Close Project, Import database from Excel, Save, Save As. Recent projects are not on this menu; use the Welcome screen or Ctrl+O.
- **Tools** -- Validate (F5), Generate DB source files (F6), Export Project Summary, ALM/PAR List PDF Exporter, and a **WinCC** submenu with the Tag Browser launcher and the WinCC Unified JS Connector commands.
- **Descriptions** -- text-list import/export to and from TIA Portal Excel, DB Text Workbook export/import (a multi-sheet workbook covering every DB text in one file), and the description template editor.
- **TIA Sync** -- opens the TIA Sync dialog (also reachable from the toolbar).
- **Settings** -- opens the Settings Hub (the recommended entry point), plus quick-access shortcuts to Validation & Colors, PLC Names & Types, DB Names and Numbers, Project Paths, Table Design, and the Multilingual toggle.

## Toolbar
The toolbar holds the most-used commands as labeled icon buttons. From left to right:

- **New**, **Open**, **Save** -- project lifecycle. Save is disabled when there are no unsaved changes; the window title shows a leading dot marker whenever the project is dirty.
- **Undo**, **Redo** -- global undo/redo across every editable table in the project. The buttons grey out when the stack is empty.
- **Validate (F5)** -- run all validation passes and open the Problems pane.
- **Generate (F6)** -- open the Generate dialog and write DB source files to the configured output folder.
- **TIA Sync** -- open the TIA Sync dialog (project-level pull/push planning).
- **Settings** -- open the Settings Hub.
- **Find (Ctrl+F)** and **Replace (Ctrl+H)** -- open the find bar at the bottom of the current DB tab. Find works on every DB tab (DI/DO/MEAS/AO/ALM/PAR).

Every toolbar button is disabled when there is no project open, except New and Open which are always available.

## Project title row
Below the toolbar the project title row shows the project filename in large bold text followed by the full path in muted grey. On the right side of this row sits the **Action bar**, which is contextual to the active tab -- for example, Refresh DB from hardware appears on DB DI / DO / MEAS / AO tabs, while Pull and Push appear on every DB-style tab. Buttons not relevant to the current tab are hidden.

## PLC / SCADA pane (left)
The left pane is a tree of databases. By default it shows your PLC databases; the **PLC / SCADA** toggle at the top of the pane switches the workspace to your SCADA databases, which have a different tab set. The active database is marked with an orange background and bold name. A coloured dot to the left of each PLC database indicates TIA online state: green for in sync, red for differs, hollow for not checked.

Right-click any database in the tree for context actions: Rename, Copy, Load from other base, Import exported files from TIA Portal, Go online / Go offline, Pull / Push whole database, Validate, Generate DB source files, Export one PLC to Excel, Delete.

A small left-arrow button collapses the pane to a thin strip when you want more horizontal space for the tables; click it again to restore.

## Main tabs
The main work area is a tab control containing every editable table in the active workspace. In the PLC workspace the tabs are:

- Hardware: MODULE NAMES, DI, DO, AI, RTD, COUNTER, DEVICES.
- Database: DB DI, DB DO, DB MEAS, DB AO, DB ALM, DB PAR.
- Other: Sequence, Text lists.

In the SCADA workspace a single SCADA tab opens, with its own nested tabs (Tags, GENERAL, HEADER, MAIN NAV, SCREENS, LOCATIONS, PLC).

Tab colors give a quick visual cue: green tabs are hardware/other, blue tabs are database. The currently selected tab is rendered in a slightly darker shade.

## Problems pane
After Validate runs, the **Problems** pane appears above the status bar. It lists every issue with severity (ERROR or WARNING), the table the issue belongs to, the row reference, and a message describing the cause. Filter buttons at the top let you narrow to All / Errors / Warnings. Double-click a row to jump to the offending cell -- the cell is highlighted with a red border. Use Copy all to put the entire list on the clipboard, and Hide to dismiss the pane (validation results are kept until you re-run F5).

## Status bar
The bottom strip carries lightweight status: the last status message, the active workspace and database name, row counts per hardware section (DI/DO/AI/RTD/CNT/AO), the current selection size in the table, and the project dirty marker. The dirty marker also appears as a leading dot in the window title bar.

# Working with Projects
## New project
Choose **File -> New** (Ctrl+N) or click New project on the Welcome screen. A blank project is created with a single PLC database named PLC 1 and default settings. The project is unsaved (no path on disk); use Save As to give it a name.

## Open project
Choose **File -> Open project...** (Ctrl+O). The first time you open a project, browse to the .plantcfg file. Afterwards it appears in the Welcome screen recent list and you can double-click it there. The standard file extension is .plantcfg; for backward compatibility, .json files in the same format will also open.

## Save
Choose **File -> Save** (Ctrl+S) or click the toolbar Save button. If the project has not yet been saved, the Save As dialog appears. Save As (Ctrl+Shift+S) always prompts for a new path.

Save is enabled only when there are unsaved changes. The window title shows a leading dot marker (the same indicator used in the status bar) whenever the project is dirty; the dot disappears the moment you save.

## Importing from the legacy Excel workbook
If you have an existing Configuration_Unit_List.xlsm populated for a plant, use **File -> Import database from Excel...** (or Import database from Excel... on the Welcome screen). Pick the workbook; Plant Configurator reads every recognised sheet and prompts for a destination PLC database. You can either:

- Create a new PLC database to hold the imported data (recommended for first import -- the suggested name is the workbook filename).
- Replace an existing PLC database with the imported content (useful when you maintain a project that mirrors a TIA project and want to refresh from the Excel reference).

After import, run Validate (F5) to surface any inconsistencies that the workbook had been hiding.

## Closing a project
**File -> Close Project** discards the in-memory project (prompting first if it has unsaved changes) and returns to the Welcome screen. Closing one project does not affect any other application state.

## Multiple projects
Plant Configurator is a single-document application -- only one project is open at a time. To work with two projects side by side, launch the executable twice; each instance is independent and has its own undo history. Recent-project entries are shared between instances.

# The PLC Databases Pane
A single project can contain many PLC databases (one per physical PLC). The left pane lists them all; clicking a database name makes it active -- every tab in the work area then reflects that database content. Edits go into whichever database is active; switching saves the previous one in memory.

## Adding and managing databases
- **Add PLC database** (button at the bottom of the pane) -- creates a fresh empty database named PLC N. You will be prompted for the name.
- **Rename** -- right-click and choose Rename. Names must be unique within the project.
- **Copy** -- right-click and choose Copy. Creates a deep clone with a (copy) suffix; useful when you want to fork a database for what-if changes.
- **Load from other base...** -- pulls all data from another PLC database in the same project into the selected one (overwrites).
- **Import exported files from TIA Portal...** -- opens a folder picker; reads .db / .dt source files exported from TIA Portal and merges them into the selected database.
- **Delete** -- removes the database. Cannot be undone via Undo; use only when you are sure.

## TIA online status
Each PLC database row carries a coloured dot:

- **Hollow / grey** -- TIA state has not been checked, or no TIA session is connected.
- **Green** -- the database content in Plant Configurator matches what TIA Portal currently holds.
- **Red** -- content differs from TIA Portal; pull or push to reconcile.

Use Go online in the context menu to attach the selected database to the currently open TIA Portal session; Go offline detaches it. When online, Pull whole database and Push whole database transfer the entire DB DI / DO / MEAS / AO / ALM / PAR contents in one operation.

# Hardware Tabs (DI / DO / AI / RTD / COUNTER / AO)
The hardware tabs describe the physical I/O modules of the PLC and the signal-level mapping. Each row corresponds to one channel of one module. The tabs share a common layout:

- A **Module number** slicer at the top -- Excel-style horizontal pill list where each pill is one module. Click a pill to filter the table to only that module; Ctrl+click for multi-select; Clear resets the filter.
- For tabs that have a corresponding TIA hardware section, a status text (HW_ID_s: ...) plus Pull HW <section> and Push HW <section> buttons.
- The main table itself, with a leading config button per row, the module identification columns (MODULE NUM, MODULE NAME, PORT, CONFIG, ID), and signal-name / structure columns derived from the database.

## Editing rows
Most cells are read-only because they are computed: STRUCTURE, STATUS NAME DEFINITION, STRUCT NAME, SIGNAL NAME come from the DB DI / DO / MEAS / AO definitions. Editable cells include **CONFIG** (a dropdown of valid types per module type), **ID** (assigns the channel to a DB row), and section-specific fields such as FILTER TIME, LOW LIMIT, HIGH LIMIT, CUSTOM LIMITS for analog tabs.

Click the leading config button (gear icon) on any row to open the **per-row config dialog**. The dialog shows all data for the channel in a focused form, lets you change the digital input config, alarm reference, polarity, or -- for analog signals -- the measurement reference, custom limits, and filter time.

## Module Names tab
The MODULE NAMES tab gives every module a human-readable name. It is a 2x3 grid of small tables, one per signal section (DI / DO / AI / RTD / COUNTER / AO), each with two columns: MODULE NUMBER (read-only) and MODULE NAME (editable). The names you enter here are shown on the hardware tabs and in generated source files.

## HW Pull / HW Push
These buttons synchronise the hardware section with TIA Portal station configuration:

- **Pull HW** reads the current module list and HW_ID values from the connected TIA project and overwrites the table; use this after you change the hardware in TIA Portal and want Plant Configurator to catch up.
- **Push HW** does the reverse -- pushes the locally-configured module names back into TIA Portal station, useful if you have been working offline and want to re-align TIA with Plant Configurator.

Both operations need an open TIA Portal session and a database that has been switched Online in the PLC pane. The status text shows whether the local HW_IDs match TIA current state.

## DEVICES tab
The DEVICES tab lists higher-level field devices (rather than individual channels) and their mapping to the I/O modules. It uses the same spreadsheet pattern as the other hardware tabs.

# Database Tabs (DB DI / DO / MEAS / AO / ALM / PAR)
The database tabs are the heart of the application. Each one corresponds to one Siemens TIA data block in the generated source. Rows represent individual signals, alarms or parameters; columns hold every piece of data that ends up in the generated DB. Every database tab shares a uniform toolbar at the top and a consistent set of features in the table itself.

## Column slicer
At the very top of every DB tab is a pill row called the **column slicer**. Each pill toggles a group of related columns on or off, for example:

- **All** -- show every column.
- **Nothing** -- collapse to the frozen identification columns only.
- **Other Language** -- show only the *Other text columns (e.g. NAME OTHER, DESCRIPTION OTHER).
- **Mapping** -- show columns linking the DB row to the corresponding hardware channel.
- **Coloring** -- show conditional-color values (DB DI only).
- **Alarm Mapping** -- show ALM CONFIG / INVERT / ID and the cross-reference text (DB DI only).
- **Repair** -- show the REPAIR column with computed suggestions for each row.
- **XRef** -- show TIA cross-reference usage counts.

## Filters
Below the slicer, each DB tab has a per-column filter strip aligned with the table columns. Each filter is a combo box; pick a value to constrain the table to only rows matching that value across all active filters. The combo populates itself from the distinct values currently in the column. A separate **Text filter** at the top performs a substring match across every visible text column.

Clear filter removes every active filter at once. The frozen-column section of the filter strip stays put when you scroll horizontally, just like the table frozen columns themselves.

## Frozen columns
The first seven columns of every DB tab (USED, ID, INFO, config button, PLC STRUCT, PLC NAME config, PLC NAME) are frozen -- they stay visible no matter how far right you scroll. A solid 2-pixel dark border separates the frozen region from the scrolling region.

## Editing
Double-click any editable cell to start editing; press Enter to commit and move to the next row, Esc to cancel. Most string columns accept multi-line text (Ctrl+Enter inserts a line break). Drop-down columns (TYPE, CONFIG, USED, ALM CONFIG) restrict edits to the values defined for the column in the per-DB Validation and Colors settings.

Selecting a range of cells lets you copy with Ctrl+C and paste with Ctrl+V in spreadsheet style; the source cells are marked with a green dashed marching-ants border until you paste somewhere else or press Esc. Drag the small green square at the bottom-right of the selection to auto-fill a series down or right (Excel-style).

## Per-row buttons
- **Configure row** (gear icon) -- opens a focused row editor dialog that exposes all fields for that one row, with template-matching dropdowns and special widgets for string fields with maximum-length constraints.
- **Info** (i icon) -- opens an info dialog with computed details about the row (cross-references, alarm references, derived names).
- **Configure PLC name** -- opens a dialog for editing the PLC NAME used in generated source for that row, with a preview of how the name will look after the configured struct/simulator prefixes are applied.

## Refresh DB from hardware
On DB DI / DO / MEAS / AO tabs an additional button appears in the project action bar: **Refresh DB from hardware**. This populates the DB configuration columns (the bits that depend on the hardware module type) from the corresponding hardware tables (DI / DO / AI / RTD / COUNTER / AO). Use this whenever the hardware section changes -- for example after a TIA HW Pull.

## Cross-reference (XRef) columns
The XRef columns count how many times a DB row is referenced in TIA -- by ID, by VAL, separated by Write/Read where applicable. The counts are not live; click Refresh XRef in the tab toolbar to scan the connected TIA project and update them. The scan can take a few seconds per DB on a large project.

## REPAIR column
The REPAIR column shows, per row, a brief summary of the validation problems that affect the row -- for example "Needs repair: System name ENG, Name ENG" or "ID 515 appears 2 times." It is computed live as you edit, so you do not need to re-run Validate to see the effect of a change.

## Conditional row and cell coloring
DB ALM rows are coloured by **blocking class** (Alarm, Start interlock, Normal shutdown, Quick shutdown, Emergency shutdown); DB PAR rows used by code are highlighted, and the active value cell (I_VAL, T_VAL or R_VAL -- whichever matches the parameter type) gets its own colour. All these colours are configurable in **Settings -> Table design**.

# Sequence Tab
The Sequence tab defines the SEQ_PAR data block (DB 28) and the control-status / command text lists that drive sequence diagrams. It is split into three inner tabs.

## Configuration
Four named diagrams -- START, NSD, QSD, ESD -- each rendered as a sequence diagram view. Use the **Import SEQ_PAR.db...** button to load a TIA-exported SEQ_PAR.db source file as the starting point. The **Refresh XRef** button updates cross-reference usage counts the same way the database tabs do.

Each diagram view lets you drag the sequence steps to rearrange them, click a step to edit its name and parameters, and use the right-click menu to add new steps or remove existing ones. Changes propagate to the SEQ_PAR.db source on Generate.

## Control status
A 64-row table mapping control-status IDs 0 - 63 to PLC names and bilingual text-list labels. Each row exposes:

- A read-only ID column.
- An info button (opens a focused dialog with derived details).
- Two configure buttons -- one for the text list name in both languages, one for the PLC variable name.
- TEXT LIST NAME ENG and TEXT LIST NAME OTHER columns for direct in-line edits.
- XREF columns counting TIA references by ID and by VAL.

A text filter and per-column dropdown filter strip work the same as on the DB tabs.

## Sequence commands
A 32-row table mapping sequence command IDs 0 - 31, with the same column layout as Control status. These IDs feed the HMI command text list.

# Text Lists Tab
Text Lists holds the auxiliary bilingual text catalogs used by both PLC and HMI. The top-level tab contains five inner tabs, one per catalog:

- **ALM BLOCKING CLASS** -- the five blocking classes (Alarm, Start interlock, Normal shutdown, Quick shutdown, Emergency shutdown).
- **CABINETS** -- cabinet text labels.
- **PORT CONFIGURATION** -- port configuration labels.
- **SEQ CMD** -- sequence command text-list labels (mirrors the Sequence commands inner tab).
- **SEQ CONFIG** -- sequence configuration text-list labels.

Each inner tab is a simple table of ID, TEXT LIST NAME ENG, TEXT LIST NAME OTHER. The ID column is read-only; the two text columns are editable. A short context paragraph at the bottom of each tab explains what the catalog feeds and where it shows up in TIA.

At the very top of the Text Lists tab are two buttons: **Import texts** loads bilingual text from a TIA-exported Excel workbook into all five catalogs, **Export texts** writes the current state back to a workbook in the same format.

# SCADA Workspace
SCADA configuration lives in a parallel workspace. Switch to it by clicking the **SCADA** toggle at the top of the left pane; switch back with **PLC**.

## SCADA databases
A project can contain multiple SCADA databases (typically one per HMI / runtime). The SCADA pane works the same as the PLC pane: click a database to make it active, right-click for Rename / Copy / Delete, use **Add SCADA database** to create a new one. Each SCADA database carries a source-text path, last-generated path, a list of configuration variables, and a list of tag-write definitions.

Two buttons at the bottom of the SCADA pane -- **Import SCADA text...** and **Export SCADA text...** -- bring in or write out the source text format that WinCC Unified accepts as a script module.

## SCADA tabs
In the SCADA workspace the main tab area shows a single **SCADA** tab with the following inner tabs:

- **Tags** -- the variable list. Editable columns: ID, SECTION, TAG, VARIABLE, TYPE, VALUE. The LINE column is read-only and shows where the variable was found in the source. A filter box at the top performs a substring match.
- **GENERAL** -- top section with global settings (Runtime name, Bilingual SCADA toggle, Second language, Location num, Location for language). Below is the Locations table -- eight rows, one per location index, with ACTIVE / SOURCE / ENTRY / LOCATION NAME / LOCATION NAME 2 / LOCAL or REMOTE columns. A **Validation** button runs the SCADA-specific validation.
- **HEADER** -- header text rows that appear at the top of generated SCADA screens.
- **MAIN NAV** -- main-navigation entries; the same Copy cell / Swap cell / Copy row / Swap row buttons appear in the row context menu for power editing.
- **SCREENS** -- screen layout rows, also with Copy / Swap helpers.
- **LOCATIONS** -- extended location detail rows.
- **PLC** -- PLC-bound variable rows that feed the SCADA tag list.

## WinCC Unified JS Connector
For the WinCC Unified script-injection workflow, Plant Configurator can launch and exchange data with a Node-based connector process. Use **Tools -> WinCC -> Import WinCC Unified JS Connector...** to point Plant Configurator at the connector folder, and **Test WinCC Unified script connection...** to verify the process starts and can enumerate open TIA processes. The connector folder lives next to the application under Tools\WinCCUnifiedJsConnector\.

# TIA Portal Integration
Plant Configurator integrates with Siemens TIA Portal through the Openness API, mediated by a small .NET Framework 4.8 helper executable (TiaHelper\CwcConfigurator.TiaHelper.exe) that ships next to the main application. The helper runs under the same user as Plant Configurator and talks to a running TIA Portal session over the local IPC channel.

## Prerequisites
- TIA Portal V17 or newer installed on the same machine.
- Openness API enabled for the Windows user account (one-time setup in TIA Portal settings).
- The TIA Portal project you want to talk to must be open in TIA Portal before pull/push.

## TIA Sync dialog
Open **Tools -> TIA Sync** or click TIA Sync on the toolbar. The dialog lists the open TIA projects detected by the helper and lets you pick which Plant Configurator PLC database maps to which TIA station, then run a Pull or Push for the whole selected pair. Use this for the first synchronisation or after major changes.

## Per-section pull / push
Smaller-grained pulls and pushes are available on the hardware tabs and database tabs:

- Hardware tab toolbar: Pull HW <section> / Push HW <section> for module names and HW_IDs of the one section (DI / DO / AI / RTD / COUNTER).
- Database tab toolbar: Pull DB <section> / Push DB <section> for that one DB (DB DI / DO / MEAS / AO / ALM / PAR).
- PLC pane context menu: Pull whole database / Push whole database runs every section for that PLC in one operation.

A modal progress dialog shows the current operation; you can cancel any pull or push at any time. After a Pull, the row counts in the status bar update and the database online indicator turns green if the local state now matches TIA.

## HW_ID status
Each hardware tab shows an HW_ID_s text on the right side of the top toolbar. Possible values are not checked, matches, differs. The check runs automatically on a tab change and after each pull. When it shows differs, your local module list has gone out of sync with TIA; pull to reconcile.

## Cross-reference scan
The Refresh XRef button on each DB tab scans the TIA project for usages of each DB row by ID and by VAL, populates the XREF columns, and applies the result to the REPAIR column (rows referenced zero times are marked accordingly). The scan is one-way (read-only against TIA) and safe to run at any time.

# Validation
## When validation runs
Validation runs in two places:

- Manually, when you press **F5** or choose **Tools -> Validate**. Use this whenever you want to take stock of the project state.
- Automatically, immediately before Generate DB source files (F6). If any ERROR-severity problem exists, generation stops; you must clear the errors first. Warnings do not block generation.

## What is checked
- **ID ranges** -- every DB row ID is within the allowed numeric range for its DB.
- **Duplicate IDs** -- no two rows in the same DB share an ID.
- **Validation list values** -- every dropdown column holds one of the allowed values from Settings -> Validation and Colors.
- **Text length limits** -- every text column is within the maximum character count for its field (configured per DB in Settings).
- **Multilingual coverage** -- when Multilingual mode is on, every *Other text column is filled (or matches an allowed sentinel).
- **Database consistency** -- every alarm-config reference points to an existing DB ALM row, every parameter reference to an existing DB PAR row, and so on.
- **Hardware mapping** -- every DB DI / DO / MEAS / AO row mapped to a hardware channel matches the channel configured type.

## Reading the Problems pane
Validation results appear in the Problems pane between the status bar and the work area. The pane has four columns: Severity, Table, Ref, Message. Severity is ERROR (red highlight) or WARNING (yellow highlight). Table is the database (e.g. DB_DO), Ref is the row identifier (e.g. ID 272). Double-click a row to jump to the offending cell.

Use the radio buttons at the top of the pane to filter by severity. Copy all copies the entire visible list to the clipboard as tab-separated text (good for emailing to a colleague). Revalidate runs F5 again. Hide dismisses the pane; problems remain in memory until you re-run validation.

## Per-row REPAIR column
Independent of the global Validate pass, every DB tab carries a REPAIR column that computes a short summary of the row repair status live as you edit. Use the column slicer (Repair) to bring it into view, or include it in the column layout permanently via Settings -> Table design.

# Generating DB Source Files
Generate produces the Siemens TIA .db and .dt source files from the current project state and writes them to the configured output folder.

## Running Generate
Press **F6**, choose **Tools -> Generate DB source files**, or right-click the active PLC database in the left pane and choose Generate. The Generate dialog opens, showing every DB available for export grouped by category (Configuration, DB Configuration, DB Name, DB Group Name). Tick the ones you want; use the Select all button to tick everything. The output folder is pre-populated from Settings -> Project paths; you can override per run.

Click Generate to write the files. A short status appears at the bottom of the dialog after each file; on success the dialog closes and a status message appears in the main status bar.

## File naming
The generated file name for each DB comes from Settings -> DB names and numbers, specifically the GENERATED FILE NAME column. Change a name there if you need to match a TIA-side convention or version.

## What happens on errors
Generate runs Validate first. If any ERROR is found, generation is aborted and the Problems pane is opened with the issues. Warnings do not abort generation but are still surfaced; you can review them after the fact.

# Descriptions, Templates and Multilingual Mode
## The bilingual text model
Most string fields in Plant Configurator come in pairs: an English version and an *Other version. The "other" language is whichever second language the plant is being commissioned for. Whether the *Other fields are validated depends on the **Multilingual** toggle in Settings: when on, both languages must be filled; when off, *Other fields are skipped during validation and repair.

## Importing TIA Portal text lists
Use **Descriptions -> Import text list from TIA Portal exported Excel...** to pull a TIA-exported text-list workbook into Plant Configurator. This populates the bilingual columns in DB DI / DO / MEAS / AO / ALM / PAR, the Sequence catalogs, and the Text lists tab in one operation.

## Exporting back to TIA Portal
**Descriptions -> Export text list to TIA Portal Excel...** writes the current project text lists back to a TIA-compatible workbook. The output workbook can be imported into TIA Portal directly without further editing.

## DB Text Workbook
**Descriptions -> Export DB Text Workbook...** and **Import DB Text Workbook...** handle a multi-sheet Excel workbook covering every DB tab text in one file. This is the format the company translation team works with -- they receive one workbook, return one workbook, and you re-import it in a single click.

## Description Template Editor
Choose **Descriptions -> Template Editor...** to open the description template catalog. Templates are stock descriptions parameterised by short codes (e.g. signal type, location); rows that match a template render in bold to mark them as "official wording" and rows that drift get a normal-weight font so they stand out for review.

Templates can be imported from and exported to CSV with the **Import templates from Excel...** and **Export templates to Excel...** entries on the same menu.

# Settings Hub
Plant Configurator consolidates every project setting into a single window -- the **Settings Hub**. Open it by clicking the Settings button on the toolbar, or from **Settings -> Project Settings...** in the menu. The hub is a modal window that stays open while you browse settings; close it with Esc or the Close button.

## Hub layout
The hub is split vertically:

- **Left** -- a tree of categories. Branches are expandable, leaves open editors.
- **Right** -- the editor for the selected leaf, with a header showing the leaf title, a breadcrumb, and a short description of what the setting does.
- **Bottom bar** -- status text on the left, **Apply** / **Revert** / **Close** buttons on the right.

Editing rules: each editor holds local edits until you press **Apply**, which commits the change into the project and runs whatever refresh is needed. **Revert** reloads the editor from the project, throwing away unapplied edits. Switching to another leaf without pressing Apply also discards local edits -- Apply explicitly when you want changes kept.

## General category
- **Project paths** -- folders the generator and exporters write to and read from. The Generated files output folder is the most important; the two reference paths at the bottom (PLC source folder, R source folder) are printed in Project Summary only.
- **Table design** -- table layout colour scheme, the master switch for conditional cell coloring, and the per-class row colors for DB ALM and DB PAR. Each color combo shows a small swatch next to it so you see what you picked.
- **Multilingual mode** -- checkbox toggling validation of *Other text fields.

## DB names and numbers
Editable table mapping every data block to its DB NUMBER, DB CODE, BLOCK KIND, GENERATED FILE NAME, DATA BLOCK NAME, DATA TYPE NAME, the source column used during Excel import, and the LAST USED ID to seed new rows. Add data block and Delete selected manage the list.

## Per-DB settings
Each of DB DI, DB DO, DB AO, DB MEAS, DB ALM, DB PAR has two settings leaves underneath it:

- **Validation and colors** -- three tabs: Status colors (only DB DI uses these), Text length limits (max characters per text column), and Validation lists (the dropdown allowed-value sets used by columns). Validation Lists is a master/detail editor -- pick a field on the left, edit its values on the right.
- **PLC names and types** -- the System name rows that define the PLC struct type members, plus the PLC struct type prefix and (for DI / DO / MEAS) the Simulator struct type prefix. The two right-most table columns are read-only and show the prefixed names that will land in the generated source.

Old per-setting menu entries (Settings -> Validation and Colors -> DB DI and similar) still work as quick-access shortcuts and open the same editors as standalone windows, for users who prefer one editor per window over the hub layout.

# Keyboard Shortcuts and Productivity
## Application-wide
- **Ctrl+N** -- New project
- **Ctrl+O** -- Open project
- **Ctrl+S** -- Save
- **Ctrl+Shift+S** -- Save As
- **Ctrl+Z** -- Undo
- **Ctrl+Y** -- Redo
- **F5** -- Validate
- **F6** -- Generate DB source files
- **Ctrl+F** -- Find (open the find bar at the bottom of the current DB tab)
- **Ctrl+H** -- Replace (find bar opens with the Replace row exposed)
- **Esc** -- Close the find bar (when focused)

## In the spreadsheet tables
- **Enter** -- commit current cell and move to the next row.
- **Esc** -- cancel current edit.
- **F2** -- start editing the current cell.
- **Tab / Shift+Tab** -- move to the next/previous cell.
- **Ctrl+C** -- copy selection.
- **Ctrl+V** -- paste at current selection.
- **Ctrl+A** -- select the entire visible table.
- **Delete** -- clear the values of the selected cells (for editable columns).

## Spreadsheet helpers
- **Marching ants** -- after Ctrl+C, the source cells show an animated green dashed border so you can see where the clipboard came from while you pick a paste target.
- **Auto-fill handle** -- the small green square at the bottom-right corner of a selection. Drag it down or right to fill the dragged range with a copy or a sequential auto-increment (depending on the column type).
- **Problem cell markers** -- cells flagged by the most recent Validate run carry a persistent red border. They survive scrolling and sorting; the border clears when you fix the underlying issue and re-run Validate.

# Tools Menu
The Tools menu hosts the project-level export and integration commands that are not part of the day-to-day editing flow.

- **Validate (F5)** and **Generate DB source files (F6)** -- covered in their own chapters.
- **Export Project Summary** -- writes a plain text file summarizing every database, row counts, and the recorded reference paths. Useful as an audit attachment or a quick diff baseline.
- **ALM/PAR List PDF Exporter** -- opens a dialog that produces a printable bilingual PDF of every DB ALM and DB PAR row, suitable as commissioning documentation. Templates live under Templates\alm_par_template*.docx.
- **WinCC -> WinCC Tag Browser...** -- launches the bundled standalone Tag Browser executable for inspecting tag definitions in a WinCC project. Useful when reconciling SCADA tag names.
- **WinCC -> Import WinCC Unified JS Connector...** -- point Plant Configurator at a WinCC Unified JS Connector installation folder so the SCADA script-injection flow knows where to start the connector.
- **WinCC -> Test WinCC Unified script connection...** -- start the connector and verify it can list open TIA Portal processes. Use this to confirm the WinCC Unified setup before relying on it.

# Tips, Troubleshooting, FAQ
## Saving discipline
Save often. The title bar dot and the Save toolbar button (enabled when dirty) make the project state obvious; do not rely on autosave because there is none. Undo / Redo cover most editing accidents within a single session, but they do not survive closing the project.

## Backups
The project file is plain JSON; commit it to git or copy it to a network share like any other source file. The application stores no other per-project state outside that file, so a single copy is a complete backup.

## Where is the log file
Plant Configurator writes a single log file per day to %LocalAppData%\PlantConfigurator\logs\plantconfigurator-YYYYMMDD.log. Unhandled exceptions, TIA session errors and similar diagnostics land here. When reporting a bug, attach the file from the day the bug occurred.

## I do not see my recent project
The Welcome screen lists up to twelve recent projects. If your file is older than that, the entry has fallen off; use Open project... from the menu or the Welcome screen, browse to the file, and it will reappear in the recent list. Recent entries are kept across application restarts in %AppData%\PlantConfigurator\recent-projects.json; delete that file to reset the list.

## Save is greyed out
Save is disabled when the project has no unsaved changes (the title bar would also show no leading dot in that case) or when no project is open. If you expect to have changes but Save is still grey, the change might have been undone by Ctrl+Z without you noticing -- check the title bar.

## Validate says my row is broken but it looks fine
Check the REPAIR column on the row (slicer -> Repair). It gives a short reason for the validation hit; often it is a length overflow in an *Other field that you forgot to fill, or a duplicate ID elsewhere in the same DB.

## Generate refuses to write files
Generate aborts on any ERROR-severity validation problem. Open the Problems pane (it pops up automatically), filter to Errors, fix each one, then run Generate again. Warnings do not block generation.

## TIA Pull does nothing
Pull and Push require an open TIA Portal session with the target project loaded, and the relevant Plant Configurator database must be switched **Online** (right-click and TIA go online in the PLC pane). The HW_ID_s text on each hardware tab reports the current connection state -- when it shows "not checked", the database is offline.

## Settings Hub shows "Apply skipped"
The currently open editor surfaced a validation error (e.g. a non-numeric value in a text-length field). A more specific message appears in a popup; fix the value and press Apply again.

# Glossary
- **DB DI / DO** -- Data block for Digital Inputs / Outputs. Each row is one status definition.
- **DB MEAS** -- Data block for analog measurements (combined AI / RTD / COUNTER sources).
- **DB AO** -- Data block for Analog Outputs.
- **DB ALM** -- Alarm data block. Each row is one alarm definition; blocking class assigns it to one of the shutdown categories.
- **DB PAR** -- Parameter data block. Each row is one operator-tunable parameter with I_VAL, T_VAL or R_VAL active depending on type.
- **SEQ_PAR** -- Sequence parameter data block (DB 28). Holds the step list for the four sequence diagrams.
- **HW_ID** -- TIA Portal unique identifier per hardware module; Plant Configurator compares its local mapping against TIA and reports any drift.
- **Struct type** -- A TIA Portal user-defined type used as the row shape inside a DB. Plant Configurator generates two per category: the production struct (prefix DT_) and, where relevant, a simulator struct (prefix DT_SIMULATOR_).
- **Blocking class** -- One of five categories assigned to a DB ALM row: Alarm, Start interlock, Normal shutdown, Quick shutdown, Emergency shutdown. Determines row color and how the alarm propagates in the PLC logic.
- **Cross-reference (XRef)** -- A count of how many places in the TIA project use a given DB row. Surfaced per row in the XREF columns of every DB tab and refreshed on demand with the Refresh XRef button.
- **Repair** -- The action of fixing a validation finding. The REPAIR column gives a per-row summary of pending repairs; the global Validate (F5) gives the full list in the Problems pane.
- **Multilingual mode** -- When on, both English and *Other text columns are validated; when off, *Other is ignored. Toggle in Settings -> Multilingual mode.
- **Workspace** -- Either PLC (default) or SCADA. The toggle is at the top of the left pane; each workspace has its own tab set.
'@

# ----------------------------------------------------------------------------
# Build OpenXML body from markdown
# ----------------------------------------------------------------------------
$body = New-Object System.Text.StringBuilder

function Esc-Xml($t) {
    if ($null -eq $t) { return '' }
    return ($t -replace '&','&amp;' -replace '<','&lt;' -replace '>','&gt;')
}

function Render-Runs($text) {
    # Split on ** to toggle bold
    $parts = $text -split '\*\*'
    $runs = ''
    for ($i = 0; $i -lt $parts.Count; $i++) {
        $p = Esc-Xml $parts[$i]
        if ($i % 2 -eq 1) {
            $runs += '<w:r><w:rPr><w:b/></w:rPr><w:t xml:space="preserve">' + $p + '</w:t></w:r>'
        } else {
            $runs += '<w:r><w:t xml:space="preserve">' + $p + '</w:t></w:r>'
        }
    }
    return $runs
}

$firstH1Seen = $false
$lines = $markdown -split "`r?`n"

foreach ($line in $lines) {
    $trim = $line.TrimEnd()
    if ([string]::IsNullOrWhiteSpace($trim)) {
        [void]$body.AppendLine('<w:p/>')
        continue
    }
    if ($trim.StartsWith('### ')) {
        $t = $trim.Substring(4)
        [void]$body.AppendLine('<w:p><w:pPr><w:pStyle w:val="Heading3"/></w:pPr>' + (Render-Runs $t) + '</w:p>')
    } elseif ($trim.StartsWith('## ')) {
        $t = $trim.Substring(3)
        [void]$body.AppendLine('<w:p><w:pPr><w:pStyle w:val="Heading2"/></w:pPr>' + (Render-Runs $t) + '</w:p>')
    } elseif ($trim.StartsWith('# ')) {
        $t = $trim.Substring(2)
        $pPr = if ($firstH1Seen) {
            '<w:pPr><w:pStyle w:val="Heading1"/><w:pageBreakBefore/></w:pPr>'
        } else {
            '<w:pPr><w:pStyle w:val="Heading1"/></w:pPr>'
        }
        [void]$body.AppendLine('<w:p>' + $pPr + (Render-Runs $t) + '</w:p>')
        $firstH1Seen = $true
    } elseif ($trim.StartsWith('- ')) {
        $t = $trim.Substring(2)
        [void]$body.AppendLine('<w:p><w:pPr><w:pStyle w:val="ListBulletStyle"/><w:numPr><w:ilvl w:val="0"/><w:numId w:val="1"/></w:numPr></w:pPr>' + (Render-Runs $t) + '</w:p>')
    } else {
        [void]$body.AppendLine('<w:p>' + (Render-Runs $trim) + '</w:p>')
    }
}

# ----------------------------------------------------------------------------
# Cover page (prepended)
# ----------------------------------------------------------------------------
$cover = @'
<w:p><w:pPr><w:spacing w:before="3000"/><w:jc w:val="center"/></w:pPr><w:r><w:rPr><w:sz w:val="72"/><w:b/><w:color w:val="1F6FEB"/></w:rPr><w:t xml:space="preserve">Plant Configurator</w:t></w:r></w:p>
<w:p><w:pPr><w:jc w:val="center"/></w:pPr><w:r><w:rPr><w:sz w:val="36"/><w:color w:val="57606A"/></w:rPr><w:t xml:space="preserve">User Manual</w:t></w:r></w:p>
<w:p><w:pPr><w:jc w:val="center"/><w:spacing w:before="600"/></w:pPr><w:r><w:rPr><w:sz w:val="24"/><w:color w:val="57606A"/></w:rPr><w:t xml:space="preserve">Version 1.0</w:t></w:r></w:p>
<w:p><w:pPr><w:jc w:val="center"/></w:pPr><w:r><w:rPr><w:sz w:val="24"/><w:color w:val="57606A"/></w:rPr><w:t xml:space="preserve">May 2026</w:t></w:r></w:p>
'@

$bodyText = $cover + $body.ToString()

$documentXml = @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
<w:body>
__BODY__
<w:sectPr>
<w:pgSz w:w="12240" w:h="15840"/>
<w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440" w:header="708" w:footer="708" w:gutter="0"/>
<w:cols w:space="708"/>
<w:docGrid w:linePitch="360"/>
</w:sectPr>
</w:body>
</w:document>
'@
$documentXml = $documentXml.Replace('__BODY__', $bodyText)

$stylesXml = @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
<w:docDefaults>
<w:rPrDefault><w:rPr><w:rFonts w:ascii="Calibri" w:hAnsi="Calibri"/><w:sz w:val="22"/><w:szCs w:val="22"/></w:rPr></w:rPrDefault>
<w:pPrDefault><w:pPr><w:spacing w:after="160" w:line="276" w:lineRule="auto"/></w:pPr></w:pPrDefault>
</w:docDefaults>
<w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:qFormat/></w:style>
<w:style w:type="paragraph" w:styleId="Heading1">
  <w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/>
  <w:pPr><w:keepNext/><w:spacing w:before="480" w:after="240"/><w:outlineLvl w:val="0"/></w:pPr>
  <w:rPr><w:rFonts w:ascii="Calibri Light" w:hAnsi="Calibri Light"/><w:b/><w:color w:val="1F6FEB"/><w:sz w:val="40"/></w:rPr>
</w:style>
<w:style w:type="paragraph" w:styleId="Heading2">
  <w:name w:val="heading 2"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/>
  <w:pPr><w:keepNext/><w:spacing w:before="320" w:after="160"/><w:outlineLvl w:val="1"/></w:pPr>
  <w:rPr><w:rFonts w:ascii="Calibri Light" w:hAnsi="Calibri Light"/><w:b/><w:color w:val="0A3D96"/><w:sz w:val="28"/></w:rPr>
</w:style>
<w:style w:type="paragraph" w:styleId="Heading3">
  <w:name w:val="heading 3"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/>
  <w:pPr><w:keepNext/><w:spacing w:before="200" w:after="120"/><w:outlineLvl w:val="2"/></w:pPr>
  <w:rPr><w:b/><w:color w:val="24292F"/><w:sz w:val="24"/></w:rPr>
</w:style>
<w:style w:type="paragraph" w:styleId="ListBulletStyle">
  <w:name w:val="ListBullet"/><w:basedOn w:val="Normal"/>
  <w:pPr><w:spacing w:after="80"/><w:ind w:left="360" w:hanging="360"/></w:pPr>
</w:style>
</w:styles>
'@

$numberingXml = @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:numbering xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
<w:abstractNum w:abstractNumId="0">
  <w:lvl w:ilvl="0">
    <w:start w:val="1"/>
    <w:numFmt w:val="bullet"/>
    <w:lvlText w:val="&#8226;"/>
    <w:lvlJc w:val="left"/>
    <w:pPr><w:ind w:left="360" w:hanging="360"/></w:pPr>
    <w:rPr><w:rFonts w:ascii="Symbol" w:hAnsi="Symbol" w:hint="default"/></w:rPr>
  </w:lvl>
</w:abstractNum>
<w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num>
</w:numbering>
'@

$contentTypesXml = @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
<Default Extension="xml" ContentType="application/xml"/>
<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
<Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>
<Override PartName="/word/numbering.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml"/>
</Types>
'@

$rootRelsXml = @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
</Relationships>
'@

$documentRelsXml = @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
<Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering" Target="numbering.xml"/>
</Relationships>
'@

if (Test-Path $outPath) { Remove-Item $outPath -Force }

$fileStream = [System.IO.File]::Create($outPath)
$zip = New-Object System.IO.Compression.ZipArchive($fileStream, [System.IO.Compression.ZipArchiveMode]::Create)

function Add-Entry($zip, $name, $content) {
    $entry = $zip.CreateEntry($name, [System.IO.Compression.CompressionLevel]::Optimal)
    $writer = New-Object System.IO.StreamWriter($entry.Open(), (New-Object System.Text.UTF8Encoding($false)))
    $writer.Write($content)
    $writer.Dispose()
}

Add-Entry $zip '[Content_Types].xml' $contentTypesXml
Add-Entry $zip '_rels/.rels' $rootRelsXml
Add-Entry $zip 'word/document.xml' $documentXml
Add-Entry $zip 'word/styles.xml' $stylesXml
Add-Entry $zip 'word/numbering.xml' $numberingXml
Add-Entry $zip 'word/_rels/document.xml.rels' $documentRelsXml

$zip.Dispose()
$fileStream.Dispose()

Write-Output ("Wrote " + $outPath)
Write-Output ("Size: " + (Get-Item $outPath).Length + " bytes")
