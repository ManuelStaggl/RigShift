# Befunde RigShift 1.3.0 – Analyse vor der Hardware-Testrunde

Stand: 2026-09-14 · Grundlage `main` (b77948d, Release 1.3.0 + Ko-fi-Knopf) · Auftrag: `AUFTRAG-analyse-1.3.md` ·
Umsetzung: `umsetzungsplan-1.3.md`

## Kurzfazit

Der Kern ist solide: ein atomarer `SetDisplayConfig`, Wiederholung bei 31/1610, Modus-Fallback, Rollback und Nachholen
folgen den harten Regeln; Interop und Pipe sind sauber; die Ressourcen sind 234/234 paritätisch; Pakete haben weder
Schwachstellen noch offene Updates. Im Leerlauf ist das Tool so klein wie gewünscht: **124 MB Working Set, 0,05 s CPU
pro Minute, kein Wachstum über 35 Minuten, keine Logzeile.** Größte Risiken: (1) die Windows-Einstellung
„Anruf-Absenkung“ bleibt nach einem Absturz oder Neustart dauerhaft verstellt, weil der Vorher-Wert nur im Speicher
liegt; (2) Beenden oder Abmelden während eines Wechsels bricht Rollback und Wiederherstellung mitten ab; (3) der
USB-Trigger schaltet seinen Regelzustand scharf, bevor das Ergebnis feststeht – nach einem abgelehnten, blockierten
oder verworfenen Wechsel reagiert er erst nach Aus- und Einschalten der Wheelbase; (4) die Profilseite ist unter etwa
800 px Fensterbreite unbrauchbar und die Status-Leiste dort nach einmaligem Schließen tot; (5) die App-Schicht
(Koordinator, Settings, Automatik-Service, Pipe, Update) hat keinen einzigen Test. Zahlen: **1 Kritisch, 6 Hoch,
33 Mittel, 49 Niedrig.**

## Methode

- **Code-Durchsicht** aller Bereiche A–O mit sechs parallelen Subagents (je Bereich einer), jeder Befund danach am
  Code gegengeprüft; nicht bestätigte Aussagen sind gestrichen oder als „Vermutung“ markiert.
- **Build und Tests:** `dotnet build RigShift.slnx` 0 Warnungen; `dotnet test --solution RigShift.slnx` 241/241 grün
  (971 ms). `dotnet list package --vulnerable --include-transitive` und `--outdated`: nichts (alle sechs Projekte).
- **Messungen** mit Release-Build (`dotnet publish -c Release`, Single-File, self-contained, R2R, 236 MB EXE) am
  Home-Server (Windows Server 2025, RDP-Sitzung, 125 % DPI, eine RDP-Anzeige, 0 Audio-Endpunkte):
  - Leerlauf: App mit `--minimized`, eine USB-Regel mit nie verbundenem Gerät, 0 Profile; `Get-Process` +
    `GetGuiResources` alle 30 s über 35 min (70 Messpunkte, `idle.csv`).
  - Start: Zeit vom Prozessstart bis die Pipe `\\.\pipe\RigShift` existiert, 6 Starts.
  - CLI: `status`, `list`, `save`, `apply --dry-run` je 5× sequenziell, 24 parallel, dann 50 Profile per `save`, dann
    defekte/riesige/gesperrte Dateien, defekte `settings.json` beim Start.
  - UI: Debug-Build mit `--preview-theme`, Screenshots per `PrintWindow` in de/en, hell/dunkel, bei Mindestgröße
    (720×480 logisch), 784 px, Standard 980 px und 1120 px, Editor bei 560/760/1000 px; Countdown, Tray-Popup,
    Erkennen-Fenster, Erststart ohne Profile. Bilder in `screenshots/`.
- **Nicht prüfbar am Server** (→ Abschnitt „Hardware-Testrunde“ im Plan): echtes Umschalten, Audio, HDR, Hz,
  Fensterrettung, USB-Trigger mit echtem Gerät, Stromspar-Flags echter Sim-Hardware, Standby/Resume, Velopack-Update,
  Portable-Variante, zweites Benutzerkonto.

## Messwerte

