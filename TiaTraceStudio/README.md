# TIA Trace Studio 1.31.0

Windows aplikacija za TIA Portal CSV traceove i izvoz JPG/PNG grafova. .NET 10 / WPF / ScottPlot 5. Podaci se obrađuju lokalno.

## Pokretanje

Pokreni `artifacts/release-v1.31.0/TiaTraceStudio.exe`. Za drugi Windows x64 PC raspakiraj cijeli `artifacts/TiaTraceStudio-win-x64.zip`; .NET runtime je uključen.

## Projekt puštanja u pogon (stablo testova)

1. **Novi projekt** otvara definiciju postrojenja: naziv, lokacija i broj agregata. Za svaki agregat odabire se tip turbine (Pelton, Francis, Kaplan, Cross Flow), broj mlaznica i deflektora ili sekcija privodnog aparata, MIV, bypass, uzbuda, regulacije (brzina, napon, snaga, razina, otvor, jalova snaga) i razine odbacivanja tereta. **Kopiraj postavke ovog agregata na sve ostale** ubrzava unos istih agregata.
2. Iz definicije nastaje stablo testova po agregatu:
   - **Dry test** – za svaki turbinski ventil brzina otvaranja/zatvaranja, odziv na step i na rampu reference; HPU pad tlaka pri ESD tlaku; MIV i bypass s ručnim unosom vremena.
   - **Wet test** – zalet, bearing run test, prva uzbuda, regulacije brzine i napona, sinkronizacija, zaustavljanja NSD/QSD/ESD.
   - **Testovi na mreži** – regulacije snage, razine, otvora i jalove snage, odbacivanje tereta po razinama, heat run test.
3. Uz svaki test su **upute kako ga izvesti** i **popis tagova za snimanje**. Upute i tagove uređuješ u **Katalog testova (upute i tagovi)**; u tagovima `{n}` zamjenjuje broj ventila ili razinu snage, a `{u}` broj agregata. Katalog se sprema u projekt i, po želji, kao zadani katalog za nove projekte (`%LOCALAPPDATA%/TiaTraceStudio/test-catalog.json`). Može se izvesti i uvesti kao JSON.
4. Kad se CSV učita (Otvori CSV, povlačenje ili više datoteka odjednom), za svaku datoteku otvara se prozor **Pridruži snimku testu**. Prozor predlaže test prema tagovima u datoteci i pokazuje koji očekivani tagovi postoje (✓) ili nedostaju (✗). **Učitaj CSV za ovaj test…** u panelu testa preskače pitanje.
5. Svaka snimka zadržava sve mogućnosti grafa (nazivi, jedinice, scaling, min/max, kursori, bilješke, izvoz). Test može imati više snimki. Status testa: čeka, učitano, pregledano ili nije primjenjivo; uz test se upisuje napomena.
6. **Definicija postrojenja…** mijenja definiciju i kasnije; postojeće snimke, napomene i ručni unosi ostaju, a testovi koji više nisu u definiciji zadržavaju se dok sadrže podatke.

Aplikacija ne konfigurira niti pokreće trace u TIA Portalu (TIA V21 Openness nema API za trace); trace se snima u TIA Portalu i izvozi u CSV, ili aplikacija snima sama (dolje).

## Plant Configurator, PLC-ovi i snimanje s PLC-a

