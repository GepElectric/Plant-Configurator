# Plant Configurator — predaja projekta

Ovo je **radni izvorni kod** aplikacije Plant Configurator. Sve što treba za nastavak razvoja je u ovoj mapi.

Snimljeno: **30.07.2026.**
Grana: **`feature/tia-v21-dual-helper`** — zadnji commit `b85067e` (25.06.2026.)
Provjereno prije predaje: `dotnet build -c Release` → **0 grešaka, 0 warninga**; `dotnet test` → **46/46 testova prolazi**.

---

## 1. Što je aplikacija

Zamjena za stari Excel `Configuration_Unit_List.xlsm`. Radi po logici TIA Portala:

- `PlantConfigurator.exe` — editor/generator
- `.plantcfg` — projektni file (JSON, verzioniran) sa svim konfiguracijskim podacima
- DB/DT/SCL izvorni fileovi se **generiraju iz koda**, ne iz Excel formula

Uz to radi i **dvosmjernu sinkronizaciju sa živim TIA Portalom** (Openness) i s **WinCC Unified SCADOM**.

---

## 2. Preduvjeti za razvoj

| Što | Verzija | Bilješka |
|---|---|---|
| .NET SDK | **8.0** (testirano 8.0.421) | za glavnu app (`net8.0-windows`, WPF) |
| .NET Framework 4.8 Developer Pack | 4.8 | ⚠️ vidi zamku #2 dolje |
| Visual Studio 2022 / Rider | bilo koji | opcionalno, `dotnet` CLI je dovoljan |
| **TIA Portal V18** + Openness | `C:\Program Files\Siemens\Automation\Portal V18\PublicAPI\V18\` | potrebno za build **V18 helpera** |
| **TIA Portal V21** + Openness | `C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48\` | potrebno za build **V21 helpera** |

⚠️ **Putanje do Siemens Openness DLL-ova su hardkodirane** kao `HintPath` u:
- `CwcConfigurator.TiaHelper/CwcConfigurator.TiaHelper.csproj` (V18)
- `CwcConfigurator.TiaHelper.V21/CwcConfigurator.TiaHelper.V21.csproj` (V21)

Ako TIA nije instaliran na tim putanjama, **build tog helpera pada**. Bez instaliranog V18 → prilagodi ili preskoči V18 helper (glavna app i dalje radi, samo neće moći na V18 procese).

---

## 3. Build — dvije zamke pri prvom buildu

Oba problema su **već riješena u ovoj mapi**, ali će se vratiti ako netko napravi svježi `git clone`. Zabilježeno da ne gubiš vrijeme:

### Zamka #1 — `Configuration_Unit_List_Template.xlsm` je u `.gitignore`

`CwcConfigurator/Templates/Configuration_Unit_List_Template.xlsm` **build zahtijeva** (`CopyToOutputDirectory` u csproj-u), ali ga `.gitignore` pravilo `Configuration_Unit_List*.xlsm` isključuje iz repozitorija.

Simptom svježeg clonea:
```
error MSB3030: Could not copy the file "...\Templates\Configuration_Unit_List_Template.xlsm"
because it was not found.
```

File **jest** u ovoj mapi (kopiran ručno). Trajni ispravak u `.gitignore`:
```gitignore
!CwcConfigurator/Templates/Configuration_Unit_List_Template.xlsm
```

### Zamka #2 — V21 helper nije u `.sln` → nema restore → MSB3644

`CwcConfigurator.TiaHelper.V21` **nije uvršten u `PlantConfigurator_Source.sln`**. Gradi se samo posredno, kroz `CopyTiaHelper` target u `CwcConfigurator.csproj`. Kako `dotnet restore` na solutionu ne dohvati taj projekt, ne restorira se implicitni paket `Microsoft.NETFramework.ReferenceAssemblies` i build pada:

```
error MSB3644: The reference assemblies for .NETFramework,Version=v4.8 were not found.
```

Rješenje = **restore i build kao dva odvojena koraka** (restore u istom MSBuild pozivu ne pomaže — `nuget.g.props` se importa prerano):

```powershell
dotnet restore PlantConfigurator_Source.sln
dotnet restore CwcConfigurator.TiaHelper.V21\CwcConfigurator.TiaHelper.V21.csproj
dotnet build PlantConfigurator_Source.sln -c Release --no-restore
```

Trajni ispravak: dodaj V21 projekt u `.sln`.

### Najlakše: `Build.ps1`

U korijenu ove mape je `Build.ps1` koji radi točno taj ispravan redoslijed:

```powershell
.\Build.ps1              # Release build
.\Build.ps1 -Test        # build + 46 unit testova
.\Build.ps1 -Publish     # + publish u .\publish_output (spremno za 02_Release)
.\Build.ps1 -Configuration Debug
```

### Ručno, sve komande

```powershell
# build
dotnet restore PlantConfigurator_Source.sln
dotnet restore CwcConfigurator.TiaHelper.V21\CwcConfigurator.TiaHelper.V21.csproj
dotnet build PlantConfigurator_Source.sln -c Release --no-restore