| Größe | Wert | Weg |
|---|---|---|
| Start bis Pipe bereit | 549–638 ms (6 Starts, Median 579 ms) | Stopwatch + `\\.\pipe\` |
| Working Set nach 2 s / nach 10 min / nach 35 min | 117 MB / 123,6 MB / 123,6 MB | `Get-Process` |
| Private Bytes 0 → 35 min | 45,4 → 50,3 MB (Maximum 50,4) | `Get-Process` |
| CPU im Leerlauf (Min. 10–35) | 0,047 s/min ≈ 0,08 % eines Kerns, mit 2-s-USB-Poll | `TotalProcessorTime` |
| Threads / Handles / GDI / USER (35 min) | 26→17 / 709→698 / 7 / 29 – kein Anstieg | `Get-Process`, `GetGuiResources` |
| Logzeilen im Leerlauf (35 min) | 0 | Logdatei |
| Logzeilen pro Start | 15 (davon 6 Hosting-Zeilen ohne Nutzen) | Logdatei |
| CLI `status` / `list` / `apply --dry-run` | 120 / 120 / 125 ms (5× je) | Prozessstart bis Exit |
| 24 CLI-Aufrufe parallel | alle exit 0, 116–146 ms je, 848 ms gesamt, keine Pipe-Warnung | Thread-Jobs |
| 50× `save` | 128 ms je, 6,4 s gesamt; danach `list` 125 ms, `status` 126 ms | wie oben |
| Working Set nach 24 parallelen Aufrufen / nach 50 Saves | 138 MB / 157 MB (51 Profile, jeder Save lädt alle Profile neu) | `Get-Process` |
| Start mit defekter `settings.json` | 630 ms, Standardwerte, eine Warnung im Log, CLI funktioniert | Test 5 |
| Setup / Portable / Full / Delta (Release 1.3.0) | 98,5 / 93,9 / 94,0 / 35,0 MB; EXE im Publish 236 MB | `gh release view` |
| Tests | 241/241, 971 ms; 184 Attribute; App-Projekt 0 Tests | `dotnet test` |

## Befund-Tabelle

Schweregrad: K = Kritisch, H = Hoch, M = Mittel, N = Niedrig. Beleg: „belegt“ (Datei:Zeile, Messung, Screenshot,
Reproduktion) oder „Vermutung“. GPC = braucht Gaming-PC zur Verifikation.

| ID | Bereich | Titel | Schwere | Aufwand | Beleg | GPC | Empfehlung |
|---|---|---|---|---|---|---|---|
| A-01 | A | Tote Interop-Einträge und nicht existierende Typen in der Doku | M | S | belegt | nein | `NativeMethods.txt:43-51,32,49,67` streichen; `ARCHITECTURE.md:49`, `PLAN.md:97` korrigieren |
| A-02 | A | Hz-Berechnung fünfmal dupliziert trotz `RefreshRate.Hertz` | M | S | belegt | nein | alle Stellen auf `RefreshRate.Of(...).Hertz` |
| A-03 | A | `SwitchOrchestrator` 906 Zeilen, `ViewModels.cs` acht Typen | M | M | belegt | nein | Audio/Apps/Ducking extrahieren, Datei pro Typ – nach der Testrunde |
| A-04 | A | Reste der Spiel-Automatik (`usb:`-Präfix, Schlüssellisten, Kommentare, Tests) | N | S | belegt | nein | Generalität entfernen, `null`-Filter behalten |
| A-05 | A | Aufräumliste: ungenutzte Member, doppelte Konstanten, wirkungslose Analyzer-Zeile, `dotnet format` scheitert, `Host.CreateDefaultBuilder` | N | S | belegt | nein | Sammel-Commit |
| A-06 | A | Veraltete Kommentare und Planverweise (`%LocalAppData%`, „section 10, M4“) | N | S | belegt | nein | Sammel-Commit mit N-02 |
| A-07 | A | `SetPausedAsync` ohne Fehlerbehandlung, Fire-and-forget im ViewModel | N | S | belegt | nein | try/catch + Log wie `SaveAsync` |
| A-08 | A | Geräteauswahl „nicht verbunden“ doppelt gebaut | N | S | belegt | nein | gemeinsame Hilfsfunktion |
| A-09 | A | Synchrone Windows-Aufrufe hinter `Task.FromResult`, Aufrufer wickeln in `Task.Run` | N | M | belegt | nein | nicht umsetzen, dokumentieren |
| B-01 | B | Anruf-Absenkung: Vorher-Wert nur im Speicher – nach Absturz/Neustart bleibt Windows verstellt | **K** | M | belegt | nein | Wert in `settings.json` merken, beim Start prüfen |
| B-02 | B | Beenden/Abmelden während eines Wechsels bricht Rollback ab; kein Abbruch-Token | H | M | belegt | ja (Endzustand) | `Quit`/`SessionEnding` warten oder verweigern, App-weite CTS |
| B-03 | B | Warten auf USB-Gerät hält das Wechsel-Gate bis 300 s, nicht abbrechbar | M | M | belegt | nein | Apps-Phase als Nachlauf außerhalb des Gates |
| B-04 | B | Fensterrettung ruft `SetWindowPlacement` synchron auf hängende Fremdfenster | M | S | Vermutung | ja | `IsHungAppWindow` prüfen, Zeitlimit |
| B-05 | B | Rollback-/Wiederherstellungs-Ergebnis fehlt im Toast | M | S | belegt | nein | Note-Enum in `SwitchResult`, lokalisiert in den Toast |
| B-06 | B | Nachholen (`CatchUpAsync`) ohne Wiederherstellung bei Fehlschlag | M | S | belegt | ja | `before`-Snapshot + `RestoreAfterFailureAsync` |
| B-07 | B | Ausnahme aus Query/Apply während des Retry-Zyklus → `Failed` ohne Wiederherstellung | M | S | belegt (Codepfad), Vermutung (Auslöser) | ja | try/catch um den Zyklus, Restore, Test |
| B-08 | B | Fehlerfolge `[31, 87]` bricht ohne Warten ab | N | S | belegt | nein | transient, wenn irgendein Versuch 31/1610 |
| B-09 | B | Vor-Warten und Retry teilen dasselbe 20-s-Budget | N | S | belegt | ja | Budget nach erfolgreichem Vor-Warten neu starten |
| B-10 | B | EDID-Fallback rät bei baugleichen Monitoren (2× CM27X3) | N | S | belegt | nein | bei Zwillingen und einem Kandidaten nicht matchen |
| B-11 | B | Nach Datenbank-Modi-Fallback keine Prüfung gegen den Plan | N | M | belegt | ja | Nach-Snapshot vergleichen → `AppliedPartially`/Warnung |
| B-12 | B | HDR direkt nach Apply: `null` wird still übersprungen | N | S | Vermutung | ja | nach 1 s erneut abfragen |
| B-13 | B | Dry-Run („Prüfen“) belegt das Gate und `IsSwitching` | N | S | belegt | nein | Dry-Run ohne Gate |
| B-14 | B | Fensterrettung vor dem Countdown und beim Rollback verschiebt Fenster ohne Rückweg | N | M | belegt | ja | Rettung nach Bestätigung bzw. Placements merken |
| C-01 | C | Regelzustand wird scharf, bevor der Wechsel gelungen ist (busy, Blocked, Failed, abgelehnt) | H | S | belegt | nein | `SwitchResult` auswerten, bei Misserfolg `Unarm(rule)` |
| C-02 | C | Wartezeit 0 s: ein Poll-Aussetzer = zwei Umschaltungen | M | S | belegt (Codepfad), Vermutung (Auslöser) | ja | Ende frühestens beim zweiten Poll ohne Gerät |
| C-03 | C | Wanduhr statt monotoner Zeit; Frist läuft im Standby ab | M | S | belegt (Codepfad), Vermutung (Wirkung) | ja | `GetTimestamp`, `Reset()` bei Resume |
| C-04 | C | Stromspar-Warnung: zählt alte Port-Instanzen, liest KMDF-Werte nicht, `SecurityException` still | M | M | belegt (Code), Vermutung (Flags) | ja | auf verbundene Instanzen filtern, `WDF`-Werte lesen, am Gaming-PC belegen |
| C-05 | C | Zwei Regeln auf ein Gerät ohne Hinweis; SwitchBack ohne Vorprofil ohne Log | N | S | belegt | nein | Duplikat-Hinweis, Info-Log |
| C-06 | C | Aktives Profil direkt nach dem Wechsel veraltet (`RefreshActiveAsync` Fire-and-forget) | N | S | belegt | nein | im Koordinator awaiten |
| C-07 | C | Baugleiche Geräte zählen als eines; Hub-Filter nur per Name | N | – | belegt | nein | in der Hilfe erwähnen |
| C-08 | C | Poll-Doppelarbeit (zweites HashSet, Regex pro Instanz) | N | S | belegt, gemessen unerheblich | nein | nicht umsetzen |
| D-01 | D | Leerlauf ohne Leck, Start 0,6 s | – | – | gemessen | nein | nichts |
| D-02 | D | Paketgröße 94–98 MB, EXE 236 MB, Delta 35 MB (Single-File + R2R + self-contained) | M | S | gemessen | nein | Nutzer entscheidet (Plan, Abschnitt „Nutzer entscheidet“) |
| D-03 | D | Working Set +19 MB nach 50 Saves (jeder Save lädt alle Profile neu, Verlauf/Tray werden neu gebaut) | N | S | gemessen | nein | beobachten; Reload nur des betroffenen Profils wäre S |
| E-01 | E | CLI sequenziell und parallel, 50 Profile, Riesen-/Defektdateien: nichts gefunden | – | – | gemessen | nein | nichts |
| F-01 | F | `SettingsService.Current` wird vor dem Speichern gesetzt – Schreibfehler unsichtbar | M | S | belegt | nein | erst speichern, dann `Current` |
| F-02 | F | Deinstallation lässt den HKCU-Run-Eintrag stehen | M | S | belegt | nein | im Uninstall-Hook entfernen; Datenordner-Verbleib dokumentieren |
| F-03 | F | Velopack ohne Logger – Update-Fehler landen in keinem Log | M | S | belegt | ja | `SetLogger` + `UpdateManager(logger:)` |
| F-04 | F | `.tmp` bleibt bei `File.Move`-Fehler liegen, Leser ohne `FileShare.Delete` | N | S | belegt | nein | FileShare erweitern, Retry, tmp-Cleanup |
| F-05 | F | Unlesbare oder gesperrte Profildatei verschwindet still; `save` legt ein Duplikat an | M | S | belegt (Reproduktion) | nein | übersprungene Dateien melden (InfoBar), `save` bei Lesefehler abbrechen |
| F-06 | F | CLI `save` und offener Editor: Last-Writer-Wins ohne Hinweis | N | S | belegt | nein | Doku-Hinweis oder `LastWriteTime`-Prüfung |
| F-07 | F | Downgrade 1.3 → 1.2 verliert 1.3-Felder still (`schemaVersion` blieb 1) | N | – | belegt | nein | Hinweis, kein Fix |
| G-01 | G | Core-Audio-COM-Objekte werden nicht freigegeben (RCW bis zum GC) | N | S | belegt | nein | `finally { ReleaseComObject }` |
| G-02 | G | HDR-Fallback auf Legacy-API bei jedem Fehler, Fehlercode von `SET_HDR_STATE` geht verloren | N | S | Vermutung | ja | Fallback nur vor 24H2, beide Codes loggen |
| G-03 | G | App-Stop per Prozessname trifft fremde Prozesse; `Kill()` ohne Prozessbaum | N | S | belegt | nein | Pfad vergleichen wo lesbar, Editor-Hinweis |
| G-04 | G | Verknüpfung: Profilname mit `\` am Ende bricht das Argument | N | S | belegt | nein | Backslashes escapen oder in `Validate` ablehnen |
| H-01 | H | Pipe-Server stirbt still, wenn ein zweites Benutzerkonto RigShift startet | M | S | Vermutung (Exception-Typ), belegt (nur `IOException` gefangen) | ja (2 Konten) | `UnauthorizedAccessException` fangen, Pipe-Name pro Sitzung |
| H-02 | H | `rigshift://`-Link schaltet ohne Rückfrage, wenn die Bestätigung aus ist; README/CHANGELOG behaupten das Gegenteil | M | S | belegt | nein | Link erzwingt Bestätigung (`FromLink`), Doku nachziehen |
| H-03 | H | Unsignierte Updates, Vertrauensanker GitHub-Konto, Actions per Tag | M | – | belegt | nein | in `SECURITY.md` benennen, Actions per SHA pinnen; Signing bleibt gestrichen |
| H-04 | H | Pipe: 4 Instanzen, kein Lese-Timeout – stiller Client blockiert CLI | N | S | belegt | nein | 10-s-Timeout bis zum vollständigen Request |
| H-05 | H | Diagnosebericht mit Gerätepfaden, Logs mit Benutzername in Pfaden und einer Endpoint-ID | N | S | belegt | nein | Hinweis unter dem Kopieren-Knopf und im README |
| I-01 | I | Profilseite unter ~800 px Breite unbrauchbar (Titel überdeckt, Karte zeichenweise umgebrochen) | H | S | belegt (Screenshots) | nein | Kopfzeile umbrechen, Kartenlayout mit `*`-Spalte und Trimming |
| I-02 | I | Status-InfoBar der Profilseite nach einmaligem Schließen tot | H | S | belegt (Reflection: `IsOpen` ist OneWay) | nein | `Mode=TwoWay`, vor `ShowStatus` zurücksetzen |
| I-03 | I | Regelkarte: Texte und Geräteauswahl abgeschnitten bis unter 1400 px | M | M | belegt (Screenshots) | nein | zweizeiliges Layout |
| I-04 | I | Umschalt-Feedback nur als Tray-Ballon (im Vollbild unterdrückt), Ballon nicht klickbar | M | M | belegt | nein | Ergebnis im Fenster, Ballon-Klick → Über & Hilfe |
| I-05 | I | Tray-Popup wächst unbegrenzt (50 Profile > 2200 px) | M | S | belegt | nein | `ScrollViewer` mit `MaxHeight` |
| I-06 | I | Lange Profilnamen verdrängen die Badges; kein `MaxLength` | M | S | belegt | nein | Trimming, `MaxLength` |
| I-07 | I | Unbehandelte UI-Ausnahmen nur im Log, keine Rückmeldung | M | S | belegt | nein | Ballon/InfoBar `Status_Error` |
| I-08 | I | Editor: HDR-Auswahl bricht bei Standardbreite 760 um; Sekundenfeld bei Mindestbreite abgeschnitten | M | S | belegt (Screenshots) | nein | `WrapPanel`, Standardbreite 900 |
| I-09 | I | Einstellungen/Über & Hilfe: Texte bei Mindestbreite abgeschnitten | N | S | belegt (Screenshots) | nein | `TextWrapping` in `CardControl`-Headern |
| I-10 | I | Editor: Enter speichert nicht, Kommentar behauptet es | N | S | belegt | nein | `IsDefault="True"` |
| I-11 | I | Editor verwirft Änderungen ohne Rückfrage | N | M | belegt | nein | Dirty-Flag + Rückfrage |
| I-12 | I | Regel löschen ohne Rückfrage; „Ohne Bestätigung“ ohne Hinweis | N | S | belegt | nein | Rückfrage bzw. Hinweistext |
| I-13 | I | Sprachwechsel lässt gecachte Texte stehen | N | M | belegt | nein | `Loc.PropertyChanged` in betroffenen VMs |
| I-14 | I | Tastenkürzel zeigen interne Key-Namen (`OemComma`, `Prior`) | N | S | belegt | nein | `GetKeyNameText` |
| I-15 | I | Lösch-Dialog: Primär- statt Danger-Knopf, kein Owner | N | S | belegt/Vermutung | nein | `ControlAppearance.Danger`, `Owner` |
| I-16 | I | Einstellungen: Sekundenfeld speichert pro Tastendruck | N | S | belegt | nein | `LostFocus` wie in der Regelkarte |
| I-17 | I | Kleinkram: keine Zugriffstasten, `Page.Title` fest englisch, Akzentfarbe weicht vom Plan ab, LiveRegion ohne Ereignis, `SystemThemeWatcher.Watch` in `Loaded`, Sprach-Icon | N | S | belegt/Vermutung | nein | Sammel-Commit |
| I-18 | I | Regelkarten-ComboBoxen per UIA nicht auffindbar | N | – | Vermutung | nein | mit `inspect.exe` prüfen, nur bei Bedarf |
| J-01 | J | `Tray_Active` ungenutzt; `Page.Title` hart kodiert | N | S | belegt | nein | streichen bzw. `{loc:Tr Nav_*}` |
| J-02 | J | `SetLanguage` setzt die Formatkultur auf neutrales `de`/`en` (de-AT-Formate weg) | N | S | belegt | nein | nur UI-Kultur umschalten |
| J-03 | J | Typografie und Wortwahl (gerades Anführungszeichen, Ellipsen, Du/Infinitiv, „Wechsel“ vs „Umschaltung“, „recognised“) | N | S | belegt | nein | Sammel-Commit |
| K-01 | K | Keine Dauer im Log (Wechsel, Versuche, Audio, HDR, Apps) | M | S | belegt | nein | `Duration` und Schritt-Millisekunden loggen |
| K-02 | K | Automatik-Entscheidungen stumm (Gerät gesehen/weg, Frist, übersprungen, Baseline) | M | S | belegt | nein | Ereignisliste aus `Evaluate` loggen |
| K-03 | K | Bestätigung nur im Negativfall geloggt | N | S | belegt | nein | „confirmed after N s“ |
| K-04 | K | Kein Größenlimit (Serilog-Default 1 GB, dann stiller Stopp); Debug-Level; 6 Hosting-Zeilen pro Start | N | S | belegt | nein | 50 MB + `rollOnFileSizeLimit`, Hosting-Logger stummschalten |
| L-01 | L | Kein Testprojekt für die App-Schicht | H | M | belegt | nein | `tests/RigShift.App.Tests` |
| L-02 | L | Keine eingefrorenen 1.0/1.2-Fixtures; `ExitDelaySeconds` ungetestet | M | S | belegt | nein | Fixtures + Tests |
| L-03 | L | Fake-Configurator kann weder Ausnahmen noch HDR-Fehler pro Display noch Raten simulieren | M | S | belegt | nein | Fake erweitern, Tests zu B-07 |
| L-04 | L | Windows-Schicht nur Struktur-Tests; Snapshot-Aufbau nicht als reine Funktion | N | M | belegt | einmalig ja | Fixture aus echten Rohdaten (PLAN 7) |
| L-05 | L | Tests hängen an exakten Versuchszahlen (42 Versuche / 20 s) | N | S | belegt | nein | auf Bereiche prüfen |
| M-01 | M | CI führt `dotnet publish` nicht aus – Single-File/R2R-Fehler erst beim Tag | M | S | belegt | nein | Publish-Schritt in `ci.yml` |
| M-02 | M | SDK `rollForward: latestFeature`, Actions per Major-Tag | N | S | belegt | nein | `latestPatch`, SHA-Pins |
| M-03 | M | Kein Format-Check, kein `timeout-minutes`, keine `concurrency` | N | S | belegt | nein | ergänzen |
| M-04 | M | Release prüft nicht, dass `[Unreleased]` leer ist und das Datum stimmt | N | S | belegt | nein | zwei Zeilen im Versionsschritt |
| N-01 | N | README/CHANGELOG: „Link fragt immer nach Bestätigung“ ist falsch | H | S | belegt | nein | mit H-02 |
| N-02 | N | `%LocalAppData%` in PLAN 4.2/4.7 und drei Code-Kommentaren | M | S | belegt | nein | auf `%AppData%` |
| N-03 | N | ARCHITECTURE.md: nicht existierender Listener, falsche Schichtzuordnung, „≤ 60 s“, Exit-Code 5 fehlt, Windows.Tests fehlt, Profile-Felder veraltet | M | S | belegt | nein | Abschnitt neu schreiben |
| N-04 | N | ROADMAP: Dry-Run als v2 gelistet (existiert), 🟢 für ungetestete 1.3-Funktionen | M | S | belegt | nein | 🟡 setzen, Punkt streichen |
| N-05 | N | Kleinkram: CHANGELOG „30 s at most“, CLAUDE.md Probe-Befehle/Reihenfolge, PLAN 4.7 Diagnoseseite, PLAN 6 PowerPlan/Issue-Formular/`http-api.md`, About-Screenshot älter als Seite | N | S | belegt | nein | Sammel-Commit |
| N-06 | N | `usb-power-saving.md`: KMDF-Geräte nicht abgedeckt, „per Port“ nur ohne Seriennummer, nur Netzbetrieb geprüft | N | S | belegt | nein | drei Sätze präzisieren |
| O-01…O-08 | O | Umfangsfragen | – | – | – | – | Plan, Abschnitt „Nutzer entscheidet“ |