- **Pokretanje iz Plant Configuratora:** `TiaTraceStudio.exe --plantcfg "<projekt.plantcfg>"`. Aplikacija čita (samo čita) sve PLC baze projekta: mjerenja (ID, sustav, PLC naziv, naziv, jedinica) i broj bloka MEAS. Bez otvorenog projekta nudi definiciju postrojenja s brojem agregata prema bazama „Unit n“. Isto se radi ručno: **PLC-ovi i Plant Configurator… → Poveži projekt Plant Configuratora**.
- **Novi projekt pita za .plantcfg:** čim se otvori definicija postrojenja, otvara se odabir Plant Configurator projekta; naziv postrojenja i broj agregata preuzimaju se iz njegovih baza „Unit n“. Tablica **Nazivne vrijednosti** odmah se popunjava mjerenjima iz testova agregata s rasponima iz parametara njegove baze (npr. snaga 0–2070 kW); ručno upisane vrijednosti se ne mijenjaju, a novi agregati dobivaju vrijednosti iz svoje baze „Unit n“. Na vrhu čarobnjaka piše odabrana datoteka, uz tipke **Odaberi .plantcfg…** i **↻ Osvježi**. Odustajanje od čarobnjaka zadržava prethodnu vezu.
- **Osvježavanje:** kad je povezana .plantcfg spremljena u Plant Configuratoru nakon učitavanja, ispod oznake u stablu pojavi se **↻ Osvježi iz Plant Configuratora** (provjera pri svakom povratku u prozor); ponovno se učitavaju nazivi, jedinice, adrese i rasponi.
- **Što je povezano:** ispod naziva postrojenja piše „⛓ Plant Configurator: <datoteka>.plantcfg · učitano <datum>“ (puna putanja, projekt i baze u opisu; klik otvara PLC-ove i Plant Configurator; narančasto ako projekt nije povezan). Uz svaki agregat i grupu u stablu piše PLC baza i IP PLC-a (npr. „Unit 1 · 10.25.4.11“), a opis pokazuje PLC, rack/slot, bazu s brojem mjerenja i datoteku.
- **Pretraživanje mjerenja:** u snimaču, uređivanju mjerenja i nazivnim vrijednostima upiši bilo koje riječi naziva, taga ili ID-a, u bilo kojem redoslijedu (npr. „position nozzle 2“, „bearing temp“, „168“); popis se filtrira dok tipkaš, Enter dodaje prvo pogođeno mjerenje.
- **PLC-ovi:** svaki PLC ima naziv, IP, rack/slot (S7-1500: 0/1), PLC bazu iz Plant Configuratora i agregate koje poslužuje. Baze „Unit n“ povezuju se s agregatom n automatski; ostale baze (Common, Intake …) dobivaju PLC bez agregata.
- **Nazivi i jedinice** MEAS tagova preuzimaju se iz baze PLC-a kojem snimka pripada, točno kako su konfigurirani (osim kad je uključeno „Samo ovaj CSV“).
- **Adrese:** mjerenja se čitaju iz indirektnog bloka **DB1003** (u svim PLC-ovima; iznimno se mijenja u PLC-ovi → DB za snimanje mjerenja). Blok je polje struktura `DT_MEASUREMENT_SOLO` (56 B) po ID-u 0–287, pa je `VAL = DBD(ID × 56 + 44)` (npr. NOZZLE_1, ID 4 → `%DB1003.DBD268`). Provjereno na PLCSIM Advanced: 288 mjerenja, 17 zahtjeva po taktu, čitanje ≈ 2 ms. Ostali tagovi (npr. `REG_INTF.*`) dobivaju adresu iz zaglavlja učitanog TIA trace CSV-a, iz kataloga testova (`TAG[%DB31.DBD430]`) ili ručnim upisom; adrese se pamte u projektu.
- **●  Snimaj s PLC-a…** uz svaki test: S7 (PUT/GET) čitanje, takt 100 ms – 10 s, graf uživo, **Provjeri vezu i adrese** čita svaki tag jednom. **Zaustavi i spremi** dodaje snimku pod test sa svim mogućnostima grafa. Tijekom snimanja podaci se stalno zapisuju u CSV (`<projekt> - snimke/` ili `Dokumenti/TIA Trace Studio/Snimke/`), pa se ne gube ni pri prekidu. Vrijeme uzorka je s PC sata; brze pojave (< 100 ms) snimaj TIA traceom.
- **Fiksni raspored blokova (svi PLC-ovi):** MEAS `DB1003` (56 B po ID-u, VAL REAL na +44), DI `DB1001` i DO `DB1002` (22 B po ID-u, VAL = prvi BOOL, bit 0), REG_INTF `DB31` (26 B po regulatoru, VALUES na +14: SCALED_REFERENCE, OUT, SCALED_MEAS). Signal se može navesti i ID-om: `MEAS#0`, `DI#23`, `DO#5`.
- **Izvještaj u Word (.docx), na engleskom, za klijenta:** Projekt ▾ → **Izvještaj u Word (.docx)…** pita za **predložak tvrtke: Geppert Electric, Prime Volt** ili osnovni izvještaj bez predloška (izbor se pamti u projektu; putanja predloška po korisniku, tipka „Predložak…“). Iz predloška ostaje sve osim oglednog sadržaja: zaglavlje s logom, podnožje, stilovi, numerirani naslovi i naslovnica do sadržaja. Kartica **Podaci dokumenta** nudi sve tekstove zaglavlja, naslovnice i potpisa (Checked / Approved) za promjenu po projektu (broj dokumenta, naslov, projekt, kupac, datum, autor, imena) – mijenja se samo promijenjeni dio, oznake zadržavaju format. Kartica **Poglavlja**: engleski nazivi (spremaju se u stablo). Poglavlja prate stablo; test sa snimkom je **vodoravna sekcija** sa zaglavljem i podnožjem raširenim na širinu stranice: naslov i graf (legenda dio slike, bez kursora i min/max oznaka, opis „Figure n: naziv“), na sljedećoj stranici statistika (min, max, mean, start, end) i **Remark** u obliku tablice „Conclusion“ predloška s napomenom iz aplikacije ili praznim mjestom. Numeracija stranica teče kroz sve sekcije.
  - **Parametri regulatora:** na početku potpoglavlja svakog turbinskog ventila (npr. Mlaznica 1) i poglavlja Regulacije (po jedna tablica za svaki regulator) tablica iz Plant Configuratora (struktura PID_*): P-action, Gain, I-action, Ti, D-action, Td, Dead band, Low limit, High limit; prazna za ručni upis ako Plant Configurator nema vrijednosti.
  - **Numerirane slike i tablice:** „Figure n: …“ centrirano ispod grafa, „Table n: …“ iznad svake tablice (Remark nije numeriran); na početku izvještaja nakon sadržaja **List of figures** i **List of tables** s poveznicama i brojevima stranica.
  - **Vremena otvaranja i zatvaranja:** upisuju se u panelu testa „Brzina otvaranja / zatvaranja“ svakog turbinskog ventila (i za MIV / bypass) i ispisuju u izvještaju kao tablica (prazno polje za ručni upis).
  - **Brojevi stranica u sadržaju:** ako je Word instaliran, nakon spremanja izvještaj se nevidljivo otvori u Wordu, sadržaj i brojevi stranica se osvježe i spreme.
  - **Obavezni podaci dokumenta:** nakon izbora forme na vrhu kartice Podaci dokumenta upisuju se oznaka dokumenta, naziv projekta, naslov (zadano „SAT report“), autor, kupac, područje (Section) i datum – samo ona polja koja forma ima (prepoznaju se po oznakama: „Document No.:“, „Document NUMBER“, „FB Nr.:“, „Project NAME“, „Document title“, „author:“, „customer:“, „Section :“, „DATE“ …). Bez njih izvoz ne kreće. Vrijednosti se upisuju na sva mjesta u zaglavlju i naslovnici (oznake ostaju, naslov velikim slovima gdje ga forma tako piše) i u svojstva datoteke (File → Info); pamte se u projektu.
  - **Zajednički testovi** (npr. odbacivanje tereta svih agregata): jedna snimka daje graf, skaliranje i statistiku za svaki agregat posebno („… · Unit 1“, „… · Unit 2“), prema PLC-u iz kojeg signal dolazi.
  - **SAT report:** „FAT“ iz obrasca predlaže se kao „SAT“ (Podaci dokumenta). Polja zaglavlja i naslovnice vezana na svojstva dokumenta (naslov, predmet, autor …) prikazuju se onako kako ih Word prikazuje i mogu se zamijeniti.
  - **Sadržaj je klikabilan:** svaki unos vodi na svoje poglavlje (brojevi stranica: desni klik → Ažuriraj polje; pri ispisu/PDF-u Word ih upisuje sam).
  - **Graf u postocima:** tablica „Scaling of the graph“ navodi za svaki signal što je 0 % i 100 % (položaji, izlazi regulatora i temperature: „not scaled“).
  - **Statistika** samo za analogne signale (digitalni 0/1, DI/DO bez statistike). **Remark** bez redaka Checked / Approved.
  - **Zadnje poglavlje „Commissioning engineer sign-off“:** tablica Role / Name / Date / Signature za potpis.
  - Numeracija naslova preuzima se iz predloška i kad je zadana na odlomku (Prime Volt).
