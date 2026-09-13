# RigShift – Entwicklungsplan

Stand: 2026-09-13 · Sprache dieses Dokuments: Deutsch (Repo, Code und Community-Doku sind Englisch)

Dieses Dokument ist die verbindliche Grundlage für die Entwicklung. Es ersetzt das ursprüngliche Briefing
(`AUFTRAG_Entwicklung.md`, nicht im Repo). Alle Entscheidungen hier sind mit dem Nutzer abgestimmt
(Fragerunden vom 2026-09-13) und tragen ihre Begründung.

---

## 1. Ziel und Nicht-Ziele

**Ziel:** Eine kleine, moderne Windows-Tray-App, die einen Gaming-PC zuverlässig zwischen Nutzungsprofilen
umschaltet – im ersten Anwendungsfall zwischen *Schreibtisch* (drei Monitore, Lautsprecher) und *Sim Rig*
(Ultrawide + spacedesk-Tablet, Kopfhörer). Sie löst das funktionierende PowerShell-Skript
(`legacy/DisplayProfile.ps1`) ab und ist von Anfang an so gebaut, dass sie als freies Community-Tool für
Sim-Racer taugt.

**Was RigShift anders macht als DisplayFusion, DisplayMagician, MultiMonitorTool, DisplayProfileManager:**

1. **Atomarer Topologiewechsel** in einem einzigen `SetDisplayConfig`-Aufruf. Monitore einzeln zu schalten
   scheitert am NVIDIA-Display-Head-Limit – das ist der Kern des Problems und der Grund, warum die bestehenden
   Tools versagen.
2. **Robuste Identität** der Bildschirme über Gerätepfade und EDID, nicht über flüchtige Adapter-LUIDs/Target-IDs.
3. **Warten statt scheitern:** Ein schlafender HDMI-Monitor (Fehler 31) oder ein noch nicht verbundener
   spacedesk-Viewer führt zu Wiederholung bzw. späterem Nachziehen, nicht zum Abbruch.
4. **Sicherheitsnetz:** Automatische Rückkehr zum vorherigen Zustand, wenn der Nutzer den Wechsel nicht
   bestätigt (kein Bild = kein Klick = Rollback).
5. **Verständliche Vorabprüfung** statt kryptischer Fehlercodes: Head-Budget, fehlende Bildschirme.
6. **Audio inklusive**, Wiedergabe und Kommunikation getrennt.

**Nicht-Ziele (v1):** GPU-Herstellertools ersetzen (NVIDIA Surround, Farbprofile), Fenster verschieben,
Maus-Routing, VR-Runtime-Verwaltung, Wheelbase-Profile.

---

## 2. Getroffene Entscheidungen

| Thema | Entscheidung | Begründung |
|---|---|---|
| **Name** | **RigShift** | Schaltwechsel + Rig-Wechsel, kurz, einprägsam, GitHub-Namensraum in der Sim-Szene frei. Repo `ManuelStaggl/RigShift`, Namespace `RigShift.*`, URI-Schema `rigshift://`. |
| **Umfang v1** | Klein & stabil | Ersetzt das Skript vollständig, sonst nichts. Erst am echten PC stabil, dann erweitern. |
| **Verteilung** | Velopack + GitHub Releases | Installer, Auto-Update und portable EXE aus einer Toolchain; kein Store-Konto; winget später über das Release-Asset. |
| **Lizenz** | MIT | Verbreitetste OSS-Lizenz, niedrigste Hürde für Community-Beiträge. |
| **UI-Stack** | WPF + WPF-UI 4.3 (Entscheidung von Claude, Nutzer hat den Stack freigegeben) | Siehe Abschnitt 3. |
| **Sprache** | Repo/Code/README Englisch, Plan Deutsch, App de+en | Community-Reichweite ohne Umgewöhnung für den Nutzer. |
| **Repo-Wurzel** | Projektordner `Sim Rig Umschalter` ist die Git-Wurzel | Sitzungen starten im Projektordner (Auto-Memory pro Repo). `Bestehend/` und das Briefing sind gitignoriert (private Gerätepfade, IDs). |

---

## 3. Software-Stack