## Befunde ab Mittel

### A-01 Tote Interop-Einträge und nicht existierende Typen
Beobachtung: `src/RigShift.Windows/NativeMethods.txt:43-51` (`RegisterDeviceNotification`, `DEV_BROADCAST_*`,
`WM_DEVICECHANGE`, `DBT_*`), Zeile 32 `DXGI_ERROR_NOT_FOUND`, Zeilen 49/67 `WM_DISPLAYCHANGE`/`WM_HOTKEY` haben keinen
Verwender (`NativeWindow.cs:15-16` definiert eigene Konstanten). `docs/ARCHITECTURE.md:49` und `docs/PLAN.md:97`
nennen `IDeviceEvents`/`DeviceNotificationListener` – beide existieren nicht; Anzeigeänderungen fängt
`DisplayChangeWatcher` (`DesktopServices.cs:36-60`, App-Schicht), USB wird gepollt. Ursache: Entscheidung Polling
statt Notifications, Doku nicht nachgezogen. Auswirkung: unnötig generierter Code, irreführende Architektur-Doku.
Empfehlung: Einträge streichen, Doku-Zeilen ersetzen (mit N-03).

### A-02 Hz-Berechnung fünfmal
`RefreshRate.cs:6,11` liefert `Hertz`; trotzdem rechnen `ViewModels.cs:50,545`, `DiagnosticsReport.cs:99`,
`CommandRunner.cs:237`, `TopologyPlanner.cs:118` selbst. Empfehlung: `RefreshRate.Of(display).Hertz` überall.