- **Stablo je predložak po projektu:** desni klik na bilo koji dio stabla → **Preimenuj…** (agregat, faza, grupa, podgrupa, vlastita grupa, test); upisom izvornog naziva vraća se izvorni. Vlastite podgrupe preimenuju se stvarno i snimanja idu s njima. **Obriši snimanje…** (desni klik na test ili tipka u panelu) uklanja snimanje iz stabla, a njegove snimke iz projekta nakon potvrde (CSV datoteke na disku ostaju). Obrisani zadani testovi ostaju obrisani i pri promjeni definicije; vraćaju se desnim klikom na agregat → **Vrati obrisana snimanja**.
- **Vlastita snimanja u agregatu:** desni klik na agregat → Novo snimanje agregata… / Nova podgrupa… (grana „Korisnička snimanja” agregata); desni klik na fazu ili grupu agregata (npr. Dry test → Ventili) → Novo snimanje ovdje…, Nova podgrupa…, Obriši podgrupu (prazne). Snimanja koriste PLC agregata i ostaju pri promjeni definicije postrojenja.
- **Desni klik na test u stablu:** Preimenuj test…, Uredi mjerenja… (dodavanje i uklanjanje mjerenja s pretraživanjem), Upute i mjerenja…, Napomena…. Isto je i u panelu testa ispod stabla. Novi naziv i odabrana mjerenja ostaju i kad se kasnije promijeni definicija postrojenja; naslov grafova tog testa prati novi naziv.
- **Mjerenja po testu:** početni popis dolazi iz kataloga testova; **Uredi mjerenja…** dodaje ili uklanja mjerenja pretraživanjem baze Plant Configuratora (MEAS, DI, DO, REG_INTF) po PLC-u, ili bilo koju adresu (DB, offset, bit, tip). Izmjena se može primijeniti na isti test u svim agregatima; **Vrati zadano iz kataloga** vraća početni popis. Čitaju se samo odabrana mjerenja.
- **Kursori** (kvačice iznad grafa, svaki zasebno): **Kursor** – jedan vertikalni za vrijednosti u trenutku i postavljanje nule; **Vertikalni A/B** – statistika (Δt, 1/Δt, A, B, Δy, Δy/Δt, min, max, srednja vrijednost); **Horizontalni A/B** – dvije vodoravne linije na lijevoj osi prvog panela; okvir kao kod vertikalnog kursora za svaki signal pokazuje A, B i ΔY u njegovoj jedinici (svaka Y-os, i u normaliziranom prikazu preračunato u fizikalne jedinice).
- **Raspon za normalizaciju iz Plant Configuratora:** parametri mjerenja ParVal1Id (0 %) i ParVal2Id (100 %) iz PLC baze agregata (npr. nazivna snaga, napon, brzina, mjerni raspon tlaka). Nazivna vrijednost upisana u Definiciji postrojenja ima prednost. Regulator interface (REG_INTF) uvijek je u postocima (reference 0–100 %, izlaz regulatora položaja −100…100 %) i crta se izravno.
- **Vertikalni kursor** (kvačica iznad grafa): okomita linija koja se povlači mišem, s vremenom i vrijednostima svih signala na tom mjestu; **Primijeni nulu grafa** postavlja nulu vremena na kursor (vrijedi i za izvoz). Kursor je pomagalo na ekranu i ne izvozi se.
- **Kursori A/B** (analiza): Δt i 1/Δt, za svaki signal vrijednost na A i B, Δy = B − A, Δy/Δt te minimum i maksimum između kursora, u fizikalnim jedinicama (i u normaliziranom prikazu).
- **Legenda** je ispod grafa (ispod vremenske osi), prelama se u više redova i ne prekriva krivulje – u aplikaciji, kopiranoj slici i JPG/PNG izvozu.
- **Normalizirani graf:** položaji (%) uvijek su 0–100 %, a izlazi regulatora (REG_INTF OUT) −100…100 % — prikazuju se izravno, bez parametara. Temperature (°C) ostaju u stupnjevima na istoj osi (do ~150 °C), os glasi „Normalizirano [%] · temperature [°C]“. Nazivne vrijednosti trebaju samo ostala mjerenja (snaga, brzina, napon, tlak …).
- **Zadana mjerenja** (Plant Configurator ID; turbinski ventili prema tipu turbine: Pelton mlaznice 4…9 i deflektor 3, Francis 0, Kaplan 1–2, Cross Flow 10–11):
  - HPU pad tlaka: tlak akumulatora HPU (13), turbinski ventili.
  - Zalet: ventili, brzina generatora (178), napon generatora (171).
  - Bearing run: ventili, brzina generatora, temperature ležajeva (12, 23, 30–35, 45).
  - Zaustavljanja NSD/QSD/ESD: ventili, snaga (168), brzina, napon, tlak cjevovoda (17), tlak turbine (18).
  - Odbacivanje tereta: kao zaustavljanja + vibracije (40–43, ako agregat ima mjerenje vibracija – kvačica u Definiciji postrojenja) + GCB DI 23/24.
  Snaga, napon i brzina su kontrolna mjerenja (ID 168–191).
