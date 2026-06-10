# Plant Configurator

This is the planned C# replacement for `Configuration_Unit_List.xlsm`.

The application is intended to work like TIA Portal:

- `PlantConfigurator.exe` is the editor/generator application.
- `.plantcfg` is the project file containing configuration data.
- Generated DB/DT/source files are produced from code, not from editable Excel formulas.

## Why This Exists

Excel remains useful as the reference implementation, but it is not safe enough as the final operator tool. A user can accidentally change formulas, overwrite helper cells, or move table columns. In the C# version:

- tables have fixed typed models;
- calculated fields are produced by code;
- validation runs before file generation;
- missing external files stop the action cleanly;
- text length problems point to the exact table/row/value;
- project data is stored in a versioned `.plantcfg` JSON file.

## Current Status

Initial skeleton only:

- WPF shell
- project open/save
- `.plantcfg` JSON model
- fixed table model for DI, DO, AI, RTD, COUNTER and AO
- first validation layer

The local machine currently has .NET runtimes installed, but no .NET SDK, so building the final `.exe` requires installing the .NET SDK or Visual Studio Build Tools.