### A-03 Übergroße Klassen
`SwitchOrchestrator.cs` bündelt Apply/Retry, Audio (461-682), Apps + Gerätewarten (514-577, 746-795), Keep-awake,
HDR, Ducking (802-870). `ViewModels.cs` hält acht Typen, `ProfileEditorViewModel.cs` fünf. Empfehlung: `AudioSwitcher`,
`AppRunner`, `DuckingSwitcher` als Core-Klassen hinter denselben Interfaces (Tests bleiben), Datei pro Typ – nach der
Testrunde, damit die Hardware-Logs sich auf den bekannten Code beziehen.

### B-01 Anruf-Absenkung bleibt nach Absturz oder Neustart verstellt (Kritisch)
Beobachtung: `SwitchOrchestrator.cs:41-44` – `_duckingBeforeProfiles` „lives only as long as the process“;
`SwitchDucking` (`:802-830`) schreibt HKCU `UserDuckingPreference = 3` und stellt nur zurück, wenn das Feld gesetzt
ist. Ursache: Der Vorher-Wert wird nirgends persistiert. Auswirkung: Absturz, Windows-Neustart, Update-Neustart oder
„Beenden“, während ein Profil mit `DisableCommunicationsDucking` aktiv ist → beim nächsten Start ist das Feld leer,
das nächste Profil ohne Flag stellt nichts zurück, Windows senkt dauerhaft keine Sounds mehr bei Anrufen – und der
Nutzer erfährt es nicht (nach der Schweregrad-Tabelle: „Einstellung bleibt verstellt“). Empfehlung: den Wert in
`settings.json` ablegen (`DuckingBeforeProfiles: int?`, `HasDuckingMemory: bool`, beide `set` wegen der
Source-Generator-Falle), beim Wechsel schreiben/löschen, beim Start lesen; zusätzlich beim Start wie
`KeepAwakeForActiveProfile` (`App.xaml.cs`) prüfen: aktives Profil ohne Flag + Erinnerung vorhanden → zurückstellen.