- **Odbacivanje tereta** uvijek snima stanje generatorskog prekidača: DI 23 (GCB uklopljen) i DI 24 (GCB isklopljen).
- **Nazivne vrijednosti (postotni graf):** u Definiciji postrojenja svaki agregat ima tablicu mjerenja s 0 % i 100 % (dodavanje iz baze Plant Configuratora / REG_INTF ili „Dodaj sva mjerenja iz testova“; „Kopiraj postavke ovog agregata na sve ostale“ kopira i njih). Svaki signal ima prekidač **Raspon iz projekta** (zadano uključen): tada 0 %/100 % dolaze iz nazivnih vrijednosti agregata snimke; isključen = vlastiti raspon samo tog grafa.
- **Zajednički testovi** (postrojenja s više agregata): odbacivanje tereta svih agregata (zadano 25/50/75/100 %) snima se istodobno sa svih PLC-ova, s jednom vremenskom osi; stupci nose naziv PLC-a.
- **NaN = neispravno mjerenje** (greška senzora, mjerenja ili komunikacije): prikazuje se kao 0 u rezerviranoj boji `#FF00FF`, koja se ne može dodijeliti signalu; legenda svakog grafa ima stavku „Greška snimanja (NaN, prikaz 0)“, a uz snimku se navodi broj neispravnih uzoraka po mjerenju.
- **Vlastite grupe** (npr. Intake, Zajednički pogon): Projekt ▾ → Nova grupa ili desni klik u stablu. Grupa ima naziv, bazu Plant Configuratora i IP PLC-a; desni klik na grupu: Novo snimanje u grupi, Nova podgrupa, Uredi grupu, Obriši grupu. Snimanja u grupi koriste PLC i bazu grupe.
- **PLC i baza agregata:** desni klik na agregat u stablu ili tipka „PLC i baza agregata…“ u panelu testa (IP, rack, slot, PLC baza u Plant Configuratoru).
- **Korisnička snimanja:** slobodan naziv, odabir PLC-a (npr. Common, Intake) i mjerenja s popisa njegove baze; popis se pamti za sljedeće snimanje.
- Uvjet u PLC-u: *Protection & Security → Permit access with PUT/GET communication*.