Alle Versionen am 2026-09-13 live gegen nuget.org verifiziert und in `Directory.Packages.props` gepinnt
(Central Package Management, keine floating Versionen).

| Baustein | Wahl | Version | Warum |
|---|---|---|---|
| Runtime | .NET 10 (LTS bis 2028-11) | SDK 10.0.401 | Aktuelles LTS. .NET 11 (STS) kommt 2026-11-10 – für ein Tool, das jahrelang laufen soll, ist LTS richtig. |
| UI | WPF + **WPF-UI** | 4.3.0 | Fluent/Windows-11-Look nativ in WPF (Mica, Rundungen, Snackbar, NavigationView). Gegenüber WinUI 3: keine Windows-App-SDK-Abhängigkeit, kleineres Single-File-Publish, ausgereiftes Tray-Ökosystem, stabileres Fensterverhalten beim Monitorwechsel – genau die Situation, in der RigShift arbeitet. Gegenüber Avalonia: nur Windows nötig, Win32-Interop direkter. |
| Tray | **H.NotifyIcon.Wpf** | 2.4.1 | Reifste Tray-Bibliothek (Nachfolger von Hardcodet), zuverlässige Kontextmenüs und Balloon-Toasts; das NotifyIcon von WPF-UI ist noch unausgereift. |
| MVVM | **CommunityToolkit.Mvvm** | 8.4.2 | Source-Generatoren für `ObservableProperty`/`RelayCommand`, Messenger, kein Boilerplate. |
| DI/Hosting | Microsoft.Extensions.Hosting | 10.0.12 | Generic Host, `IHostedService` für Tray, Pipe-Server und Trigger. |
| Logging | Serilog + File + Debug Sinks | 4.4.0 / 7.0.0 / 3.0.0 | Strukturierte Logs (Vorgabe `dotnet-standards`), tägliche Rotation in `%LocalAppData%\RigShift\logs`. |
| Win32 | **Microsoft.Windows.CsWin32** | 0.3.333 | Source-generierte P/Invokes und Structs aus den offiziellen Metadaten – keine handgeschriebenen Struct-Layouts mehr (die `MODE`-Struktur im Skript zeigt, was man sich damit erspart). |
| CLI | System.CommandLine | 2.0.12 | `RigShift.exe apply Rig` mit sauberem Parsing und Hilfe. |
| Verteilung | **Velopack** | 1.2.0 | Installer + Delta-Updates + portable EXE, GitHub-Releases als Update-Feed. |
| Tests | xunit.v3 + Shouldly + NSubstitute | 4.0.1 / 4.3.0 / 6.2.0 | Microsoft.Testing.Platform (`global.json` → `test.runner`), Aufruf `dotnet test --solution RigShift.slnx`. |

Weitere Festlegungen: `TreatWarningsAsErrors`, `AnalysisLevel latest-recommended`, `Nullable enable`,
Per-Monitor-V2-DPI-Awareness (Pflicht bei einem Monitor-Tool), `asInvoker` (keine Adminrechte nötig:
CCD-API und `IPolicyConfig` laufen als normaler Nutzer).

**Bewusst nicht gewählt:** NAudio/AudioSwitcher.AudioApi (unnötig, Core Audio über CsWin32 + eigenes
`IPolicyConfig`), MSIX (Signaturpflicht, Sandbox erschwert Autostart/COM), Native AOT (WPF unterstützt es nicht).

---

## 4. Architektur

### 4.1 Projekte und Abhängigkeitsrichtung

```
RigShift.App  ──►  RigShift.Windows  ──►  RigShift.Core
 (WPF, Tray,        (CCD, Core Audio,       (Profile, Planner,
  DI, CLI)           IPolicyConfig,          Orchestrator,
                     Geräteereignisse)       Import – kein Win32)
                                                   ▲
                                      RigShift.Core.Tests
```

- **Core** kennt kein Windows. Alles, was Logik ist (Matching, Planung, Wiederholungsstrategie,
  Rollback-Entscheidung, Head-Budget-Heuristik, Import-Parsing), liegt hier und ist ohne Monitore testbar.
- **Windows** implementiert die Core-Schnittstellen `IDisplayConfigurator`, `IAudioController`,
  `IProfileStore` sowie (ab M2/M4) `IDeviceEvents` und `IAutostart`. Referenz für die Algorithmen ist
  `legacy/DisplayProfile.ps1`; die harten Regeln stehen in `docs/display-topology.md`.