### B-02 Beenden oder Abmelden während eines Wechsels (Hoch)
Beobachtung: `App.xaml.cs:58-62` `Quit()` ruft sofort `Shutdown()`; `OnExit` (`:136-141`) disposed den Host, ohne auf
den `SwitchCoordinator` zu warten; der Orchestrator läuft in `Task.Run` mit `CancellationToken.None`
(`SwitchCoordinator.cs:95,152`), `IProfileSwitcher.SwitchAsync` ignoriert den übergebenen Token (`:73-78`).
`ConfirmationWindow.OnClosing` (`:72-78`) liefert beim App-Ende `Rejected` → Rollback startet auf dem Threadpool,
während der Prozess endet. `UpdateService.CanInstallNow` prüft `IsSwitching`, `Quit` nicht. Auswirkung: Tray →
„Beenden“, Update-Neustart oder Abmelden im Countdown: Topologie halb, Audio evtl. teilweise zurück, Ducking siehe
B-01. Empfehlung: `Quit()` bei `IsSwitching` verweigern oder mit Hinweis bis 30 s warten (`SwitchCoordinator` bekommt
`Task? Current`); `SessionEnding` ebenso; App-weite `CancellationTokenSource`, die `Quit()` auslöst, durch den
Koordinator an den Orchestrator reichen.

### B-03 Warten auf USB-Gerät hält das Gate
`RunAppsAsync` läuft innerhalb `SwitchAsync` (`SwitchOrchestrator.cs:157`), Wartezeit 5–300 s (`:753`); solange
bleiben Gate und `IsSwitching` belegt (`SwitchCoordinator.cs:83,90`): Hotkeys melden „Busy“, Automatik überspringt
Polls, CLI `apply` liefert Exit 1, Tray zeigt „schaltet um“, Update-Installation gesperrt; der Nutzer kann nicht
abbrechen, obwohl das Profil längst bestätigt ist. Empfehlung: Apps-Phase als Nachlauf nach dem Ergebnis mit eigenem
`CancellationTokenSource`, den ein neuer Wechsel abbricht; `IsSwitching` fällt mit dem Ergebnis.

### B-04 Fensterrettung ohne Schutz vor hängenden Fenstern (Vermutung, Gaming-PC)
`WindowRescuer.cs:128` `SetWindowPlacement` synchron auf dem Orchestrator-Thread für jedes fremde Fenster; kein
`IsHungAppWindow`, kein Zeitlimit. Hängt ein Launcher (Shader-Compile), blockiert der Wechsel vor dem Countdown.
Empfehlung: `IsHungAppWindow` prüfen und überspringen; Rettung mit Zeitlimit (3 s).

### B-05 Rollback-Ergebnis fehlt im Toast
`SwitchOrchestrator.cs:230-243` und `:262-292` liefern `Failed` mit „restoring the previous topology failed/
restored“; `AppServices.cs` `ForNotification` nutzt `record.Message` nicht. Der Nutzer sieht „Fehler 87“, aber nicht,
ob der Bildschirm im alten oder neuen Zustand ist – bei dunklem Display die entscheidende Information. Empfehlung:
`SwitchResult.Note` als Enum (`RestoredPrevious | RestoreFailed`), lokalisiert im Toast.

### B-06 Nachholen ohne Wiederherstellung
`CatchUpAsync` (`SwitchOrchestrator.cs:178-207`) → `ApplyWithRetryAsync` ohne `before`-Snapshot, kein
`RestoreAfterFailureAsync` (vgl. `:125`). Genau der Fall aus Regel 8 (spacedesk verbindet, Windows springt zum
Schreibtisch, CatchUp versucht den Rig): scheitert er mit 31/1610 und lässt Displays dunkel, passiert nichts weiter.
Empfehlung: Snapshot vorher merken, bei Fehlschlag wiederherstellen, `Message` mitgeben.

### B-07 Ausnahme im Retry-Zyklus → keine Wiederherstellung
`CcdNative.QueryAllPaths` wirft `Win32Exception`; der Orchestrator hat um `ApplyWithRetryAsync`/`PollTopologyAsync`
keinen `catch`, nur `SwitchCoordinator.cs:105`. Wirft die Abfrage nach einem fehlgeschlagenen Versuch, der Displays
dunkel ließ, endet der Wechsel als `Failed` ohne `RestoreAfterFailureAsync`. Der Auslöser ist am Server nicht
reproduzierbar (Vermutung), der Codepfad belegt; der Fake kann ihn heute nicht simulieren (L-03). Empfehlung:
Ausnahmen im Zyklus fangen, wie einen Fehlschlag behandeln, Test `Switch_ApplyThrows_RestoresPreviousTopology`.

### C-01 Regelzustand scharf vor dem Ergebnis (Hoch)
`AutomationTrigger.cs:133-137` setzt `IsRunning`/`StartedByRule`, bevor `AutomationService.RunAsync`
(`:123-137`) den Wechsel startet; der Rückgabewert von `SwitchAsync` wird nicht ausgewertet – `null` bei belegtem
Gate (`SwitchCoordinator.cs:83-87`), `Blocked`, `Failed` oder `RolledBack` (Nutzer lehnt ab) zählen alle als
„gestartet“. Auswirkung: Wheelbase an → Wechsel blockiert (z. B. G9 noch nicht wach) oder abgelehnt → nichts
passiert mehr, bis das Gerät aus- und nach Ablauf der Frist wieder eingeschaltet wird; und später schaltet SwitchBack
bei einem manuell erreichten Rig-Profil ungewollt zurück (`:148`). Empfehlung: `RunAsync` wertet `SwitchResult?` aus;
bei `null`/`Blocked`/`Failed`/`RolledBack` `_trigger.Unarm(rule)` (Zustand auf „nicht gestartet“), damit der nächste
Poll mit noch verbundenem Gerät erneut auslöst – bei `Blocked` mit Log, damit es nicht alle 2 s klappert (z. B. erst
nach der Frist erneut).