## Raspored ekrana

Glavni ekran ima samo stablo testova (lijevo) i graf. Iznad grafa: **Signali…**, **Graf…**, **Izvoz…** (prozor s postavkama grafa) i **Alati…** (statistika, kursori, bilješke, izvedeni signali). Projektne radnje (definicija postrojenja, PLC-ovi, katalog testova, korisnička snimanja, baza naziva, izvoz svih grafova) su u izborniku **Projekt ▾** gore. Panel testa ispod stabla je kratak; **Upute i mjerenja…** i **Napomena…** otvaraju se u prozoru.

## Osnovni rad

1. **Otvori CSV** ili povuci datoteku u prozor.
2. Odaberi svaki signal i **sam unesi njegov naziv**. Poznati tagovi mogu preuzeti naziv koji si ranije unio u bazu; aplikacija ne zaključuje značenje ni jedinicu iz taga. Naziv je obavezan prije izvoza za sve uključene signale. Projekti mogu čuvati i nedovršena imena.
3. Unesi jedinicu, primjerice `bar`, `m`, `rpm` ili `%`, te odaberi boju.
4. Odaberi način prikaza, po želji uključi **Označi min / max**, pa klikni **Primijeni**.
5. **Izvezi sliku** sprema JPG ili PNG u zadanim dimenzijama. **Kopiraj sliku**, pokraj izvoza, kopira isti render u punoj rezoluciji u međuspremnik bez stvaranja datoteke. U kartici Izvoz dostupan je pregled konačne slike.

Graf je u središtu aplikacije. Rjeđe postavke su sklopljene pod **Dodatne postavke signala**, **Dodatni izgled** i **Dodatni alati**. Nema automatskih bilješki ni pretpostavki o događajima u traceu.

## Projekt sa svim snimkama

