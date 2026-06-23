# TIA Openness V21 — Plan upgradea aplikacije

Radni dokument. Prolazimo temu po temu; **detaljan plan/scope upisuje se tek kad temu zaključimo.**
Implementacija ide u fazama, nakon što prođemo sve teme.

Kontekst: prešli smo s TIA Portal V18 na V21. Cilj: iskoristiti nove Openness značajke gdje imaju smisla za ovu aplikaciju (CwcConfigurator + TiaHelper).

## Status tema

| # | Tema | Status |
|---|------|--------|
| 1 | Migracija TiaHelpera V18 → V21 (preduvjet) | ✅ **Zaključeno — plan dolje**; obuhvaća i #3 i #5 |
| 2 | `.s7dcl` SIMATIC Source Documents (diff/git) | ❌ **Odbačeno** (vidi dolje) |
| 3 | Version-independent import sintaksa | 🔀 **Spojeno u temu #1** (migracija) |
| 4 | Export/import know-how zaštićenih blokova | ❌ **Odbačeno** (ne pogađa workflow — samo konfig podaci, ne zaštićena logika) |
| 5 | Modularni assemblyji (stabilnost helpera) | 🔀 **Spojeno u temu #1** (migracija) |
| 6 | Download / Upload na PLC iz aplikacije | ❌ **Odbačeno** (korisnika ne zanima) |
| 7 | Snapshot → start values / žive vrijednosti prije pulla | ❌ **Odbačeno** (live read ne ide kroz Openness → nepouzdano) |
| 8 | `ExportProjectTexts` (prijevodni tekstovi) | ⏸️ **Odgođeno** — za neki drugi put |
| 9 | WinCC Unified text liste — automatski export | ⚠️ **Plan ispravljen** — REST nema text liste → ide Engineering put (kao #11) |
| 10 | "Set internal tag" JS skripta — round-trip preko Unified Openness | ✅ **Zaključeno** — modul `AUX_FUNCTIONS`, funkcija `_SET_INTERNAL_TAG` |
| 11 | Lista screenova za navigacijske dropdownove (uz #10) | ✅ **Zaključeno** — REST nema screen katalog → Engineering put (HmiSoftware.ScreenFolder) |

---

## Tema 2 — `.s7dcl` SIMATIC Source Documents → ❌ ODBAČENO

**Datum odluke:** 2026-06-23

**Što je tema bila:** prelazak izvoza/uvoza TIA blokova sa starog `GenerateSource` (`.db`/`.udt`/`.scl`) na novi tekstualni format `.s7dcl` (V20+), radi: (a) robusnijeg parsiranja, (b) diffa logičkih blokova, (c) pravog git redak-po-redak diffa.

**Zašto odbačeno:**
- Workflow aplikacije je: export → izmjena u tablicama → import; korisnik zna da je **zadnja verzija u aplikaciji**. Diff između tablica i TIA DB-ova mu **ne znači** ništa.
- Aplikacija upravlja **samo konfiguracijskim DB-ovima** (DI/DO/MEAS/ALM/PAR/AO/SEQUENCE/HW), **ne logikom** → benefit "diff logike" otpada.
- Git **nije** dio aplikacije; "git za PLC" marketinški benefit `.s7dcl`-a ovdje ne pogađa.
- Grid **već jest** view podataka; čitljiv tekstualni diff konfiguracije nije potreban.
- Jedini preostali benefit (robusnost parsiranja) ne opravdava **prepisivanje cijelog `TiaPortalExportImporter` regex parsera** na novu sintaksu — postojeći parser radi, a stari `GenerateSource` format postoji i u V21.

**Napomena za budućnost:** ako ikad zatreba čitljiv "što se promijenilo" prikaz za konfig DB-ove, gradi se **na postojećem strukturiranom modelu** (`DbCatalog` retci + postojeći fingerprinti u `TiaLiveSyncService`), **bez** `.s7dcl`-a i bez V21 ovisnosti. Git "plant history" je zasebna potencijalna tema, neovisna o `.s7dcl`.

---

## Tema 9 — WinCC Unified text liste: round-trip descriptiona → ✅ ZAKLJUČENO

**Datum odluke:** 2026-06-23

### Cilj
Ukloniti ručni izvoz/uvoz `Unified_text_lists.xlsx` iz/u WinCC Unified. Descriptioni (ALM, PAR i ostale mapirane liste) round-trip-aju između aplikacije i žive SCADE jednim klikom.

### Polazno stanje (postojeće, ne dirati osim gdje navedeno)
- **App ↔ xlsx već dvosmjerno:** `DescriptionTextListsDialog.Load` → `DescriptionTextListService.ImportWorkbook` → `AutoMapEmptyDescriptionLists` → `ApplyDescriptionsToProject`; `SaveWorkbook` → `PushDescriptionsIntoTextLists` → `SaveWorkbook`.
- **Unified Openness REST servis se već isporučuje i pokreće:** `Siemens.Simatic.Hmi.Openness.RestService.exe` **v21.0.7623.4176** (dakle V21 — "advanced XML formats for text lists" dostupan), na `http://localhost:7979`, preko `UnifiedJsConnectorRuntime`. Trenutno se koristi **samo** `GET tiaProcesses`.
- **PLC vs SCADA su dvije odvojene trake** (različit attach, granularnost, validacija) — ostaju odvojene.

### Arhitektonske odluke
1. **Unified attach ide u TIA SYNC prozor** (`TiaSyncDialog`) kao paralelna "WinCC Unified" sekcija pored postojećeg PLC attacha. Razlog: taj prozor je već connection-hub (scan procesa, Attach/Disconnect, drži session).
2. **Unified session se drži u dijeljenom servisu** (npr. `IUnifiedSessionService`, wrap nad `UnifiedJsConnectorRuntime`), registriran/dijeljen kao i `ITiaSessionService`, da ga `DescriptionTextListsDialog` može koristiti bez vlastitog attacha.
3. **Descriptions dialog dobiva 2 nove tipke:** **Pull descriptions** i **Push descriptions**. Pokrivaju cijeli text-list workbook (ALM, PAR + ostale mapirane liste).
4. **Model smjera = eksplicitan, bez auto-merge.** Pull prepisuje app polja iz SCADE; Push prepisuje SCADA liste iz app-a. Korisnik bira smjer klikom → nema skrivene logike rješavanja konflikata u v1. (Conflict-preview = moguće buduće proširenje.)

### Tok operacija
**Pull descriptions (SCADA → app):**
1. Zahtijeva aktivnu Unified session (attach u TIA SYNC prozoru).
2. REST poziv: text-list **export** → `Unified_text_lists.xlsx` u temp.
3. Provući kroz postojeći `ImportWorkbook` → `AutoMapEmptyDescriptionLists` → `ApplyDescriptionsToProject`.
→ ALM/PAR/… description polja osvježena iz žive SCADE.

**Push descriptions (app → SCADA):**
1. `SaveMappings` + `PushDescriptionsIntoTextLists` (app gradi xlsx).
2. REST poziv: text-list **import** s tim xlsx → u WinCC Unified.
→ SCADA text liste osvježene iz app-a.

**Redoslijed kod punog osvježavanja:** descriptioni su ključani po ID-evima koje definiraju DB-ovi → logički prvo PLC pull (ID-evi), pa Pull descriptions (tekst). Bez auto-orkestracije u v1; ostaju odvojeni gumbi.

### Otvorena istraživačka točka (riješiti na početku implementacije)
- **Točan REST endpoint za text-list export/import** na `Siemens.Simatic.Hmi.Openness.RestService` — servis nudi više od `tiaProcesses`; identificirati rutu (RestService.xml / Siemens HMI Openness V21 docs). Ne izmišljati rutu prije potvrde.

### Scope / dodir s kodom
- `TiaSyncDialog.xaml(.cs)` — nova "WinCC Unified" sekcija (scan/attach/disconnect Unified procesa).
- Novi `IUnifiedSessionService` + implementacija (wrap `UnifiedJsConnectorRuntime`, drži CurrentUnifiedSession).
- `UnifiedJsConnectorRuntime` — dodati metode za text-list export/import (novi endpointi pored `tiaProcesses`).
- `DescriptionTextListsDialog.xaml(.cs)` — 2 tipke (Pull/Push descriptions) koje koriste dijeljenu Unified session + postojeći Import/Save put.
- DI/registracija dijeljenog servisa.

### Izvan scopea (v1)
- "Sync sve" (PLC+SCADA jednim klikom) — odbačeno za sada.
- Conflict-preview / merge descriptiona — moguće buduće proširenje.
- Push smjer #8 `ExportProjectTexts` (prijevodni tekstovi projekta) — zasebna odgođena tema.

---

## Tema 10 — "Set internal tag" JS skripta: round-trip preko Unified Openness → 🟡 ZA OBRADU

**Dodano:** 2026-06-23 (study notes; tema još nije zaključena)

### Što je to
WinCC Unified JS skripta ("set internal tag") koja preko internih tagova konfigurira UI ponašanje SCADE. Format (`ScadaConfigFileService`):
- sekcije `///// SECTION /////`
- varijable `let VAR = vrijednost; // komentar`
- upisi `Tags.SysFct.SetTagValue("TAG", VAR);`

### Što konfigurira
- **MAIN_NAV** glavni navigacijski gumbi (≤16)
- **Vidljivi screenovi po gumbu** — matrica `SCREENS_Button_X.ENTRY_Y` (16×16)
- **HEADER_TABS / HEADER_TAB** header tabovi
- **LOCATION** (0..10) + source/entry
- **TOP_NAV_CARD** po gumbu (TEXT_LANGUAGE_1/2, GRAPHIC_LIST_ID)
- **PLC rack/slot mapa modula** (grey/white) po lokaciji
- **SIDE_CONTENT** default screenovi + generirani brojači (`MAIN_NAV_BUTTONS_NUM`, `BUTTON_X_SCREEN_NUM`)

### Trenutni tok (file-based, ručno)
- Import: `ScadaConfigFileService.Import(.js)` (`MainWindow.xaml.cs:3590`) → parsira u `ScadaDatabase` (Variables + TagWrites), normalizira indeksirane grupe.
- Uređivanje u gridovima (ScadaOverviewGrid…).
- Export: `Export(.js)` → `SCADA_configuration.js` (`MainWindow.xaml.cs:3654`).
- Korisnik **ručno kopira skriptu iz/u WinCC Unified script editor.**

### Ideja teme
Automatizirati round-trip skripte preko **Unified Openness REST** (ista infrastruktura kao #9: bundlani V21 servis, attach u TIA SYNC prozoru) → Pull/Push "set internal tag" skripte umjesto ručnog copy-paste.

### Feasibility pitanje #1 (istražiti prije zaključka)
Izlaže li WinCC Unified Openness REST **sadržaj skripte** (globalni script / scheduled task) za čitanje/pisanje? Text liste su first-class Openness objekti; tijelo proizvoljne skripte možda nije dostupno preko REST servisa.
- Ako DA → analogno #9 (Pull/Push gumbi + REST endpoint).
- Ako NE → fallback: export/import screen ili global-definitions XML-a, ili ostati file-based uz poluautomatizaciju.

### Otvorena pitanja za diskusiju
- Smjer (pull/push/round-trip)?
- Gdje žive gumbi (uz postojeći SCADA grid u MainWindow)?
- Source-of-truth (app vs SCADA), kao kod #9.

---

### Tema 10 — PLAN (feasibility potvrđena, čeka 1 podatak)

**Datum:** 2026-06-23

**Feasibility — POTVRĐENO** preko doca bundlanog `Siemens.Simatic.Hmi.Openness.RestService` (V21):
- `GET /export/unifiedDevices?processId=X` — "Exports the script modules by device" (tu je "set internal tag").
- `POST /import/unifiedDevices?processId=X` — "Imports the script modules by device" (per-module poruke uspjeha).
- Bonus podržano: Screens i Scheduler export/import, libraryTypes export/import.
- App trenutno zove samo `GET tiaProcesses` → fale samo pozivi ova dva endpointa.

**Tok — Pull SCADA config (Unified → app):**
1. Aktivna Unified session (attach u TIA SYNC, dijeljeno s #9).
2. `GET /export/unifiedDevices` → script moduli u root folder (postojeći `UnifiedJsConnectorFolder`).
3. Locirati "set internal tag" modul → `ScadaConfigFileService.Import(file)` → gridovi.

**Tok — Push SCADA config (app → Unified):**
1. `ScadaConfigFileService.Export(_project.Scada, moduleFile)` u export folder.
2. Backup prethodne verzije modula.
3. `POST /import/unifiedDevices` → natrag u Unified.

**Što je dobro odraditi (preporuke):**
1. Ciljati SAMO "set internal tag" modul — ne dirati ostale (import je per-module).
2. Dijeliti `IUnifiedSessionService` / attach iz #9 → #9 i #10 raditi zajedno.
3. 2 tipke uz postojeći SCADA grid u MainWindow: "Pull SCADA config" / "Push SCADA config".
4. Zadržati file-based Import/Export kao fallback (offline).
5. Backup modula prije push-a (rollback, pišemo u živu SCADU).
6. Auto-lociranje modula po imenu; ako više/nejasno → korisnik bira.
7. Eksplicitan smjer, bez auto-merge (kao #9).

**Scope / dodir s kodom:**
- `UnifiedJsConnectorRuntime` — dodati `ExportScriptModulesAsync` / `ImportScriptModulesAsync` (/export|/import/unifiedDevices).
- Dijeljeni `IUnifiedSessionService` (zajedno s #9).
- `MainWindow` SCADA dio — 2 tipke + glue na `ScadaConfigFileService`.
- Konfiguracija: naziv "set internal tag" modula za ciljanje.

**Cilj (potvrđeno):**
- Globalni modul: **`AUX_FUNCTIONS`**. Funkcija: **`_SET_INTERNAL_TAG`** (unutar modula, uz "Global definition area" i moguće druge funkcije).

**Granularnost (bitno):** Openness export/import je na razini **modula** (`/export/unifiedDevices` izvozi cijeli `AUX_FUNCTIONS`), ne pojedine funkcije. Zato:
- **Pull:** iz exportanog `AUX_FUNCTIONS` modula izdvojiti **samo tijelo `_SET_INTERNAL_TAG`** → `ScadaConfigFileService.Import`.
- **Push:** generirani sadržaj upisati natrag **baš u `_SET_INTERNAL_TAG`** (function-splice), a **ostatak modula** (Global definition area + druge funkcije) **ostaviti netaknutim** → import cijelog modula.
- ⚠️ Sigurnosno kritično: NE pregaziti cijeli `AUX_FUNCTIONS` samo tom funkcijom.

**Za potvrditi pri implementaciji:** točan format exporta modula (wrapper oko funkcije, npr. `export function _SET_INTERNAL_TAG() {…}`) da splice ispravno pronađe granice funkcije. Trenutni file-based tok vjerojatno koristi samo tijelo funkcije — provjeriti razliku.

**Pretpostavke (default da, reci ako ne):** round-trip (pull+push); backup modula prije push-a.

**Ovisnost:** dijeli Unified attach infrastrukturu s #9 → #9 i #10 implementirati zajedno.

---

## Tema 1 — Migracija V18 → V21 (jedna app, dva helpera) → ✅ ZAKLJUČENO

**Datum odluke:** 2026-06-23

### Arhitektonska odluka
- **Jedna app, dva helper builda** (V18 + V21). Glavna app (`CwcConfigurator`) ne referencira Openness — sav rad je u zasebnom procesu `CwcConfigurator.TiaHelper.exe` (JSON preko stdin/stdout). Zato app ostaje jedinstvena.
- **Selekcija helpera po PROCESU, ne globalno.** `GetProcesses()` je version-specific → app pita oba helpera, spoji + označi procese verzijom; attach na proces rutira na helper te verzije kao aktivnu session.
- **V18 ostaje (za sad)** → pravi održiv dual-build. Izlaz kasnije = obrisati V18 build + jednu granu selekcije; app netaknuta.

### Polazno stanje (potvrđeno)
- `CwcConfigurator.csproj` — nula Siemens referenci; AfterBuild builda helper i kopira `net48` output u `TiaHelper/`.
- `TiaSessionService` — pokreće helper `Process.Start`, već prosljeđuje `CWC_TIA_PORTAL_VERSION = TiaApiLocator.GetPreferredVersionLabel()`.
- Helper `GetTiaApiDirectory()` — **hardkodira V18** putanju (`Program.cs:1759`); jedina stvar koju treba poverziti.
- `TiaApiLocator` već detektira V18 i V21 instalacije.

### Scope / posao

**A. Helper restruktura (V18 + V21 build)**
- Dijeljeni source (`Program.cs` + DTO-i) + dva csproj-a (`...TiaHelper.V18`, `...TiaHelper.V21`) koja linkaju isti source.
- V18 csproj: monolitni `Siemens.Engineering.dll` (+ `.Hmi`) iz `Portal V18\PublicAPI\V18`; define `TIA_V18`.
- V21 csproj: modularni `Siemens.Engineering.Base.dll` + `Step7.dll` (+ HMI po potrebi) iz `Portal V21\PublicAPI\V21\net48`; token `29bfe5fdf4ba5d3b`; define `TIA_V21`.
- `GetTiaApiDirectory()` čita `CWC_TIA_PORTAL_VERSION` (već se prosljeđuje) umjesto hardkodiranog V18; resolva pravu putanju.

**B. API divergence audit (kompajlirati V21 helper rano)**
- Provjeriti svaki Openness poziv helpera protiv V21: `GetService<OnlineProvider/CrossReferenceService/SoftwareContainer/ICompilable>`, `ExternalSourceGroup.GenerateSource/CreateFromFile/GenerateBlocksFromSource`, `ImportProjectTexts` (reflection).
- Očekivano malo `#if` — uglavnom enum preimenovanja (Celcius→Celsius, micros→Microseconds, Milisecond→Milliseconds…). Popraviti gdje pukne.

**C. Build & packaging**
- `CwcConfigurator.csproj` AfterBuild: buildati OBA helpera, kopirati u `TiaHelper/V18/` i `TiaHelper/V21/`.

**D. App-side selekcija & rutiranje (`TiaSessionService`, `TiaApiLocator`, `TiaSyncDialog`)**
- `TiaApiLocator` — vratiti SVE instalirane verzije (ne "preferiraj jednu").
- Process listing: pitati oba instalirana helpera, spojiti, označiti svaki proces verzijom.
- Dodati `Version` u `TiaPortalProcessInfo` (helper DTO + app DTO).
- Session: Attach(processId, version) → pokrenuti/držati helper te verzije; rutirati sve naredbe njemu. Ukloniti pretpostavku "jedan globalni helper".
- `TiaSyncDialog` — prikazati verziju po procesu u listi.

**E. Edge cases**
- Samo jedna verzija instalirana → kao danas, ali version-aware.
- Obje instalirane/pokrenute → spojena lista, rutiranje po izabranom procesu.
- Helper za verziju nedostaje → uredna poruka.

### Izvan scopea
- Korištenje V21-only značajki u helperu (s7dcl itd.) — migracija je samo "neka radi i na V21", ne "iskoristi nove mogućnosti".

### Rizici
- Više API divergencija nego očekivano → mitigacija: kompajlirati V21 helper rano (korak B) prije ostatka.

### Ovisnosti
- Neovisno o #9/#10 (PLC traka). Može se raditi prvo (otključava da TIA SYNC uopće radi na V21).

---

## Tema 11 — Lista screenova za navigacijske dropdownove (vezano uz #10) → 🟡 ZA OBRADU / plan skiciran

**Dodano:** 2026-06-23

### Cilj
Pri konfiguraciji navigacije u app-u (nova standardizirana `_SET_INTERNAL_TAG` funkcija — meni, screenovi po gumbu) nuditi **dropdown/search dostupnih screenova** umjesto free-text → povući **katalog screenova** iz WinCC Unified.

### Nalaz (doc bundlanog REST servisa)
- `GET /unifiedDevices?processId=X` vraća script module (`GlobalModuleList`), **ne** listu screenova.
- Modeli `UnifiedScreen`/`UnifiedScreenGroup` (Name, nested grupe) postoje; `UnifiedDeviceEntity.Screens` = "full qualified screen names to be imported/exported" — ali screenovi se surface u kontekstu script-scana (`excludeNestedScreensNotContainingScripts`), ne kao čist navigacijski katalog.
- Zaključak: REST nije pouzdan izvor pune liste screenova.

### Putevi
- **Put A (preporuka, pouzdano):** enumerirati preko WinCC Unified **Engineering Openness** — `HmiSoftware.ScreenFolder` → screens + nested ScreenGroups — kroz extension **TiaHelpera** (već referencira `Siemens.Engineering.Hmi`; dodati `Siemens.Engineering.HmiUnified`). Novi helper command `GetUnifiedScreens`. Ovisi o #1 (HMI Unified reference po V18/V21).
- **Put B (verify-first, jeftinije):** može li `/export/unifiedDevices` sa screens-entitetom vratiti pune nazive screenova → ako da, reuse #9/#10 REST infrastrukture.

**Odluka:** verify-first put B; ako ne da pun popis → put A.

### Scope
- (Put A) TiaHelper: `GetUnifiedScreens` command (enumeracija HMI Unified screenova).
- App: katalog screenova (refreshable) → dropdown/search u SCADA navigacijskom UI-u (gdje se podešava matrica screenova po gumbu).
- Veže se na #10 UI i na novi format `_SET_INTERNAL_TAG`.

### Napomena (utječe na #10)
Razvijena je **nova, standardiziranija** `_SET_INTERNAL_TAG` funkcija. #10 implementacija mora ciljati **novi format** (sekcije/varijable koje `ScadaConfigFileService` parsira mogu se razlikovati od trenutnog). Provjeriti novi format prije #10 splice/parse logike.

---

## Živa TIA istraživanja (2026-06-23) — potvrde za #10 i #11

Spojeno na otvoreni V21 projekt (`Kosovo_zahvat_V21`, SCADA uređaj `SCADA_PC`) preko bundlanog `Siemens.Simatic.Hmi.Openness.RestService` na :7979.

### #10 — format `_SET_INTERNAL_TAG` (POTVRĐENO)
- `GET /unifiedDevices?processId=X` → moduli: AUX_FUNCTIONS, TRENDS, USER_INFO, STATUS, MEASUREMENT, COPY_UDT, REPORTS. (Bez screenova.)
- `POST /export/unifiedDevices` (body: RootPath + Entities[{DisplayName, DeviceName, GlobalModuleList:["AUX_FUNCTIONS"]}]) → izvozi:
  - `AUX_FUNCTIONS.hmi.js` (5569 linija, 2707 `SetTagValue` poziva)
  - `AUX_FUNCTIONS.hmi.yml` (meta: `ScriptModules: AUX_FUNCTIONS: ScriptFile: AUX_FUNCTIONS.hmi.js`)
- Struktura `.hmi.js`:
  - Linije 1-16: **Global definition area** (`export let …` konstante).
  - Linije 17-20: **auto-generirani marker** "End of Global definition area" — NE DIRATI (import oslonjen na njega).
  - Linija 21: `export function _SET_INTERNAL_TAG(parameter1, parameter2) {` (JEDINA funkcija u modulu).
  - Tijelo: sekcije `///// MAIN /////` + `let VAR = value;  //  komentar` + `Tags.SysFct.SetTagValue("TAG", VAR);` — **identično formatu `ScadaConfigFileService`**.
  - Linija 5569: `}` (jedini top-level close).
- **Round-trip dizajn (potvrđen):**
  - Pull: export → split `.hmi.js` na [header = global area + signatura | body | footer = `}`] → body u `ScadaConfigFileService.Import`; header+footer spremiti verbatim.
  - Push: `ScadaConfigFileService.Export` → body → re-wrap s verbatim header+footer → `import/unifiedDevices`.
  - Sigurno: global area + marker + signatura + ostali moduli ostaju netaknuti (splice samo body jedine funkcije).
- ⚠️ Provjeriti razliku trenutnog file-based toka (vjerojatno samo body) vs cijeli `.hmi.js`.

### #11 — screenovi (POTVRĐENO: REST put NE radi)
- Nema screen-katalog endpointa (`GetTiaProcesses`, `GetUnifiedDevices`, Export/Import UnifiedDevices/LibraryTypes, GetPing — nema GetScreens).
- `/unifiedDevices` vraća samo `GlobalModuleList`; `/export` izvozi samo script module.
- → **Put B (REST) odbačen. Ide Put A: Engineering Openness** (`HmiSoftware.ScreenFolder` → screens + nested ScreenGroups) kroz extension TiaHelpera (+`Siemens.Engineering.HmiUnified`). Ovisi o #1.

### Edit interface (postojeće stanje)
- `ScadaTab` već ima: `ScadaTagsGrid`, `ScadaGeneralSettingsPanel` (Bilingual + 2. jezik), `ScadaLocationsGrid` (Local/Remote), `ScadaHeaderGrid`, `ScadaMainNavGrid`, `ScadaNavigationGrid`, `ScadaOverviewGrid`, `ScadaVariablesGrid`, + SCADA DB tree.
- Zaključak: edit interface **uglavnom postoji**. Dodati: (a) Pull/Push gumbe (#10), (b) screen dropdown/search u navigacijskim gridovima (#11).

---

## FAZA 1 — Migracija V18→V21: IMPLEMENTIRANO (2026-06-23)

Branch: `feature/tia-v21-dual-helper`. Build čist (0 grešaka), verificirano na stroju s instaliranim V18 **i** V21 (otvoren V21 projekt Kosovo_zahvat_V21).

**Promjene:**
- `CwcConfigurator.TiaHelper/Program.cs` — `GetTiaApiDirectory()` sad bira putanju preko compile-time `#if TIA_V21` (svaki build resolva svoje Openness assemblyje).
- `CwcConfigurator.TiaHelper.csproj` — dodan define `TIA_V18` (V18 build).
- **Novo:** `CwcConfigurator.TiaHelper.V21/CwcConfigurator.TiaHelper.V21.csproj` — linka isti `Program.cs`, referencira `Siemens.Engineering.Base` + `Step7` (V21 net48), define `TIA_V21`, isti `AssemblyName=CwcConfigurator.TiaHelper`.
- `TiaApiLocator` — `GetInstalledVersions()` (sve instalirane verzije).
- `TiaSyncContracts` — `TiaPortalProcessInfo.Version` (+ `[V18]/[V21]` u ToString); `ITiaSessionService.AttachToRunningPortalAsync(int, string portalVersion)`.
- `TiaSessionService` — per-version helper procesi (`Dictionary<version,Process>`); `GetRunningPortalProcessesAsync` pita SVE instalirane verzije i tagira procese; attach pinna `_activeVersion`; sve session-komande rutiraju na taj helper; per-version `ResolveHelperPath` (`TiaHelper/<version>/`), dev-fallback na oba projekta.
- `TiaSyncDialog` — attach prosljeđuje `process.Version`.
- `CwcConfigurator.csproj` — `CopyTiaHelper` target gradi OBA helpera u `TiaHelper/V18` i `TiaHelper/V21`.

**Verifikacija:**
- V21 helper kompajlira **bez ijedne #if divergencije** (API površina helpera identična V18/V21).
- Staged V18 helper → `Processes:[]` (nema V18 TIA); staged V21 helper → živi proces 4104. Merge bi dao `PID 4104 [V21] | Kosovo_zahvat_V21`.

**Napomena:** `GetPreferredVersionLabel()` ostao (više se ne koristi za rutiranje; bezopasan).

---

## ISPRAVAK #9 (2026-06-23, tijekom Faze 2)

Provjerom doca `Siemens.Simatic.Hmi.Openness.RestService` potvrđeno: servis **NEMA** text-list/translation/language endpoint (samo: tiaProcesses, unifiedDevices=script moduli, libraryTypes, scheduler/screens feature-flagovi). Dakle izvorni #9 plan (text liste preko REST-a) **ne vrijedi**.

**Korigirani put #9:** text liste idu **Engineering Openness putem** — isto kao #11 (screenovi). Tj. nova helper komanda (HMI Unified: `HmiSoftware` → text list composition export/import), na **postojećoj TIA SYNC (Engineering) sesiji** — NE preko JS-Connector REST-a.

**Posljedica na arhitekturu Faze 2:**
- **#10 (set internal tag skripta)** → JS-Connector REST (`/export|/import/unifiedDevices`) — VERIFICIRANO, ostaje.
- **#9 (text liste) + #11 (screenovi)** → Engineering helper (HMI Unified), piggyback na postojeći dual-version TiaSync attach.
- "Dijeljeni Unified attach u TIA SYNC prozoru" iz starog #9 plana → **otpada za #9/#11**; treba samo za #10 (REST). #9/#11 koriste već postojeći Engineering attach.

Redoslijed Faze 2: **#10 prvo** (verificirano), pa **#9+#11** kroz helper HMI Unified.

---

## FAZA 2 — status (2026-06-23)

### #10 — IMPLEMENTIRANO ✅ (commit e0f65b3)
Round-trip `AUX_FUNCTIONS._SET_INTERNAL_TAG` preko Unified REST. Vidi commit; verificirano na živom V21 (body se parsira, splice lossless, 39 testova prolaze). Pull/Push gumbi u SCADA tabu.

### #11 — istraga gotova, spremno za implementaciju
**Potvrđeni V21 screen API** (reflection nad `Siemens.Engineering.WinCCUnified.dll`):
- `Siemens.Engineering.HmiUnified.HmiSoftware` ima `Screens` (top-level) i `ScreenGroups` (`HmiScreenGroupComposition`, rekurzivno: svaka grupa ima Screens + ScreenGroups).
- Svaki screen ima `.Name`; namespace tipova `Siemens.Engineering.HmiUnified.*`.
- Assembly = **`Siemens.Engineering.WinCCUnified.dll`** (V21). V18 nema `HmiUnified.dll` na PublicAPI putanji → **#11 je V21-only**, što je OK jer je SCADA V21 (RestService 21.x, projekt .ap21).

**Plan implementacije #11:**
- V21 helper (`CwcConfigurator.TiaHelper.V21`): dodati referencu `Siemens.Engineering.WinCCUnified`; nova komanda `GetUnifiedScreens` (pod `#if TIA_V21`) — naći `HmiSoftware` preko `SoftwareContainer` (kao PlcSoftware), rekurzivno pobrati Screens + ScreenGroups → lista fully-qualified imena.
- App: nova session komanda + DTO (lista screenova); katalog screenova (refreshable).
- UI: bind katalog kao ItemsSource na screen-kolone u nav gridovima (`ScadaNavigationGrid` / mainnav matrica).

### #9 — text liste (Engineering put, odgođeno/zasebno)
REST nema text liste → ide preko HMI text list composition (Engineering), analogno #11. Nije kritično za navigaciju; raditi nakon #11.

---

## STATUS IMPLEMENTACIJE (2026-06-23) — branch feature/tia-v21-dual-helper

| Cjelina | Commit | Stanje |
|---|---|---|
| Faza 1 — migracija V18→V21 (dual-helper) | c8023d0 | ✅ implementirano + verificirano |
| #10 — SCADA round-trip `_SET_INTERNAL_TAG` | e0f65b3 | ✅ implementirano + verificirano (živi V21) |
| #11 — katalog screenova za nav dropdownove | 62e9d2e | ✅ implementirano; helper verificiran (29 screenova) |
| #9 — text liste (descriptioni) auto-export | — | ⏸️ ostaje: Engineering text-list export daje XML, ne app-ov `Unified_text_lists.xlsx` → treba most/konverzija; zasebna istraga |

**Verificirano automatski:** clean build (app + oba helpera), 39 unit testova, V21 helper runtime (GetProcesses, Attach, GetUnifiedScreens) i Unified REST (GetUnifiedDevices, export AUX_FUNCTIONS) protiv živog projekta Kosovo_zahvat_V21.

**NIJE moguće automatski (treba korisnikov klik-test u WPF UI):** vidi TEST listu — interakcije u prozorima (TIA Sync attach lista, SCADA Pull/Push gumbi, screen dropdown) i destruktivni Push u živu SCADU.

---

## SCADA REDIZAJN (nakon korisnikova testa #10/#11)

Cilj: app treba *graditi* `_SET_INTERNAL_TAG` intuitivno, ne prikazivati sirove retke. Faze:
- **R1** — odabir Unified uređaja (više SCADA uređaja u projektu) + reorg defaultnog taba. ✅ implementirano.
- **R2** — Navigation Builder: master-detail (main-nav tipke ↔ screenovi po tipki + top-nav kartice), dropdown screenova (#11), auto-brojači (`BUTTON_X_SCREEN_NUM`), live validacija. ← sljedeće, srce redizajna.
- **R3** — PLC rack/slot vizualni editor (grey/white klik-mreža) + validacije.

### R1 — implementirano (2026-06-23)
- `ProjectPaths.UnifiedScadaDeviceName` (persistira odabrani SCADA uređaj).
- `ScadaUnifiedSyncService`: `ListScadaDeviceNamesAsync` + Pull/Push primaju `preferredDeviceDisplayName`; ako više uređaja ima AUX_FUNCTIONS a nije odabran → jasna greška (ne pogađa); ako jedan → auto.
- SCADA top bar: "Device" combo + "Refresh devices" (lista uređaja iz svih TIA procesa koji imaju AUX_FUNCTIONS); odabir se pamti; Pull/Push ciljaju odabrani.
- Reorg: default tab = **GENERAL** (umjesto sirovog); "Tags" preimenovan u **"Advanced (raw)"**.

### Ideja za KASNIJE (korisnik): grey/white moduli iz PLC pulla
Iz PLC konfiguracije (TIA Openness, kad se povuče PLC) može se izvući jesu li moduli **grey ili white**. To se može **direktno applyati** na SCADA interne tagove (`LOCATION_x_..._RACK_..._SLOT_y` = 0 grey / 1 white) umjesto ručnog unosa. Veže se na R3 (PLC rack/slot). Nije za sada — zabilježeno.

### R2 — Navigation Builder: implementirano (2026-06-23, commit a8abb77)
- `ScadaNavigationModel` (FromDatabase/ApplyTo) — verificiran test-first + na živom AUX_FUNCTIONS (Intake→[Control,HPU,PLC], Trends, Alarms, Configuration).
- NAVIGATION tab: master-detail (tipke ↔ screenovi), editable dropdown screenova (#11 katalog), Add/Remove/Up/Down, labela tipke. Izmjene → ApplyTo → Push/Export reflektiraju; brojači auto.
- Builder vidljivo pokazuje i zaostale matrix unose (screenovi pod tipkama bez labele) — korisnik ih sad može počistiti.

Ostaje: **R3** (PLC rack/slot vizual + grey/white iz PLC pulla) — korisnik rekao za kasnije.