# testovi
dotnet test CwcConfigurator.Tests\CwcConfigurator.Tests.csproj -c Release

# release paket (isto što je u ..\02_Release)
dotnet publish CwcConfigurator\CwcConfigurator.csproj -c Release -o <izlazna_mapa>
```

Build glavne app automatski (`CopyTiaHelper` target) gradi **oba** helpera i stavlja ih u `TiaHelper\V18\` i `TiaHelper\V21\` uz `.exe`.

---

## 4. Arhitektura — zašto je podijeljeno na 3 projekta

| Projekt | Target | Uloga |
|---|---|---|
| `CwcConfigurator` | `net8.0-windows`, WPF | glavna aplikacija. **Nula Siemens referenci.** |
| `CwcConfigurator.TiaHelper` | `net48` | Openness helper za **TIA V18** |
| `CwcConfigurator.TiaHelper.V21` | `net48` | Openness helper za **TIA V21** (linka *isti* `Program.cs`, samo drugi define i druge reference) |
| `CwcConfigurator.Tests` | `net8.0-windows` | unit testovi (46) |

**Ključna odluka:** TIA Openness je `net48`-only i version-specific. Zato glavna app **nikad** ne referencira Siemens assemblyje — sav Openness rad ide u **zasebnom procesu** (`CwcConfigurator.TiaHelper.exe`), komunikacija **JSON preko stdin/stdout**. Posljedice:

- app ostaje jedan `net8.0` build bez obzira na TIA verzije
- pad Opennessa ne ruši aplikaciju
- **selekcija verzije ide po TIA procesu, ne globalno**: app pita oba helpera za listu procesa, spoji ih i označi verzijom; attach na proces onda rutira sve komande helperu te verzije

Relevantno: `Services/TiaSync/TiaSessionService.cs` (per-version helper procesi, rutiranje), `TiaApiLocator.cs` (detekcija instaliranih verzija), `TiaSyncContracts.cs` (DTO-i i interface).

**Isto ime exea** u oba helpera (`AssemblyName=CwcConfigurator.TiaHelper`) — namjerno, app ih razlikuje po mapi `TiaHelper/<V18|V21>/`.

### WinCC Unified — dva odvojena puta

1. **JS-Connector REST** (`Siemens.Simatic.Hmi.Openness.RestService.exe` v21, bundlan u `CwcConfigurator/Tools/WinCCUnifiedJsConnector/`, sluša na `localhost:7979`) — koristi se za round-trip SCADA skripte `AUX_FUNCTIONS._SET_INTERNAL_TAG`.
2. **Engineering Openness** (kroz TiaHelper V21, `Siemens.Engineering.WinCCUnified.dll`) — koristi se za katalog screenova. REST **nema** endpoint za screenove ni za text liste — provjereno, vidi `TIA_V21_Upgrade_Plan.md`.

---

## 5. Karta koda

```
CwcConfigurator/
  Domain/
    CwcProject.cs ............ (80 KB) SRCE — cijeli projektni model, ono što se
                               serijalizira u .plantcfg
    SignalFactory.cs, HardwareLimits.cs, ObservableModel.cs
  Services/
    ProjectFileService.cs .... open/save .plantcfg
    ProjectValidator.cs ...... validacija prije generiranja
    Validation/*RepairEvaluator.cs ... auto-popravci po tablici (DI/DO/MEAS/AO/ALM/PAR)
    TiaSourceGenerator.cs .... (60 KB) generiranje DB/DT/SCL izvornih fileova
    TiaPortalExportImporter.cs (62 KB) parser TIA exportanih .db/.udt/.scl
    TiaSync/
      TiaSessionService.cs ... pokretanje i rutiranje helpera po verziji
      TiaLiveSyncService.cs .. (78 KB) živi pull/push, fingerprinti, diff
      TiaMappingService.cs ... mapiranje app baza <-> TIA uređaja
      TiaStaDispatcher.cs .... STA thread za Openness pozive
    ScadaConfigFileService.cs  (69 KB) parser/generator "set internal tag" JS-a
    ScadaNavigationModel.cs .. model navigacije (Navigation Builder)
    ScadaUnifiedSyncService.cs Pull/Push SCADA konfiguracije preko REST-a
    UnifiedJsConnectorRuntime.cs  REST klijent
    DescriptionTextListService.cs / ScadaTextListPushService.cs  text liste
    ExcelProjectImporter/Exporter.cs  Excel put (legacy kompatibilnost)
    AlarmTesting/ ............ ⚠️ NEDOVRŠENO, vidi točku 6
  App/
    MainWindow.xaml .......... (302 KB) cijeli UI — svi tabovi
    MainWindow.xaml.cs ....... (490 KB) ⚠️ najveći file u projektu
    MainWindow.*.cs .......... partial klase po temi (Autosave, FindReplace,
                               GridBulkOps, Problems, SettingsHub, Filters, ...)
    *Dialog.xaml(.cs) ........ dijalozi
    NavigationEditorWindow ... SCADA Navigation Builder
    Settings/ ................ view-ovi za Settings hub
  Templates/, Scripts/, Tools/, Installers/, Themes/
```

**Napomena o `MainWindow.xaml.cs`:** 490 KB u jednom fileu. Pri daljnjem radu nastavi obrazac postojećih `MainWindow.<Tema>.cs` partial klasa — ne dodavaj u glavni file.

---

## 6. ⚠️ Gdje je razvoj stao — NECOMMITANI RAD

`git status` u ovoj mapi namjerno **nije čist**. To je vjerna slika gdje je posao prekinut (25.–30.06.2026.):

```
 M CwcConfigurator/App/MainWindow.xaml.cs
 M CwcConfigurator/CwcConfigurator.csproj
?? CwcConfigurator/Services/AlarmTesting/
?? CwcConfigurator/App/AlarmTesterDialog.xaml
?? CwcConfigurator/App/AlarmTesterDialog.xaml.cs
?? CwcConfigurator.Tests/AlarmTestPlanBuilderTests.cs
```

**Tema: Alarm Tester** — `Tools → Alarm Tester...`. Automatsko testiranje alarma: gradi plan testa iz ALM baze i forsira vrijednosti u PLC-u da provjeri javljaju li se alarmi.

- `AlarmTestPlanBuilder.cs` — gradnja plana (ima testove, prolaze)
- `AlarmTesterS7Client.cs` — put preko S7 protokola (S7.Net)
- `AlarmTesterOpenPipeClient.cs` (19 KB) — put preko OpenPipe
- `PlcWebApiClient.cs` — put preko PLC Web API
- `AlarmTestModels.cs` — modeli
- `AlarmTesterDialog.xaml(.cs)` — UI

Stanje: **kompajlira se, testovi prolaze, ali nije verificirano na živom PLC-u.** Prije nastavka: provjeri koji je od tri klijenta (S7 / OpenPipe / WebAPI) zamišljen kao glavni put — sva tri su započeta.

Prvi korak za novog programera: `git diff` i `git status` da vidiš točan obim, pa odluči hoćeš li commitati kao WIP ili dovršiti.

---

## 7. Otvorene teme (roadmap)

Sve odluke, istrage i obrazloženja su u **`TIA_V21_Upgrade_Plan.md`** — to je glavni radni dokument, čitaj ga prije bilo kakve promjene na TIA/SCADA dijelu. Sadrži i teme koje su **odbačene i zašto** (vrijedi da se ne kreće opet u ćorsokak).

Završeno:
- ✅ Faza 1 — migracija V18→V21, dual-helper (`c8023d0`)
- ✅ #10 — SCADA round-trip `_SET_INTERNAL_TAG` preko Unified REST (`e0f65b3`)
- ✅ #11 — katalog screenova za nav dropdownove (`62e9d2e`)
- ✅ R1 — odabir Unified uređaja + reorg SCADA taba
- ✅ R2 — Navigation Builder (`a8abb77`)

Ostaje:
- **R3** — vizualni PLC rack/slot editor (grey/white klik-mreža) + validacije.
  Bonus ideja zabilježena u planu: grey/white stanje modula se može **izvući iz PLC pulla** i automatski applyati na SCADA interne tagove, umjesto ručnog unosa.
- **#9** — automatski export/import descriptiona (text lista). Blokirano na tome što Engineering text-list export daje **XML**, a app očekuje `Unified_text_lists.xlsx` → treba konverzija/most. Zasebna istraga. REST put je provjeren i **ne radi** (nema endpoint).
- **#8** — `ExportProjectTexts` (prijevodni tekstovi) — odgođeno.

---

## 8. Ostali dokumenti u ovoj mapi

| File | Što je |
|---|---|
| `TIA_V21_Upgrade_Plan.md` | **glavni radni dokument** — sve odluke o TIA/SCADA razvoju, s obrazloženjima |
| `Plant_Configurator_Manual.docx` | korisnički priručnik |
| `CwcConfigurator/README.md` | ⚠️ **zastario** — piše "initial skeleton only" i "nema .NET SDK". Ne vjeruj mu, ostavljen samo kao originalni dokument. |
| `PlantConfigurator.plantcfg` | veliki (31 MB) referentni/testni projekt |
| `CwcConfigurator/sample.cwcproj` | mali sample projekt |
| `TIA_SYNC/` | arhiva starih TIA sync runova (traceovi iz 05/2026). Dijagnostika, nije potrebno za build. |

---

## 9. Praktične napomene

- **Radi na lokalnom disku, ne direktno u OneDriveu.** OneDrive sinkronizacija i build koji stvara/briše tisuće fileova u `bin`/`obj` idu jedno drugom na živce (sporo, lockovi, konflikti). Kopiraj mapu na lokalni disk, radi tamo.
- **Git povijest je uključena** (25 commitova, od `05f41ca`, 10.06.2026.). `origin` remote je namjerno uklonjen jer je pokazivao na privatnu putanju na starom računalu. Dodaj vlastiti remote kad zatreba.
- Grana `master` (`1252bcb`) **zaostaje** za `feature/tia-v21-dual-helper`. Radna grana je `feature/tia-v21-dual-helper` — na njoj je sve iz točke 7.
- `bin/` i `obj/` su počišćeni. Prvi build ih ponovno stvara.
- `CwcConfigurator/Tools/python` je **ugrađeni Python runtime** (65 MB) koji app koristi za generiranje ALM/PAR dokumentacije (`Scripts/alm_par_doc.py`). Ide u output, nije opcionalan.
- `CwcConfigurator/Installers/` sadrži offline instalere .NET-a (171 MB) koji se kopiraju u release paket — zato je `02_Release` velik.
- `csproj` uslovno referencira `..\..\tag_browser\dist\WinCC_Tag_Browser.exe`. Ta mapa **nije** dio ovog repozitorija; referenca je `Condition="Exists(...)"` pa build prolazi i bez nje. Tag Browser tada nije dostupan iz menija `WinCC`.