- **Jedan CSV = jedan graf / jedna slika.** **Otvori CSV** dodaje snimke u trenutačni projekt. Možeš odabrati ili povući više datoteka odjednom; svaki CSV dobiva zaseban graf, a prethodni ostaju dostupni.
- Kad su učitana barem dva CSV-a, iznad grafa pojavljuje se birač datoteke. Svaki CSV čuva vlastiti izgled, kursore, oblačiće, vremenski pogled i undo/redo povijest.
- Unesi naziv signala pa klikni **Primijeni promjene**. Naziv se prenosi u bazu trenutačnog projekta i na podudarne signale njegovih CSV-ova, osim lokalnih iznimki. Novi CSV automatski preuzima poznate nazive tog projekta. Podudaranje koristi točan izvorni CSV/PLC tag, uključujući velika i mala slova; nepoznati signali ostaju neimenovani.
- Za drugačiji naziv u jednom CSV-u uključi **Samo ovaj CSV (naziv, jedinica i scaling)** prije primjene. Te postavke ne mijenjaju bazu niti druge grafove i ostaju zaštićene od kasnijih zajedničkih promjena.
- Pod **Dijeljenje postavki signala → Primijeni postavke na sve CSV-ove** možeš izričito zamijeniti naziv, jedinicu, faktor i offset istog taga svugdje, uključujući lokalne iznimke. **Preuzmi postavke iz baze** uklanja lokalnu iznimku odabranog signala i vraća spremljeni naziv, jedinicu, faktor i offset.
- **Spremi projekt / Ctrl+S** sprema cijelu zbirku u jednu **`.tracestudio`** datoteku: sve uzorke iz CSV-ova, postavke svakog grafa, nazive i lokalne iznimke, jedinice, oblačiće, kursore, bilješke, vremenske poglede i odabranu snimku. To je čitljiva JSON datoteka s ugrađenim podacima; izvorni CSV-ovi nisu potrebni za ponovno otvaranje ili prijenos na drugi PC.
- **Otvori projekt** otvara cijelu spremljenu zbirku. Prije zamjene aplikacija nudi spremanje nespremljenih promjena. **Novi projekt** počinje s praznom zbirkom i vlastitom bazom naziva.
- Sklopivi dio **Projekt · snimke i nazivi** sadrži **Novi projekt**, **Spremi projekt kao…**, uklanjanje snimke, bazu naziva projekta i izvoz svih grafova. Brisanje zapisa baze ne briše postojeće nazive na grafovima. Baza dijeli naziv, jedinicu, faktor i offset za isti izvorni tag, uključujući naknadno dodane CSV-ove. Jedinica i scaling dijele se i prije unosa naziva; promjenu potvrdi s **Primijeni**.
- Baza naziva čuva se unutar `.tracestudio` datoteke. Nazivi se ne dijele između odvojenih projekata. Stara globalna datoteka `signal-names.json` ne učitava se automatski.
- Starije `.tiaproject` i `.tiatrace` projekte i `.tiasession` zbirke možeš otvoriti preko **Otvori projekt** i spremiti u novi format. Njihovi postojeći nazivi ostaju sačuvani, a zajednički nazivi popunjavaju projektnu bazu. Ako u bazi nedostaju jedinica, faktor i offset, preuzimaju se iz prve snimke s postavljenim vrijednostima za isti tag; početne vrijednosti u ostalim snimkama dopunjuju se. Lokalne iznimke i postojeći zapisi baze ostaju sačuvani.
- Undo/redo povijest tijekom rada vrijedi za odabrani CSV; njezini koraci ne spremaju se u projektnu datoteku. Baza i nazivi u drugim CSV-ovima uređuju se zasebno.
- **Izvezi sve učitane grafove** koristi vlastite postavke svakog CSV-a. Postojeće slike ne prepisuje, nego dodaje broj u ime datoteke. Neispravni ili neimenovani grafovi prijavljuju se, a ostali se izvoze.

## Vremenska os

Pod **Graf → Jedinica vremenske osi** odaberi **s** (sekunde), **min** (minute), **h** (sati) ili **d** (dani), pa klikni **Primijeni**. Izbor vrijedi za trenutačni CSV i sprema se u projekt. Brojevi i naziv osi, vremena min/max oznaka, oznake kursora i Δt koriste odabranu jedinicu, uključujući JPG/PNG i kopiranu sliku. Zadano je `Time [s]`; naziv `Time` možeš promijeniti.

Izvorna vremena i izračuni ostaju u sekundama. Numerička polja označena `[s]` (raspon, nula vremena, kursori i bilješke) i dalje se unose u sekundama. Promjena prikaza ne mijenja uzorke, stvarne položaje kursora ili njihove izmjerene vrijednosti. Jedan dan znači 86 400 sekundi, bez kalendarskih/vremenskih zona.

## Različite jedinice

**Izvorne jedinice · više Y-osi**