### C-02 Wartezeit 0 s ohne Entprellung
`AutomationTrigger.cs:114` `now - gone >= ExitDelayOf(rule)` ist bei 0 s im selben Poll wahr. Ein einzelner Poll
ohne Gerät (Re-Enumeration nach Standby, Selective-Suspend-Reconnect) erzeugt Ende- und Start-Aktion = zwei
Umschaltungen mit Countdown. Empfehlung: Ende frühestens beim zweiten Poll ohne Gerät; UI darf weiter 0 zeigen.

### C-03 Wanduhr statt monotoner Zeit
`AutomationService.cs:105` reicht `_time.GetUtcNow()` als `now`; `AutomationTrigger.cs:111` merkt `GoneSince`
damit. `DispatcherTimer` tickt im Standby nicht, die Uhr läuft weiter: Wheelbase aus, PC schläft innerhalb der Frist
→ erster Poll nach dem Aufwachen sieht die Frist als abgelaufen und schaltet zurück. NTP-Sprünge verkürzen oder
verlängern die Frist. Empfehlung: `GetTimestamp`/`GetElapsedTime`, bei `PowerModeChanged(Resume)` `Reset()` (Baseline
neu wie beim Pausieren).

### C-04 Stromspar-Warnung: falsche Positive und Negative
`UsbPowerCheck.cs:84-94` zählt alle Instanzen unter `Enum\USB\<VID&PID>` – auch Ports, an denen das Gerät früher
steckte: ein alter Port mit Flag warnt dauerhaft, obwohl der aktuelle sauber ist. Gelesen werden nur
`EnhancedPowerManagementEnabled`/`SelectiveSuspendEnabled` (`:22`); Geräte mit KMDF-/WinUSB-Herstellertreiber
(Thrustmaster, Logitech G Hub) speichern die Nutzerwahl unter `Device Parameters\WDF` (`IdleInWorkingState`,
`UserSetDeviceIdleEnabled`) → keine Warnung. `SecurityException` wird nur als Debug geloggt und ergibt still „keine
Warnung“ (`:97-100`). Empfehlung: Instanzen mit `PresentInstanceIds()` abgleichen, `WDF`-Werte lesen, am Gaming-PC
mit `Probe usb-power <id>` vor/nach dem Kästchen belegen, welcher Wert kippt.

### D-02 Paketgröße
Setup 98,5 MB, Portable 93,9 MB, Full 94,0 MB, Delta 35,0 MB (Release 1.3.0); die Publish-EXE hat 236 MB
(`RigShift.App.csproj:15-19`: self-contained, Single-File, R2R). Für ein „kleines Tool“ ist das der sichtbarste
Widerspruch. Optionen: (a) so lassen; (b) `PublishSingleFile=false` (Velopack packt den Ordner ohnehin, Deltas
werden klein); (c) framework-dependent (~5 MB Setup, aber .NET-10-Desktop-Runtime muss auf dem Gaming-PC sein –
Velopack kann sie mit `--framework net10.0-x64-desktop` nachinstallieren). Nutzerentscheidung, siehe Plan.

### F-01 `Current` vor dem Speichern
`AppServices.cs:59-60` setzt `Current = updated` vor `await store.SaveAsync(...)`. Wirft `SaveAsync`
(schreibgeschützt, gesperrt), zeigt die UI den neuen Wert, `Changed` bleibt aus, nach Neustart ist der Wert weg;
die ViewModels loggen nur. Empfehlung: erst speichern, dann `Current` setzen.

### F-02 Run-Key bleibt bei Deinstallation
`Program.cs` `OnBeforeUninstallFastCallback` ruft nur `UriSchemeRegistration.Unregister()`; `RunKeyAutostart` hat
keinen Uninstall-Pfad. Nach der Deinstallation zeigt HKCU\…\Run auf eine gelöschte EXE. Zusätzlich bleibt
`%AppData%\RigShift` liegen – vertretbar, aber undokumentiert. Empfehlung: im Hook den Run-Eintrag entfernen; README
erwähnt den Datenordner.

### F-03 Velopack ohne Logger
`VelopackApp.Build()` und `new UpdateManager(...)` (`UpdateService.cs:41`) ohne `SetLogger`/`logger:`. Ein Fehler beim
Anwenden eines Updates (Update.exe, Hooks, Delta) landet in keinem `rigshift-*.log` – „Update hat nichts gemacht“ ist
dann nicht auswertbar. Empfehlung: Serilog über `Serilog.Extensions.Logging` als MEL-Logger an beide geben (früher
File-Logger, da `Run()` vor `Log.Logger` läuft).

### F-05 Gesperrte oder unlesbare Profildatei (belegt)
Reproduktion am Server: `save LockTest` → Datei exklusiv öffnen → `list` meldet „No profiles.“ (Log: „could not be
read, skipped“, `JsonProfileStore.cs:167-170`) → `save LockTest` legt eine **zweite** Datei an → nach dem Entsperren
zeigt `list` zwei „LockTest“. Backup-Tools und Virenscanner halten Dateien kurz offen; die Profilseite zeigt nichts.
Empfehlung: Leser mit `FileShare.ReadWrite | FileShare.Delete`, einmaliger Retry, übersprungene Dateien als InfoBar
melden, `save` bei Lesefehlern abbrechen statt neu anlegen.