- **App** enthält nur UI, Komposition (DI) und Prozess-Belange (Einzelinstanz, CLI, Updates).

### 4.2 Datenmodell

Profile sind reine Daten ohne flüchtige Bezeichner (`src/RigShift.Core/Profiles/`):

- `Profile` – Name, Icon, Liste `DisplayAssignment`, `AudioAssignment`, `ConfirmTimeoutSeconds`.
- `DisplayIdentity` – `AdapterDevicePath`, `TargetDevicePath` (Primärschlüssel), EDID-Hersteller/-Produkt
  (Fallback, wenn Port/Kabel gewechselt wurde), `FriendlyName` (nur Anzeige).
- `DisplayAssignment` – Auflösung, Bildrate als Bruch, Position, Rotation, `IsPrimary`, **`IsOptional`**
  (spacedesk: fehlt → überspringen, später nachziehen).
- `AudioAssignment` – Wiedergabe, Wiedergabe-Kommunikation, Aufnahme, Aufnahme-Kommunikation, Lautstärke.

Ablage: `%LocalAppData%\RigShift\profiles\<guid>.json` und `settings.json`, jeweils mit `schemaVersion`.
JSON über `System.Text.Json` mit Source-Generator-Kontext.

### 4.3 Der Wechsel als Zustandsautomat

```
Idle ─► Planning ─► Applying ─► AudioSwitch ─► Confirming ─► Applied
           │           │                           │
           ▼           ▼                           ▼ (Timeout)
        Blocked      Failed                    RollingBack ─► RolledBack
                                                   │
                       (optionale Bildschirme fehlen)
                                                   ▼
                                               FollowUp (wartet auf WM_DISPLAYCHANGE, max. 60 s, plant erneut)
```

**Planning** (`TopologyPlanner`, Core, voll getestet):

1. Snapshot aller Pfade (aktiv + inaktiv, `QDC_ALL_PATHS`) holen.
2. Jede `DisplayAssignment` gegen die Snapshot-Displays matchen: erst `TargetDevicePath` + `AdapterDevicePath`,
   dann EDID-Fallback (→ Warnung `MatchedByEdidFallback`).
3. Nicht gefundene Displays: `NotAttached` oder `AttachedButUnavailable` (Target vorhanden, `targetAvailable = 0`,
   typisch: HDMI-Monitor im Standby).
4. Pflicht-Display fehlt → `IsBlocked`. Nur optionale fehlen → `ShouldRetryLater`.
5. Head-Budget-Heuristik pro Adapter: Pixelrate = Breite × Höhe × Bildrate. Über ca. 1,0 Gpx/s (4K@165 = 1,37;
   5120×1440@240 = 1,77) zählt ein Display **zwei** Heads, sonst einen. Budget je Adapter konfigurierbar
   (NVIDIA-Standard 4). Überschreitung ist eine **Warnung mit Erklärung**, keine Sperre – AMD/Intel-Limits
   sind anders, und die Heuristik darf nie einen funktionierenden Wechsel verhindern.

**Applying** (`SwitchOrchestrator`, Core, mit Fake-Configurator getestet):

1. Vorher-Snapshot (aktive Pfade + Modi) für den Rollback sichern.
2. Versuch 1: `SetDisplayConfig` mit gespeicherten Modi. Flags `SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG |
   SDC_SAVE_TO_DATABASE | SDC_ALLOW_CHANGES`.
3. Bei Fehler: derselbe Pfadsatz **ohne Modi** (OS wählt aus seiner Datenbank) – hat im Log-Fall vom
   2026-09-13 den zweiten Versuch gerettet.
4. Bei `ERROR_GEN_FAILURE` (31) oder `AttachedButUnavailable`: **auf das Target warten** – Snapshot
   im 1-s-Takt neu abfragen, bis `targetAvailable` gesetzt ist oder das Zeitbudget (Standard 20 s) verbraucht
   ist; dann erneut anwenden. Kein fester 3-s-Schlaf mehr.
5. Zeit- und Versuchsbudget konfigurierbar; jeder Versuch mit Fehlercode geloggt.