- Svaka različita unesena jedinica dobiva vlastitu Y-os. Signali s istom jedinicom dijele os; jedinice su osjetljive na velika/mala slova.
- Signali bez unesene jedinice dobivaju zasebne osi. Unos jedinica zato pomaže smanjiti broj osi.
- Granice svake osi automatski se računaju iz podataka u odabranom vremenskom rasponu. Jedinice se ne pretvaraju automatski (npr. bar i Pa ostaju odvojeni).
- Kod puno jedinica koristi više panela ili postotni prikaz radi čitljivosti.

**Normalizirano · %**

- Za svaki uključeni signal korisnik zadaje vrijednosti koje odgovaraju **0 %** i **100 %**, u jedinici signala nakon faktora i offseta.
- Formula: `100 × (vrijednost − donja_granica) / (gornja_granica − donja_granica)`.
- Primjer: za 0–10 bar, vrijednost 5 bar prikazuje se kao 50 %.
- Raspon se ne pretpostavlja niti automatski mijenja pri zoomu. Gumb **Preuzmi min/max cijele snimke** izričito preuzima granice podataka za odabrani signal.
- Vrijednosti izvan raspona nisu odrezane: moguće je −10 % ili 120 %. Konstantan signal traži ručni unos različitih granica.
- Statistika u Dodatnim alatima zadržava fizičke, skalirane vrijednosti. Tablica kursora na grafu prati prikaz grafa, pa u postotnom načinu prikazuje postotke. Oznake ekstrema u postotnom prikazu prikazuju i fizičku vrijednost ako je jedinica unesena.

## Mjerenje s dva kursora

- Uključi **Kursori A/B** iznad grafa. Dva kursora pojavljuju se unutar trenutačnog pogleda.
- Povuci liniju ili obojenu oznaku **A** / **B**. Položaji se istodobno ažuriraju u svim panelima, bez pomicanja osi.
- Između kursora prikazuje se proteklo vrijeme **Δt**. Tablica u svakom panelu prikazuje sve njegove vidljive signale: vrijednost u A, vrijednost u B i razliku **B − A**, uz unesenu jedinicu. Δt ostaje pozitivan i kada zamijeniš redoslijed kursora.
- Vrijednosti između uzoraka linearno se interpoliraju. Nedostajući podaci i vremena izvan snimke prikazuju se crticom; nema ekstrapolacije.
- Za precizan unos koristi polja kursora u **Dodatnim alatima**. Dostupni su i **Shift+klik** za A te **Ctrl+klik** za B.
- Kursori, Δt i tablica uključeni su u kopiranu sliku i JPG/PNG izvoz. Položaji se spremaju u projekt. Isključi **Kursori A/B** za sliku bez mjerenja.
- Automatski min/max oblačići izbjegavaju tablicu i oznaku Δt. Ako za sve retke nema dovoljno mjesta, povećaj visinu slike ili rasporedi signale u više panela; izvoz traži da svi retci budu vidljivi.

## Min / max oznake

Opcionalno se označavaju minimum i maksimum svakog vidljivog signala unutar trenutačnog vremenskog pogleda. Kod jednakih ekstrema označava se prvo pojavljivanje; konstantan signal dobiva jednu oznaku **min = max**.

Oznake sadrže korisnički naziv, vrijednost i vrijeme te spojnicu do stvarne točke. Početni automatski raspored traži slobodan prostor bez krivulja signala, drugih oblačića i tablice kursora. Algoritam uzima u obzir sve vidljive segmente, njihove Y-osi i debljinu te prednost daje kratkim spojnicama. Raspored se prilagođava zoomu, veličini prozora i izvozu; ručno zadane pozicije imaju prednost.

Ako krivulje ispune raspoloživ prostor, algoritam odabire položaj s najmanjim prekrivanjem krivulja i prikazuje obavijest. Automatski oblačići i dalje se ne preklapaju međusobno. Ako nema mjesta ni za sve oblačiće, aplikacija navodi koliko ih ne stane; povećaj graf, smanji broj signala ili ih podijeli u panele. Spojnice do ekstrema i korisničke bilješke imaju neovisan položaj. Dok su min/max oznake uključene, zamjenjuju standardnu legendu.


### Ručno uređivanje oblačića