### H-01 Pipe-Server stirbt still bei zweitem Benutzerkonto
`CommandPipeServer.cs:52-55` fängt beim Anlegen nur `IOException`; der Mutex ist sitzungslokal (`Local\`,
`Program.cs:14`), der Pipe-Name rechnerweit. Startet Benutzer B RigShift, während A es laufen hat, gehört die Pipe A
mit `CurrentUserOnly`-DACL; `CreateNamedPipe` liefert ACCESS_DENIED, .NET wirft `UnauthorizedAccessException`
(Vermutung, aus dem .NET-Verhalten abgeleitet, nicht reproduziert), der Fire-and-forget-Task (`:35`) faultet ohne
Log. Für B funktionieren CLI, Verknüpfungen und Links nie. Empfehlung: Ausnahme fangen und loggen, Pipe-Name mit
Sitzungs-ID, Listener-Task mit Fehler-Fortsetzung.

### H-02 Link ohne Rückfrage (mit N-01)
`RigShiftUri.ToArguments` liefert `["apply", name]`; `CommandRunner.cs:133` übergibt `SkipConfirmation = false`; die
Bestätigung hängt allein am Timeout (`SwitchOrchestrator.cs:114`). Bei Profil-Timeout 0 oder Schalter „Nach dem
Umschalten bestätigen“ aus schaltet eine Webseite (nach dem Browser-Prompt) Bildschirme, Audio und Apps um. README:91
und CHANGELOG (1.2.0) behaupten „always asks for confirmation“. `--no-confirm` ist per Link nicht erreichbar (belegt).
Empfehlung: `SwitchRequest.FromLink` → Bestätigung immer (Timeout mindestens Standard); Doku wird damit wahr.

### H-03 Update-Kette (Restrisiko, nicht umsetzen)
`release.yml` ohne Signatur, Actions per Major-Tag; Velopack prüft SHA256 gegen den Release-Feed, Vertrauensanker ist
das GitHub-Konto; Auto-Apply beim nächsten Start ist Standard. Code-Signing ist vom Nutzer gestrichen. Empfehlung:
Actions per SHA pinnen, Tag-Schutz und 2FA, Restrisiko in `SECURITY.md` benennen.

### I-01 Profilseite bei kleiner Breite unbrauchbar (Hoch)
Screenshots `profiles-min-720-de-light.png` (Mindestbreite 720 lg): Seitentitel weg, Kopfknöpfe abgeschnitten, die
Karte bricht den Text zeichenweise um, Name und Knöpfe sind nicht mehr sichtbar. `firststart-profiles-784-en-dark.png`
(784 lg): Titel „Pro…“ von den Kopfknöpfen überdeckt. Ursache: `ProfilesPage.xaml:17-21` drei Knöpfe in einer
`Auto`-Spalte neben dem Titel; Karte `:51-81` horizontaler `StackPanel` ohne Trimming, Buttons in fester Spalte.
Empfehlung: Kopfzeile als `WrapPanel` (Knöpfe rutschen unter den Titel), Karte mit `*`-Spalte + `TextTrimming`,
Knöpfe bei schmalem Fenster nur als Icons; `MinWidth` erst dann senken, wenn es stimmt.

### I-02 Status-InfoBar tot nach Schließen (Hoch)
`ProfilesPage.xaml:26-27` `IsClosable="True" IsOpen="{Binding IsStatusOpen}"`; per Reflection auf `Wpf.Ui.dll` 4.3:
`InfoBar.IsOpenProperty` hat einfache `PropertyMetadata` (kein `BindsTwoWayByDefault`) → Bindung OneWay. Nach dem X
bleibt `IsStatusOpen` im VM `true`; `ShowStatus` (`ViewModels.cs:230-235`) setzt wieder `true` → keine Änderung, kein
`PropertyChanged`, keine Meldung mehr bis zum Neustart (Speichern, Duplizieren, Verknüpfung, Fehler). Empfehlung:
`Mode=TwoWay`, in `ShowStatus` vorher `false` setzen.

### I-03 Regelkarte abgeschnitten
`automation-784-de-dark.png`: „Wenn ein USB-Gerät verbun“, „Fanatec Whe“, „Ohne Bestätigung umschalte“ abgeschnitten,
Spinner fehlt; `automation-1120-en-dark.png`: Gerätename noch abgeschnitten. Ursache `AutomationPage.xaml:40-46`: zwei
`*`-Spalten plus feste, `NumberBox Width="130"`. Empfehlung: zweizeilig (Trigger-Zeile, Ende-Zeile), Gerätename mit
`ToolTip`.

### I-04 Umschalt-Feedback nur im Ballon
`DesktopServices.cs:141-142` meldet Ergebnisse nur per `ShowNotification`; die Profilseite zeigt nichts. Windows
unterdrückt Ballons bei aktivem Benachrichtigungsassistent – bei Vollbildspielen automatisch, also im Anwendungsfall.
„Details stehen im Log“ ist nicht klickbar (`TrayBalloonTipClicked` nur für Updates). Empfehlung: Ergebnis zusätzlich
als InfoBar/`CheckMessage` im Fenster, Ballon-Klick bei Failed/Blocked → Über & Hilfe, sichtbare „Wechsle …“-Zeile.

### I-05 Tray-Popup ohne Scrollgrenze
`TrayPopupView.xaml:54-101` `ItemsControl` ohne `ScrollViewer`/`MaxHeight` in `Border Width="320"`; 50 Profile ergeben
> 2200 px, Fußzeile außerhalb des Monitors. Empfehlung: `ScrollViewer MaxHeight` (60 % Arbeitsbereich).

### I-06 Lange Profilnamen
`ProfilesPage.xaml:51-81` Name ohne `TextTrimming` im horizontalen `StackPanel`, danach die Badges „Aktiv/Standard“;
`Editor_Name` ohne `MaxLength` (`Editor_CustomName` hat 40). Empfehlung: `*`-Spalte + `CharacterEllipsis`, `MaxLength`.

### I-07 Unbehandelte UI-Ausnahmen still
`App.xaml.cs:265-269` loggt und setzt `Handled`, ohne Hinweis. `AsyncRelayCommand`-Ausnahmen (z. B. `COMException`
in `DisplaysViewModel.RefreshAsync`, das nur `Win32Exception` fängt, `ViewModels.cs:493`) wirken wie „Knopf tut
nichts“. Empfehlung: einmalige, nicht modale Meldung (`Status_Error`) zusätzlich zum Log.

### I-08 Editor bei Standard- und Mindestbreite
`editor-760-en-dark.png`: HDR-Auswahl bricht unter die Hz-Auswahl (bekannt, Schwelle 1000); `editor-min-560-de-light.png`:
Sekundenfeld ohne Spinner abgeschnitten. `ProfileEditorWindow.xaml:10` `Width="760"`, `:153-219` vier Spalten ohne
Umbruch. Empfehlung: `WrapPanel` für RadioButton/CheckBox/Löschen, Hz und HDR in einer `WrapPanel`-Zeile,
Standardbreite 900.

### K-01 Keine Dauer im Log
`SwitchOrchestrator.cs:160` loggt „finished“ ohne Dauer (`Finish` schreibt sie nur ins Result);
`CcdDisplayConfigurator.cs:157` ohne Millisekunden; Audio/HDR/Apps ebenso. PLAN 4.7 verlangt „Versuche mit Fehlercode,
Dauer, Ergebnis“ – die Testrunde kann Wartezeiten (31 → Poll → Retry) sonst nicht rekonstruieren. Empfehlung:
`Duration` in Zeile 160/197, `GetTimestamp`/`GetElapsedTime` um Apply, Audio, HDR, Apps, Rettung.

### K-02 Automatik stumm
`AutomationService.cs:100-103` überspringt ohne Log; `AutomationTrigger.Evaluate` erkennt Gerät weg, zurück, Frist
läuft, Baseline, Regel geändert – ohne Rückgabe an den Logger; `Reset()` bei Pause ohne Log. „Warum hat er nicht
zurückgeschaltet?“ ist aus dem Log nicht zu beantworten. Empfehlung: `Evaluate` liefert eine Ereignisliste, der
Service loggt sie auf Information.

### L-01 Kein App-Testprojekt (Hoch)
`grep SettingsService|SwitchCoordinator|HotkeyService|CommandPipeServer|UpdateService tests/` → leer. F-01 hätte ein
Test gefangen; `SwitchCoordinator.RememberCatchUp` (`:177-180`) ist die trickreichste Zeile der Klasse und ungetestet.
Empfehlung: `tests/RigShift.App.Tests` (net10.0-windows, Klassenlogik ohne UI-Automation); konkrete Tests im Plan.

### L-02 Migrations-Fixtures
`JsonSettingsStoreTests.cs:73-84` prüft `IsEnabled`, nicht `ExitDelaySeconds` (einziger `set`-Initialisierer ohne
Test). Alle Migrationstests erzeugen „alte“ Dateien aus dem heutigen Serializer; eine echte 1.0-/1.2-Datei liegt
nicht vor – ein umbenannter Schlüssel bliebe unbemerkt. Empfehlung: `Fixtures/settings-1.0.json`, `profile-1.0.json`,
`profile-1.2.json` (aus `git show v1.0.0`/`v1.2.0`, anonymisiert) mit Tests, die jedes Feld asserten.

### L-03 Fake-Lücken
`FakeDisplayConfigurator`: keine Ausnahme aus `ApplyAsync`/`QueryAsync` (B-07), `HdrResult` ein Wert für alle
Displays, `ListRefreshRatesAsync` immer leer. Empfehlung: Exception-Queue, HDR-Ergebnis pro Display, Raten-Liste;
Tests `Switch_ApplyThrows_RestoresPreviousTopology`, `Switch_HdrFailsOnOneDisplay_OthersStillSet`.

### M-01 CI ohne Publish
`ci.yml` baut und testet; `dotnet publish` mit Single-File/R2R/self-contained läuft nur in `release.yml` beim Tag.
R2R- oder Trim-Fehler brechen erst das Release. Empfehlung: Publish-Schritt (ohne vpk) in `ci.yml`, ~1–2 min.

### N-02 `%LocalAppData%`
`docs/PLAN.md` 4.2 (Z. 112) und 4.7 (Z. 201), `AppSettings.cs:8`, `AppServices.cs:13`, `IProfileStore.cs:5` nennen
`%LocalAppData%`; Code nutzt `ApplicationData` (`App.xaml.cs:39`, `JsonProfileStore.cs:31`). Der Plan ist laut
CLAUDE.md verbindlich, und Velopack leert `%LocalAppData%\RigShift` – ein falscher Pfad dort ist gefährlich.

### N-03 ARCHITECTURE.md
Zeile 49 nicht existierender Listener (A-01); Zeile 8 ordnet „device notifications, JSON profile store“ der
Windows-Schicht zu (Store liegt in Core, Watcher in App); Zeile 31 „≤ 60 s“ (kein Limit seit M5,
`SwitchCoordinator.cs:130-132`); Zeile 58 Exit-Codes ohne 5; Zeile 10 ohne `tests/RigShift.Windows.Tests`; Zeile 17
Profile ohne Hotkey/Apps/KeepAwake/Ducking/AppsWait, Display ohne CustomName/Hdr; Core enthält außerdem `Cli/`,
`Ipc/`, `Updates/` (A-09). Empfehlung: Abschnitte „Projects“, „Core concepts“, „Interfaces“ neu schreiben.

### N-04 ROADMAP
Zeile 120 „Dry-run mode in the UI“ als v2 – existiert („Prüfen“, `ProfilesPage.xaml:97`); Zeile 3 definiert 🟡
„built, hardware test pending“, alle 1.3-Punkte (Z. 27-35) sind 🟢, obwohl CLAUDE.md und PLAN 6 „ohne Hardwaretest“
sagen. Empfehlung: 🟡 für Fensterrettung, Apps-warten, Ducking, Stromsparwarnung, Wach halten, HDR/Hz; Punkt streichen.

## Geprüft und in Ordnung (Auswahl, mit Prüfweg)

- Regeln 1–5 und 8 aus `display-topology.md`: ein `SetDisplayConfig` mit den vorgeschriebenen Flags
  (`CcdDisplayConfigurator.cs:148-156`), Handles nur im Snapshot, 31 und 1610 transient, Warten auf verschwundene
  Pflicht-Displays, Fallback ohne Modi, CatchUp unabhängig vom aktiven Profil.
- Rollback-Reihenfolge: Keep-awake → Ducking → Topologie + HDR + Rettung → Audio-Defaults + Lautstärke; Apps nie vor
  Bestätigung (Tests). Lautstärke und Keep-awake überleben einen Absturz korrekt (Profilwert bzw. Handle-gebunden).
- Nebenläufigkeit: `SemaphoreSlim(1,1)` mit `WaitAsync(0)`, alle Aufrufer auf dem UI-Thread (Pipe per
  `Dispatcher.InvokeAsync`, Hotkey WndProc, Automatik `DispatcherTimer`, Tray Click, Link → zweiter Prozess → Pipe).
- Interop: `cbSize`/`header.size` überall, Zwei-Phasen-Abfragen mit Retry, Rückgabewerte geprüft und geloggt,
  `[ComImport]`-RCWs, `SafeHandle`, `fixed` korrekt, `CoTaskMemFree`/`PropVariantClear`/`LocalFree`.
- Pipe: `CurrentUserOnly` beidseitig, 4-Byte-Framing ≤ 1 MB, `ReadExactlyAsync`; URI: Scheme/Host geprüft, kein
  Query/Fragment, Name nur zur Suche; Profildateien heißen `<guid>.json`; Registry nur HKCU, HKLM nur lesend.
- Persistenz: atomares Schreiben (`.tmp` + Move), fehlendes Verzeichnis angelegt, defekte Dateien → Defaults bzw.
  übersprungen, unbekannte Felder ignoriert, alle seit 1.0 neuen Properties mit korrektem Default bei fehlendem
  Schlüssel (Tabelle im Agentenbericht, Tests vorhanden außer `ExitDelaySeconds`).
- Logging: keine Interpolation in Log-Aufrufen, jeder Wechselschritt und jede Automatik-Aktion geloggt (nur die
  Entscheidungen fehlen), Leerlauf stumm, `ProcessId`-Enricher, `shared: true`.
- Lokalisierung: 234/234 Schlüssel, Platzhalter identisch, Formatierung überall mit `Loc.Instance.Culture`, dynamische
  Präfixe (`Outcome_`, `Warning_`, `Problem_`, `Icon_`) vollständig gegen die Enums.
- UI: alle Bindungspfade lösen auf, ObservableCollections nur auf dem UI-Thread, Seiten/VMs Singletons, leere Zustände
  überall vorhanden (Screenshots Erststart), Countdown en/de hell/dunkel korrekt, Tray-Popup mit aktivem Profil,
  Erkennen-Fenster; im Dunkel-Theme kein zu dunkler Text sichtbar (Stolperfalle `ui:TextBlock` tritt nicht auf).
- Build/CI: Warnungen als Fehler in allen sechs Projekten, Versionen exakt gepinnt, CI = Release-Checks,
  `Deterministic`/`ContinuousIntegrationBuild`, vpk-Optionen vollständig, `%LocalAppData%` nirgends im Code.
- 25 README-/CHANGELOG-Behauptungen gegen den Code bestätigt (CLI, Exit-Codes, Pipe, Logs, Update-Takt, „nicht doppelt
  starten“, 10-s-Frist, Polling 2 s/1 s, Rettung 1 s, Ducking-Werte, Run-Key, Power-Request, Hotkey erneut =
  bestätigen, URI nur installiert, Windows 10 2004+, Release-Assets, FUNDING).