**AudioSwitch:** Endpoint-Zustand prüfen (nur `DEVICE_STATE_ACTIVE`), sonst überspringen und melden. Fehler im
Audio-Teil brechen den Wechsel nicht ab (Anzeige und Audio getrennt bewertet, wie im Skript).

**Confirming:** Auf der neuen Hauptanzeige ein Fenster mit Countdown („Anzeige behalten?", Tastaturfokus,
Screenreader-Text). Bestätigung nur über Klick/Enter/Hotkey – **Mausbewegung zählt nicht**, denn sie beweist
nicht, dass ein Bild da ist. `ConfirmTimeoutSeconds = 0` schaltet das Netz für vertrauenswürdige Profile ab;
CLI-Aufrufe dürfen es per `--no-confirm` abschalten.

**RollingBack:** Vorher-Snapshot mit derselben Wiederholungslogik anwenden. Scheitert auch das, bleibt das
Ergebnis `Failed` mit klarer Meldung und Log-Verweis.

### 4.4 Prozessmodell

- **Einzelinstanz** über benannten Mutex; eine zweite Instanz übergibt ihre Argumente per Named Pipe
  `\\.\pipe\RigShift` an die laufende und beendet sich mit deren Exit-Code.
- **CLI** (v1): `RigShift.exe apply <name> [--no-confirm] [--dry-run]`, `list`, `save <name>`, `status`.
  Exit-Codes: 0 Applied, 1 Failed, 2 Blocked, 3 RolledBack, 4 Profil unbekannt, 5 ungültige Argumente
  (ergänzt in M4; „anderer Wechsel läuft" = 1). Läuft keine Instanz, startet die App minimiert und führt den
  Befehl aus.
- **Tray:** Linksklick → Popup mit Profilen (aktives markiert), Rechtsklick → Kontextmenü (Öffnen, Aktuelle
  Anordnung als Profil speichern, Einstellungen, Beenden). Ergebnis als Balloon-Toast und Snackbar.
- **Autostart:** HKCU `Run` mit `--minimized`; Standardprofil beim Start optional anwenden.
- **Aktives Profil erkennen:** Nach jedem `WM_DISPLAYCHANGE` Snapshot gegen alle Profile matchen
  (gleiche Displays, Positionen, Primär) → Tray-Icon und Häkchen aktualisieren, auch wenn der Nutzer in den
  Windows-Einstellungen umgeschaltet hat.

### 4.5 Import der bestehenden Profile

> **Entscheidung 2026-09-13 (M3, Nutzer):** Kein Import in der Oberfläche. Die Skriptdateien hat nur der Autor;
> Community-Nutzer hatten sie nie. Die Profile des Autors wurden einmalig mit
> `RigShift.Probe convert <skriptordner> <zielordner>` in RigShift-JSON umgewandelt und liegen lokal als Vorlage
> (`Bestehend/RigShiftProfiles/`, gitignoriert). Der Importcode aus M2 bleibt nur als Werkzeug im Probe-Programm.
> Der folgende Absatz beschreibt den ursprünglichen Plan.

Beim ersten Start (oder über Einstellungen): Ordner wählen (Vorschlag `C:\Tools\DisplayProfiles`), `.display`
parsen (`LegacyDisplayFile`, Core, getestet), Structs in der Windows-Schicht dekodieren
(`DISPLAYCONFIG_PATH_INFO`/`MODE_INFO` aus CsWin32 → `DisplayAssignment`), Audio aus `DisplayProfiles.json`
übernehmen. Ergebnis: neue JSON-Profile; die Originale bleiben unangetastet (Rückfallebene bleibt erhalten,
bis der Nutzer die Verknüpfungen selbst entfernt).

### 4.6 Lokalisierung und Barrierefreiheit

- `Resources/Strings.resx` (en) + `Strings.de.resx`; Sprache automatisch, in den Einstellungen überschreibbar.
- Alle interaktiven Elemente mit `AutomationProperties.Name`, vollständige Tastaturbedienung, Fokusreihenfolge,
  Hoher-Kontrast-Modus über WPF-UI-Themes, Countdown-Dialog als Live-Region.

### 4.7 Logging und Diagnose

- Serilog, Tagesrotation, 14 Dateien, `%LocalAppData%\RigShift\logs\rigshift-<datum>.log`.
- Jeder Wechsel: Profil, Plan (gefunden/fehlend/Warnungen), Versuche mit Fehlercode, Dauer, Ergebnis.
- Diagnoseseite (v1, einfach): angeschlossene Bildschirme mit Pfad, EDID, Verfügbarkeit, aktiver Modus;
  Audiogeräte; letzte zehn Wechsel.

---

## 5. Meilensteine v1

Jeder Meilenstein endet mit grünem `dotnet build` + `dotnet test` und einem Commit. Am echten PC wird erst ab
M5 getestet – vorher gibt es nichts, das Monitore anfasst.

| # | Inhalt | Akzeptanz |
|---|---|---|
| **M0** | Skelett (dieser Stand): Solution, Projekte, Modell, Schnittstellen, Legacy-Parser, CI | Build + Tests grün, Repo öffentlich |
| **M1** | `TopologyPlanner` + `SwitchOrchestrator` in Core mit Fake-Configurator; Head-Budget; Wiederholungs- und Wartelogik; Rollback-Entscheidung | Tests für: Match per Pfad, Match per EDID, optional fehlt, Pflicht fehlt, Fehler 31 → warten → Erfolg, Timeout → Rollback, Head-Budget-Warnung |
| **M2** | Windows-Schicht: `CcdDisplayConfigurator` (Query/Apply/Snapshot, Struct-Dekodierung), `PolicyConfigAudioController`, `JsonProfileStore`, Legacy-Import inkl. Struct-Dekodierung | Manuelle Prüfung auf dem Entwicklungsrechner: Snapshot zeigt echte Displays; Import erzeugt aus den Beispieldateien plausible Profile (Dateien lokal, nicht im Repo) |
| **M3** | App: Tray-Shell, Profilliste, Wechsel mit Countdown-Dialog, Toast, Einstellungen (Standardprofil, Autostart, Timeout, Sprache), Diagnoseseite | Bedienbar ohne Maus; de/en umschaltbar |
| **M4** | CLI + Einzelinstanz + Pipe; Autostart; „Aktuelle Anordnung als Profil speichern"; Profil bearbeiten (Bildschirme, Primär, Audio aus Liste) | `RigShift.exe apply Rig` aus Stream-Deck-Aktion oder Verknüpfung funktioniert |
| **M5** | **Test am Gaming-PC:** Desk ↔ Rig, Rig mit schlafendem G9 (Fehler-31-Fall), Rig ohne spacedesk + Nachziehen, Rollback bei Nicht-Bestätigung | Alle vier Fälle protokolliert; Skript bleibt parallel installiert |
| **M6** | Release 1.0: Velopack-Paket, GitHub-Release-Workflow, README mit Screenshots, CHANGELOG | Installation + Auto-Update von 1.0.0 auf 1.0.1 nachgewiesen |

---

## 6. Roadmap nach v1 (vom Nutzer gewählt, priorisiert)

**v1.1 – Auslösen und Steuern**

1. Globale Tastenkürzel pro Profil (`RegisterHotKey`).
2. URI-Schema `rigshift://apply/<name>` (HKCU-Registrierung durch Velopack-Hook).
3. Mikrofon pro Profil + getrennte Kommunikationsrolle (gleiche API wie Wiedergabe, fast gratis).
4. Lautstärke pro Profil (`IAudioEndpointVolume`).
5. Apps pro Profil starten/beenden (Reihenfolge, Wartezeit, „nur wenn nicht läuft").
6. Prozess-Trigger (WMI `Win32_ProcessStartTrace` oder ETW; Fallback Polling 2 s) + **Spiele-Vorlagen**
   (LMU, iRacing, ACC, AC EVO, rFactor 2, AMS2, F1 – als JSON in `templates/`, per PR erweiterbar).

**v1.2 – Automatik und Komfort**

7. USB-Gerät verbunden (`RegisterDeviceNotification`, Wheelbase/Dongle per VID/PID oder Name).
8. Rennmodus: Fokus-Assistent an, Spielmodus, Standby/Bildschirmschoner aus; alles beim Zurückwechseln zurück.
9. Energieplan pro Profil (`powercfg /setactive`).
10. HDR je Bildschirm (CCD `DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE`), Bildwiederholrate; Nachtlicht nur
    über undokumentierte Registry → als „experimentell" markieren.
11. Lokale HTTP-API (`http://127.0.0.1:<port>/api/profiles`, Token in settings.json) für Skripte und SimHub.
12. Home Assistant: MQTT-Discovery (aktives Profil als Sensor, Wechsel als Select-Entität) auf Basis der API.

**v2 – Community**

13. Einrichtungsassistent (erstes Profil aus aktuellem Zustand, zweites nach Umstecken).
14. Diagnose-Export ohne persönliche Daten (Pfade gehasht, Namen behalten) für Issues.
15. Code-Signatur (SignPath.io ist für OSS kostenlos; alternativ Azure Trusted Signing), winget-Manifest.
16. AMD/Intel-Validierung durch Community-Tester; Head-Budget-Voreinstellungen je GPU-Familie.
17. Trockenlauf-Modus in der UI (die CLI hat `--dry-run` schon in v1).

---

## 7. Teststrategie

- **Core:** vollständig ohne Windows. Fake `IDisplayConfigurator`, der Snapshots aus Testdaten liefert und
  Fehlercodes je Versuch skripten kann (z. B. `[31, 31, 0]`). Fake-Uhr für Timeouts.
- **Windows:** kleine manuelle Prüfprogramme (`RigShift.exe status` bzw. Diagnoseseite) statt automatisierter
  Tests, weil echte Monitore gebraucht werden. Snapshot-Ausgabe als anonymisierte JSON-Fixture für Core-Tests
  wiederverwenden.
- **App:** ViewModels über Core-Fakes testbar; UI-Automationstests bewusst keine.
- **Echter PC (M5):** vier Pflichtszenarien, siehe Meilensteine. Vor jedem Test: Skript-Verknüpfungen als
  Rückfallebene griffbereit.

---

## 8. Verteilung und Release

- `dotnet publish -c Release -r win-x64` (self-contained, Single-File, ReadyToRun) → `vpk pack` → Velopack
  erzeugt Setup-EXE, portable ZIP und Delta-Pakete → GitHub Release `vX.Y.Z`.
- Workflow `.github/workflows/release.yml` läuft bei Tag `v*`; `ci.yml` bei jedem Push/PR (Build + Tests).
- Versionsquelle: `Directory.Build.props` → `<Version>`; der Tag muss übereinstimmen (Workflow prüft).
- Auto-Update: App prüft beim Start und alle 24 h gegen GitHub Releases, installiert beim nächsten Neustart.
- v1 ohne Signatur (SmartScreen-Hinweis im README erklärt); Signatur in v2.

---

## 9. Community-Readiness

- Keine hartkodierten Geräte, Namen oder IDs – alles aus Profilen und Erkennung.
- Private Daten bleiben draußen: `Bestehend/`, Briefing und Handoff sind gitignoriert; Logs enthalten
  Gerätepfade nur lokal; Diagnose-Export (v2) anonymisiert.
- Repo-Hygiene ab M0: MIT-Lizenz, README, CONTRIBUTING, Issue-Vorlagen (Bug mit Log-Anhang, Feature),
  PR-Vorlage, CI-Badge, CHANGELOG (Keep a Changelog), Conventional Commits.
- Verwandte Projekte im README nennen und abgrenzen (DisplayProfileManager, MonitorSwitcher, DisplayMagician).

---

## 10. Risiken und offene Punkte

| Risiko | Umgang |
|---|---|
| CsWin32 generiert `DISPLAYCONFIG_MODE_INFO` als Union – das Marshalling der Legacy-Bytes muss exakt 64 Byte treffen | In M2 mit den lokalen Beispieldateien verifizieren; Größe per `Marshal.SizeOf` im Test prüfen |
| `IPolicyConfig` ist undokumentiert und kann sich mit Windows-Versionen ändern | GUID-Kaskade (Win10/Win11-Varianten) implementieren, Fehler nicht fatal |
| Countdown-Dialog erscheint auf einem Bildschirm ohne Bild | Dialog auf der **neuen Hauptanzeige** und zusätzlich Hotkey `Esc` = Rollback von jedem Bildschirm aus |
| Head-Budget-Heuristik liefert Fehlalarm bei AMD/Intel | Nur Warnung, Budget je Adapter einstellbar, „nicht mehr anzeigen" |
| Prozess-Trigger per WMI braucht ggf. Adminrechte | In v1.1 evaluieren; Fallback Polling |
| `CcdDisplayConfigurator` übergibt pro Bildschirm nur den Source-Modus (Auflösung, Position) und die Bildrate im Pfad, **kein Target-Timing** – das Skript hat gespeicherte Target-Modi mitgegeben | Entscheidung M2: Das Profilmodell speichert kein Timing, Windows wählt es per `SDC_ALLOW_CHANGES` (dokumentiertes Verhalten). In M5 prüfen; scheitert es, Target-Timing ins Profil aufnehmen |
| Bildschirm im Standby liefert evtl. keinen `monitorDevicePath` → Planner meldet `NotAttached` statt `AttachedButUnavailable` und wartet nicht | In M5 mit schlafendem G9 prüfen (`RigShift.Probe snapshot`) |
| Velopack installiert nach `%LocalAppData%\RigShift` – derselbe Ordner wie Profile/Einstellungen; eine Deinstallation könnte die Profile löschen | In M6 prüfen; notfalls Daten nach `%AppData%\RigShift` verlegen (mit Migration) |
| spacedesk-Display nach Verbindung an falscher Position | FollowUp-Phase plant neu und wendet den vollständigen Pfadsatz erneut an |

Offen (klärt die Entwicklungssession, wenn es ansteht): Icon-Design (vorerst `desk.ico`/`rig.ico` aus dem
Skript), Port der HTTP-API.

Entschieden in M3 (Nutzer, 2026-09-13): **Tray-Balloon** als Ergebnismeldung (keine App-Kennung/Verknüpfung
nötig, geht auch portabel); **Tray-Popup + Hauptfenster** mit den Seiten Profile, Diagnose, Einstellungen;
**Farbschema folgt Windows** (inkl. hohem Kontrast). Bestätigungszeit ist eine App-Einstellung; ein Profil kann
sie mit eigenem Wert überschreiben (`Profile.ConfirmTimeoutSeconds` ist dafür nullable).

Entschieden in M4 (Nutzer, 2026-09-13):
- **Profil bearbeiten = Eigenschaften + Übernahme:** Name, Icon, Bestätigungszeit; je Bildschirm optional, primär,
  entfernen; Audio aus Auswahlliste. Auflösung und Position stellt man in Windows ein und übernimmt sie mit
  „Aktuelle Anordnung übernehmen" – kein Zahleneditor (Fehlerquelle), kein visueller Anordner (v1.x).
- **„Aktuelle Anordnung speichern" übernimmt die aktuellen Standard-Audiogeräte** (im Dialog änderbar).
- **CLI:** `apply`/`save` starten die Tray-App minimiert, falls sie nicht läuft, und reden dann per Pipe mit ihr
  (der CLI-Prozess ist immer der Client, damit der Exit-Code stimmt); `list`/`status` antworten ohne App-Start.
  CLI-Ausgabe englisch (maschinennah, stabil für Skripte).
- **Profilverwaltung:** Löschen (mit Rückfrage), Duplizieren, Desktop-Verknüpfung `RigShift.exe apply "<Name>"`.

---

## 11. So startet die Entwicklungssession

1. Sitzung **im Projektordner** starten (Repo-Wurzel); `.claude/handoff.md` wird automatisch eingelesen.
2. `dotnet build RigShift.slnx` und `dotnet test --solution RigShift.slnx` müssen grün sein.
3. Mit **M1** beginnen (reine Core-Logik, testgetrieben). Referenzen: `legacy/DisplayProfile.ps1`,
   `docs/display-topology.md`, `docs/ARCHITECTURE.md`.
4. Nach jedem Meilenstein: Commit im Conventional-Commits-Format, `CHANGELOG.md` ergänzen, Push.
5. Die lokalen Beispieldateien (`Bestehend/DisplayProfiles/*.display`, `.json`, `.log`) sind für M2 die
   Testdaten – **nie ins Repo**.