- **Povuci lijevom tipkom** za promjenu položaja oblačića. Točka ekstrema ostaje na pravom mjestu; spojnica prati oblačić. Povlačenje ne pomiče osi grafa.
- **Desni klik → Sakrij ovaj oblačić** uklanja odabranu oznaku iz prikaza, kopiranja i JPG/PNG izvoza.
- **Desni klik → Vrati automatski položaj** uklanja ručni položaj te oznake.
- Pojedinačno skrivanje/prikazivanje dostupno je i pod **Signali → Dodatne postavke signala** (minimum, maksimum i kombinirani min=max).
- Pod **Graf → Dodatni izgled** možeš prikazati sve skrivene oblačiće ili vratiti automatski raspored svih oblačića.
- Pozicije i skrivenost čuvaju se u projektu, uključeni su u undo/redo i automatsko spremanje. Pozicije su relativne prema raspoloživom prostoru grafa pa se prilagođavaju rezoluciji izvoza. Promjena naziva ili redoslijeda signala ne gubi ove postavke.
- Ručni raspored vezan je uz minimum/maksimum signala, a ne uz određeni uzorak: ako promijeniš vremenski raspon, oblačić označava ekstrem novog raspona.

## Dodatne mogućnosti

- **Graf:** naslov, naziv i jedinica vremenske osi te vremenski raspon. Nakon zooma **Koristi pogled** prenosi raspon u izvoz i statistiku. Vremenske postavke su sekunde od prvog uzorka CSV-a; nula prikazanog vremena pomiče prikaz osi.
- **Signali / dodatno:** faktor, offset, stil i debljina linije te panel 1–4 unutar iste slike. Novi CSV uvijek dobiva zaseban graf.
- **Dodatni alati:** statistika intervala, dva kursora, ručne bilješke i izvedeni signali A−B / derivacija. Izvedeni signali su snimka izračuna; ne preračunavaju se automatski nakon promjene izvora. Njihovo ime također unosi korisnik.
- **Predlošci:** čuvaju izgled, korisnička imena, jedinice i granice normalizacije. Primjenjuju se samo na točno podudarne izvorne PLC nazive.
- **Projekti:** `.tracestudio` čuva sve snimke i njihove postavke; izvorni CSV-ovi nisu potrebni. Postojeće korisničke bilješke u starijim projektima ostaju sačuvane.
- **Grupni izvoz:** cijeli CSV-ovi u JPG, bez bilješki/kursora. Imena preuzima iz podudarnih postavki; neimenovani uključeni signali zaustavljaju izvoz te datoteke. Postojeće slike ne prepisuje.
- **Undo/redo:** posljednjih 20 potvrđenih stanja, dostupno pod Graf / Dodatni izgled.
- **Oporavak:** potvrđeno stanje svih učitanih CSV-ova sprema se svakih 30 s u `%LOCALAPPDATA%/TiaTraceStudio/recovery.tiasession`; nakon prekida nudi se obnova. Podržana je i obnova starog `recovery.tiatrace`. Nepotvrđena polja nisu dio automatske snimke.
- Ctrl+O: CSV; Ctrl+S: projekt; Ctrl+E: izvoz. Miš: kotačić = zoom, povlačenje = pomicanje, Shift+klik = kursor A, Ctrl+klik = B.

## Uvoz i izvoz

CSV/TSV podržava zarez, točka-zarez i tabulator, navodnike, BOM, preambulu, decimalni zarez uz točka-zarez/tab i bool vrijednosti. Numeričko vrijeme u ms/us pretvara se u sekunde; velika vremena oduzimaju se kao decimalni brojevi radi očuvanja preciznosti. Tekstualni kalendarski datumi nisu podržani.

Neispravni redci i nerastuća vremena prijavljuju se i preskaču. Nedostajuće vrijednosti ostaju praznine. Vremenski razmaci veći od 1,5 × medijana prijavljuju se; valjani susjedni uzorci povezuju se linijom.

JPG/PNG renderira se u zadanoj rezoluciji, ne kao screenshot prozora. Maksimalno 32 megapiksela uz provjeru dimenzija radi čitljivosti panela.

## Razvoj

Potreban je .NET 10 SDK na Windowsu. `build.ps1` koristi lokalni SDK ovog projekta ili sistemski dotnet.

```powershell
./build.ps1
./build.ps1 -TestCsv 'C:/putanja/Nozzle1_Reg_Ramp_100s_250ms.csv'
```

Testovi koriste originalni primjer s 393 uzorka te sintetičke podatke za bar/m/rpm, normalizaciju, istodobne ekstreme i konstantne signale. Testna imena poput Kanal 1 služe samo provjerama i ne dodjeljuju se pri uvozu u aplikaciji. Izvori su u `src/TiaTraceStudio`, provjere u `tests/TiaTraceStudio.Checks`.
