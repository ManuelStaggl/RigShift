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
| **Lizenz Markendateien** | Logo, Symbol und App-Icons vom MIT ausgenommen (alle Rechte vorbehalten, unveränderte Nutzung zum Verweis erlaubt); Regeln in `docs/brand/README.md` (Nutzer, 2026-09-13) | Forks dürfen den Code frei nutzen, aber nicht wie die offizielle App mit Installer/Auto-Update aussehen – Schutz vor Verwechslung und untergeschobenen Kopien. Rechte liegen beim Nutzer. Fluent-Profilsymbole bleiben MIT (Microsoft). |
| **Nachziehen (FollowUp)** | Ohne Zeitlimit, solange kein anderer Wechsel läuft; greift auch, wenn Windows zwischendurch ein anderes Profil geladen hat (M5) | Ein spacedesk-Viewer verbindet sich oft erst Minuten später; Windows lädt beim Verbinden selbst gespeicherte Anordnungen, das erkannte Profil taugt nicht als Absicht. |
| **Bestätigung für Desk** | Nutzerprofil Desk mit eigener Bestätigungszeit 0 (Nutzer, M5) | Vom Rig aus ist der Dialog auf dem Schreibtisch nicht erreichbar; Desk ist die bewährte Anordnung. Keine Code-Änderung, Rig behält das Netz. |
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
                                               FollowUp (wartet auf WM_DISPLAYCHANGE, solange das Profil aktiv ist, plant erneut)
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

### 4.8 Markenauftritt (M4.5)

Quelle: `RigShift-Brand-Package/` (lokal, gitignoriert, 12 MB; Einstieg `06-guides/CLAUDE-HANDOFF.md`). Markenregeln
dort sind verbindlich (Schreibweise „RigShift", Logo nie verändern, aktiver Zustand mit Häkchen + Text, nicht nur
Farbe; keine Fontdateien mitliefern). Entscheidungen des Nutzers (2026-09-13) in Abschnitt 10.

1. **Ablage.** Nur Genutztes ins Repo: `src/RigShift.App/Assets/Brand/` bekommt `rigshift-app-light.ico`,
   `rigshift-tray-light.ico`/`-dark.ico`, die ResourceDictionaries Shared/Light/Dark und das Symbol als PNG (hell/dunkel) für
   den Popup-Kopf. `docs/brand/` bekommt das horizontale Logo mit Slogan (SVG, hell/dunkel) und das App-Icon als
   PNG für README und GitHub. Prüfhilfe nur im Debug-Build: `RigShift.exe --preview-branding <ordner>
   [--preview-theme light|dark]` schreibt Tray-Icon-Bögen und zeigt das Popup in einem Fenster.
   `Assets/desk.ico`/`rig.ico` entfallen.
2. **App-Icon.** `ApplicationIcon` und das Icon aller Fenster (Hauptfenster, Editor, Countdown) = `rigshift-app-light.ico`.
3. **Ressourcen.** `RigShift.Shared` + genau eines von `RigShift.Light`/`RigShift.Dark` in `App.xaml` mergen
   (`BrandTheme`); der vorhandene Theme-Wechsel (Windows-Theme-Watcher) tauscht das Brand-Dictionary mit aus.
   Bei hohem Kontrast bleiben WPF-UI-/Systemfarben maßgeblich. WPF-UI-Akzent = Markenblau (`primary` #0067B8
   hell, #79B8FF dunkel) statt Windows-Akzent. Keine WPF-UI-Schlüssel überschreiben.
4. **Profilsymbole.** Bekannte Icon-Schlüssel `desk`, `rig`, `vr`, `tv`, `stream` (Core, getestet; Alt-Profile nutzen
   schon `desk`/`rig`). Darstellung mit **Fluent System Icons aus WPF-UI** statt der Paket-Geometrien (Nutzer:
   gefielen nicht): `Desktop`, `TopSpeed`, `HeadsetVr`, `Tv`, `Live`; Umriss, beim aktiven Profil gefüllt – im
   Editor als Auswahl mit Vorschau, in der Profilliste und im Popup vor dem Namen. Ohne bzw. mit unbekanntem
   Schlüssel: im Tray das RigShift-Symbol, in Listen kein Symbol.
5. **Tray-Icon.** Zeigt das Profilsymbol des aktiven Profils, zur Laufzeit gefüllt gerendert (weiß auf
   dunkler, dunkel auf heller **Taskleiste** – `SystemUsesLightTheme`, nicht das App-Theme) in der Pixelgröße der
   aktuellen DPI. Kein Profil erkannt oder Wechsel läuft → `rigshift-tray-*.ico`. Neu zuweisen bei
   Taskleisten-Theme-, Kontrast- und DPI-Wechsel.
6. **Tray-Popup nach Mockup** (`07-mockups`): Kopf mit Symbol + „Profil wechseln", Profilzeilen mit Symbol und Name,
   aktives Profil hervorgehoben mit Häkchen und „Aktiv", Status „Wechsle …" bei laufendem Wechsel (Zeilen gesperrt),
   unten Öffnen, Einstellungen, Beenden. Hauptfenster behält seine Seiten und bekommt nur das App-Icon in der
   Titelleiste (kein großes Logo in der Seitenleiste – Nutzer, 2026-09-13), Profilsymbole und Tokens. Mockup-Inhalte ohne Funktion (Monitor-Grafik, Seiten Anzeige/Audio) werden nicht gebaut.
7. **Texte.** Neue Strings de/en für Icon-Namen (VR, TV/Couch, Streaming) und Popup; Markentexte aus
   `BRAND-GUIDELINES.md` („Sim Rig ist aktiv", „Vorheriges Profil wiederhergestellt").
8. **README.** Logo per `<picture>` hell/dunkel, Slogan „Desk to rig. In one shift."; Social-Preview-Bild lädt der
   Nutzer selbst in den GitHub-Einstellungen hoch.
9. **Prüfung.** Build + Tests grün; Screenshots (PrintWindow) von Popup, Hauptfenster, Editor, Countdown in Hell und
   Dunkel; Tray-Icon bei 100 % hier, weitere DPI-Stufen und beide Taskleisten-Themes in M5 am Gaming-PC;
   Publish-Ordner enthält keine `.ttf/.otf/.woff*`.

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
| **M4.5** | Markenauftritt aus dem Brand Package 1.0 einbauen (Abschnitt 4.8) | App-/Fenster-Icon neu; Tray zeigt Profilsymbol passend zur Taskleistenfarbe und folgt Theme-/DPI-Wechsel; Tray-Popup nach Mockup; fünf Profilsymbole im Editor wählbar; README mit Logo; keine Fontdateien im Publish-Ordner |
| **M5** | **Test am Gaming-PC:** Desk ↔ Rig, Rig mit schlafendem G9 (Fehler-31-Fall), Rig ohne spacedesk + Nachziehen, Rollback bei Nicht-Bestätigung | Alle vier Fälle protokolliert; Skript bleibt parallel installiert |
| **M6** | Release 1.0: Velopack-Paket, GitHub-Release-Workflow, README mit Screenshots, CHANGELOG | Installation + Auto-Update von 1.0.0 auf 1.0.1 nachgewiesen |

### M5-Protokoll (Gaming-PC, 2026-09-13, Release-Build)

| Fall | Ergebnis (Log) | Befund → Korrektur |
|---|---|---|
| Desk ↔ Rig | Rig 1 Pfad, Desk 3 Pfade, je Versuch 1, Audio gesetzt | – |
| Rig mit schlafendem G9 | Erst **gescheitert** nach 2 s (31, dann 1610); nach Korrektur Erfolg in Versuch 5–6 nach ~5 s | 1610 gilt als vorübergehend; nach Fehlversuch auf verschwundene Bildschirme warten; dunkle Bildschirme nach endgültigem Fehlschlag wiederherstellen |
| Rollback bei Nicht-Bestätigung | Zurücksetzen und Timeout stellen Anzeige + Audio her; Rückweg zu eingeschlafenem G9 scheiterte zunächst | Auch auf die (rein optionale) Rollback-Topologie warten |
| Rig ohne spacedesk + Nachziehen | Erst verworfen, weil Windows beim Verbinden selbst Desk lud; nach Korrektur nachgezogen in einem Versuch | FollowUp ohne Zeitlimit und unabhängig vom erkannten Profil (display-topology.md, Regel 8) |
| `apply Rig` per Verknüpfung, Countdown-Fokus | Enter bestätigt ohne Klick | – |
| Tray-Popup auf G9 (125 %) nach Start am Desk (150 %) | Popup weit neben dem Tray | Eigene Platzierung in physischen Pixeln (H.NotifyIcon rechnet mit Start-DPI) |
| Tray-Icon helle Taskleiste, DPI | Icon folgt live, 24 px bei 150 %, 20 px bei 125 % | Popup blieb nach Theme-Wechsel halb hell → Ressourcen bei Theme-Wechsel neu auflösen |

### M6-Protokoll (Home-Server, 2026-09-13, Pakete aus GitHub Releases)

| Schritt | Ergebnis (Log) | Befund |
|---|---|---|
| Release-Workflow v1.0.0 / v1.0.1 per Tag | beide grün; Setup, portable ZIP, Full-Paket, für 1.0.1 zusätzlich Delta (23 MB) | – |
| `RigShift-win-Setup.exe --silent` (1.0.0) | installiert nach `%LocalAppData%\RigShift\current`, Uninstall-Eintrag geschrieben; App meldet „No update available, installed version 1.0.0“ | Setup **leert einen vorhandenen `%LocalAppData%\RigShift`** → Datenordner `%AppData%` bestätigt |
| 1.0.1 veröffentlicht, 1.0.0 neu gestartet | „Downloading update 1.0.1“ → „downloaded“ nach 9 s, Balloon | – |
| erneuter Start | Velopack installiert beim Start, „RigShift 1.0.1.0 starting“, EXE-Version 1.0.1 | – |
| `RigShift.exe list` (1.0.1) | Exit 0 | – |

---

## 6. Roadmap nach v1 (vom Nutzer gewählt, priorisiert)

**Umfangsprüfung 2026-09-14 (läuft):** Der Nutzer hält das Tool für überladen – Ziel ist ein kleines Tool, das
nebenher läuft und schnell ins Rig wechselt. Home Assistant (Punkt 12) ist gestoppt und nicht committet. Automatik
per Spiel steht in Frage (man startet Spiele erst im Rig; Umschalten während des Spielstarts ist riskant).
**Entschieden: der USB-Trigger bleibt** („an das Rig gehen, Wheelbase einschalten, alles Weitere passiert
automatisch“). Dazu auf Wunsch des Nutzers die **Wartezeit bis zur Ende-Aktion pro Regel einstellbar**
(`AutomationRule.ExitDelaySeconds`, Standard 10 s, 0–600) – Sim-Hardware muss oft kurz aus- und wieder eingeschaltet
werden.
**Entschieden nach Web-Recherche** (Kern laut Belegen: richtiger Primärbildschirm, ein Klick/Hotkey, Audiogerät,
„erst umschalten, dann Spiel starten“, robuste Wiederherstellung): **gestrichen** werden die Automatik per Spiel samt
Vorlagen (Punkt 6 – schaltet zu spät und mitten im Spielstart), die lokale HTTP-API (Punkt 11 – keine Nachfrage,
offener Port) und der Energieplan pro Profil (Punkt 9 – keine Nachfrage); Home Assistant (Punkt 12) verworfen.
**Behalten:** USB-Trigger, Wach halten, Apps und Lautstärke pro Profil, HDR + Hz pro Bildschirm, Monitornamen,
Seiten Bildschirme und Über & Hilfe. Die gestrichenen Stände bleiben in der Git-Historie (`f10c971` API,
`e151728` Energie, `eea7338`/`3fb1fd4` Automatik). Die Abschnitte zu 6, 9, 11 und 12 unten sind damit Historie.
**Neu für 1.3 gewählt** (zweite Recherche 2026-09-14, Nutzer hat alle vier genommen):
1. **Verlorene Fenster holen** – nach dem Umschalten Fenster, die auf keinem aktiven Bildschirm mehr liegen, auf den
   Hauptbildschirm verschieben (Discord/Steam/SimHub landen sonst auf abgeschalteten Bildschirmen). Immer an.
2. **Apps warten auf Gerät** – pro Profil optional ein USB-Gerät, auf das vor dem App-Start gewartet wird (mit
   Höchstwartezeit), sonst Hinweis im Tray. Wheel-Software und Spiele wollen das Gerät vor dem Start sehen.
3. **Rig-Check USB-Stromsparen** – Hinweis, wenn Windows ein Trigger-Gerät schlafen legen darf (Pedal-/Wheelbase-
   Abbrüche). Nur Hinweis mit Anleitung, keine Änderung ohne Adminrechte.
4. **Windows-Lautstärkeabsenkung bei Anrufen aus** – Profil-Schalter, beim Zurückschalten alter Wert (HKCU,
   undokumentiert → Fehler nur im Log).
**Umgesetzt 2026-09-14**: (1) `IWindowRescuer` / `WindowRescuer` (EnumWindows, sichtbar, nicht gecloakt, kein
Tool-Fenster; verloren = `MonitorFromRect` mit `MONITOR_DEFAULTTONULL` findet keinen Monitor, bei minimiert/maximiert
über die Normalposition; Verschieben per `SetWindowPlacement` zentriert in den Arbeitsbereich des Hauptbildschirms,
Größe begrenzt, Zustand bleibt, kein Fokusklau). Läuft nach **jedem** erfolgreichen Apply (Umschalten, Rollback,
Wiederherstellung nach Fehler, Nachholen) nach `SwitchOptions.WindowRescueDelay` (1 s); Geometrie in
`WindowGeometry`. (2) `Profile.AppsWaitForUsbDeviceId`/`…Name`/`AppsWaitSeconds` (30 s, 5–300, `set` wegen
Source-Generator); der Orchestrator fragt `IUsbDeviceList` sekündlich ab, startet die Apps bei Zeitablauf trotzdem
und meldet `AppsOutcome.DeviceMissing` (Tray-Meldung mit Gerätename und Sekunden, CLI-Zeile, Verlauf). Editor:
Auswahl „Nicht warten“ + verbundene Geräte (wie Automatik-Seite, gespeichertes getrenntes Gerät „(nicht
verbunden)“) und Sekundenfeld. (3) `IUsbPowerCheck` / `UsbPowerCheck` nur lesend (Energieschema AC über
`PowerReadACValueIndex`, `Device Parameters` unter `HKLM\…\Enum\USB`); Entscheidung in `UsbPowerSaving.ShouldWarn`
(warnt, wenn eine Geräteinstanz Stromsparen erlaubt und das Schema es nicht ausschließt – Schema allein warnt
nicht). Hinweis + Link auf `docs/usb-power-saving.md` in jeder USB-Regelkarte, geprüft beim Laden und
Aktualisieren; Probe `usb-power <VID_xxxx&PID_xxxx>`. (4) `Profile.DisableCommunicationsDucking` +
`IDuckingPreference` / `RegistryDuckingPreference` (HKCU `UserDuckingPreference`, `null` = Wert löschen): setzt 3,
merkt den Wert von vor dem ersten solchen Profil im Speicher, das nächste Profil ohne Schalter stellt ihn her,
Ablehnen stellt den Stand vor dem Wechsel her. Tests (204 → 241) belegen: Rettung nach Umschalten mit 1 s
Verzögerung, zweimal bei Rollback, nach Wiederherstellung und beim Nachholen, Ausnahme ändert das Ergebnis nicht,
DryRun/Blocked rufen nichts auf; Gerät schon da (keine Wartezeit), erscheint nach 3 s, erscheint nie (Apps starten,
`DeviceMissing`, Wartezeit geklemmt), kein Gerät (keine Abfrage), Ablehnen (kein Warten, keine Apps); Ducking
setzen + Wiederherstellen über ein Zwischenprofil, fehlender Wert kommt als fehlend zurück, Nutzerwert ohne
Schalter unberührt, Rollback, DryRun/Blocked unberührt, Fehler scheitert nicht; Geometrie, Warnregel und
Registry-Flag (DWORD/Binär); fehlender `appsWaitSeconds`-Schlüssel lädt 30. Probe `usb-power` läuft am Server.
**Nicht belegt:** echtes Verschieben von Fenstern nach einem Topologiewechsel, Warten mit echter Wheelbase,
Wirkung der Absenkung in einem Discord-Anruf, Warnung mit echten Sim-Geräten → Gaming-PC.
**Am Server angesehen** (Dev-Build, de, dunkel, ohne Umschalten): Automatik-Seite mit USB-Regel, Wartezeit und
Stromspar-Warnung samt Anleitungsknopf (Gerät mit gesetztem Flag); Editor mit Hz-/HDR-Auswahl je Bildschirm,
Anruf-Absenkung, Wach halten und „Vor dem App-Start auf Gerät warten“. Abgeschnittenes Ende-Label der Regelkarte
korrigiert (Umbruch).
Nicht übernommen: Audio pro App (undokumentierte Schnittstelle), Maus sperren, Desktopsymbole, Surround, VR, CEC.

**v1.1 – Updates in der App** (vom Nutzer gewählt 2026-09-14; kommt zuerst, weil klein und für alle späteren
Releases nützlich)

- U1. Einstellungen „Über / Updates“: Versionsnummer, Knopf „Nach Updates suchen“, Status (aktuell / wird geladen /
  bereit / Fehler). Die automatische Prüfung (Start + 24 h) bleibt. **Umgesetzt 2026-09-14**: Dev-Build zeigt
  „Entwicklungs-Build“ mit gesperrtem Knopf; lokal gepackte, installierte 1.0.1 prüft beim Klick (Log „requested by
  the user“), Knopf während der Prüfung gesperrt, Status „Aktuell. Zuletzt geprüft …“. Laden/Bereit nur ohne neueres
  Release nicht live belegt.
- U2. „Jetzt neu starten und installieren“ in den Einstellungen und aus der Tray-Meldung; gesperrt, solange ein
  Umschaltvorgang läuft. **Umgesetzt 2026-09-14** als Knopf in den Einstellungen und Eintrag im Tray-Menü (nur wenn
  ein Update bereit ist); ein Klick auf die Tray-Meldung öffnet die Einstellungen statt sofort neu zu starten, weil
  ein ungewollter Neustart per Meldungsklick überraschend wäre. Technik: `WaitExitThenApplyUpdates` + sauberes
  `Quit()` (Tray-Icon und Log werden geschlossen), nicht `ApplyUpdatesAndRestart` (beendet sofort). Ende-zu-Ende am
  Server belegt: aktueller Code lokal als 1.0.0 gepackt und installiert → lädt 1.0.1 von GitHub (8 s), Knopf erscheint,
  Klick → „Restarting to install update 1.0.1“, Exit 0, neuer Prozess nach 2 s, `sq.version` 1.0.1, EXE aus Commit
  `30182fd` (offizielles Release). Damit sind auch die U1-Zustände Laden/Bereit belegt.
- U3. Einstellung „Updates automatisch installieren“ / „nur benachrichtigen“. Grund: Nutzer, die ungefragte
  Änderungen nicht wollen; ohne Code-Signatur hängt die Update-Sicherheit allein am GitHub-Konto. **Umgesetzt
  2026-09-14**: `AppSettings.OnlyNotifyAboutUpdates` (Standard `false` = automatisch; bewusst so herum, weil der
  JSON-Source-Generator Property-Initialisierer bei fehlendem Schlüssel nicht anwendet – `InstallUpdatesAutomatically
  = true` hätte 1.0-Nutzer still auf „nur melden“ gestellt, per Test belegt). Aus = prüfen und
  melden (Balloon „verfügbar“, Status, Knopf), **nichts herunterladen**; „Neu starten und installieren“ lädt dann und
  startet sofort neu. Zusätzlich liest `Program.cs` die Einstellung vor `VelopackApp.Run()` und schaltet Auto-Apply
  beim Start ab – sonst würde ein früher im Auto-Modus geladenes Paket trotz „aus“ installiert. Einschalten bei
  gemeldeter Version lädt sofort. Am Server belegt (Build als 1.0.0 installiert): mit `onlyNotifyAboutUpdates: true`
  zweimal gestartet → je „Update 1.0.1 available, automatic installation is off“, kein Download, bleibt 1.0.0; ohne
  settings.json → lädt, nächster Start installiert 1.0.1. **Nicht belegt:** Klick „installieren“ im Zustand
  „verfügbar“ (RDP-Sitzung getrennt, keine Eingabe möglich) – der Download-Pfad ist derselbe wie im Auto-Modus.
- U4. Release-Notes der neuen Version anzeigen (aus dem Velopack-Paket bzw. Link aufs GitHub-Release).
  **Umgesetzt 2026-09-14**: `VelopackAsset.NotesMarkdown` (= CHANGELOG-Abschnitt) wird in `RigShift.Core.Updates.
  ReleaseNotes` zu Klartext (Überschriften, Aufzählung „•“, umbrochene Punkte zusammengefügt, Links/Fett/Code
  entfernt) und unter dem Update-Status gezeigt, solange eine neuere Version bekannt ist; darunter Link
  `…/releases/tag/vX.Y.Z` (nur https wird geöffnet). Kein Markdown-Renderer – zusätzliche Abhängigkeit lohnt für
  wenige Zeilen nicht. Am Server belegt (Build als 1.0.0, nur melden): Einstellungen zeigen „Version 1.0.1 ist
  verfügbar.“, „Fixed“, den umbrochenen Punkt aus dem 1.0.1-CHANGELOG als eine Zeile und den Link. Link-Klick
  (Browser) nicht geprüft.

Laufende App und Autostart: Die Prüfung läuft auch im Dauerbetrieb alle 24 h und meldet per Tray-Meldung; seit U2
muss niemand bis zum nächsten Windows-Start warten.

**Veröffentlicht als 1.1.0 (2026-09-14)**, Workflow-Run 34806008712 success, Delta-Paket 32 MB. Abnahme am Server:
installiertes offizielles 1.0.1 lädt 1.1.0 in 10 s, nächster Start „RigShift 1.1.0.0 starting“, `sq.version` 1.1.0.
- Randbedingung: kein GitHub-Token in der App (wäre aus der EXE auslesbar); Limit 60 API-Abfragen/h pro IP reicht.

**v1.1 – Auslösen und Steuern**

Zuschnitt (vom Nutzer gewählt 2026-09-14): Punkte 1 und 2 kommen zusammen als **1.2.0** – beide sind reine Auslöser
ohne neue Audio-Logik und am Server prüfbar. Mikrofon, Lautstärke und Apps (3–5) folgen als eigener Block, weil sie
einen Hardwaretest am Gaming-PC brauchen. Festlegungen zu Punkt 1:

- Eingabe im Profil-Editor als **Aufnahmefeld** (anklicken, Kombination drücken, Löschen-Knopf). Strg, Alt oder Win
  ist Pflicht, damit keine normale Taste systemweit belegt wird – Umschalt allein reicht nicht (Umschalt+A würde das
  große A in jeder App schlucken).
- Während der Profil-Editor offen ist, sind alle Kürzel abgemeldet: sonst würde das Aufnehmen eines schon belegten
  Kürzels sofort umschalten, und die Belegt-Prüfung beim Speichern (`RegisterHotKey` probeweise) sähe die eigenen.
- Nochmal dasselbe Kürzel drücken, während dessen Bestätigungs-Countdown läuft, bestätigt (wie Enter).
- Belegt eine andere App das Kürzel (`RegisterHotKey` schlägt fehl): Hinweis direkt im Editor beim Speichern, beim
  App-Start einmalige Tray-Meldung für alle fehlgeschlagenen Kürzel, beides geloggt.
- Ein Kürzel schaltet **genau wie ein Tray-Klick** um – Bestätigungs-Countdown nach Profil-/App-Einstellung. Das
  Sicherheitsnetz bleibt, weil ein Kürzel gerade dann gedrückt wird, wenn man den Zielbildschirm nicht sieht.

1. Globale Tastenkürzel pro Profil (`RegisterHotKey`). **Umgesetzt 2026-09-14**: `Profile.Hotkey` (MOD-Flags +
   Virtual-Key), Prüfung in `ProfileEditing.Validate` (ungültig / doppelt), `HotkeyService` (Message-only-Fenster,
   neu registriert nur bei echter Änderung, `MOD_NOREPEAT`), Kürzel im Tray-Menü rechts neben dem Profil. Am Server
   belegt (Dev-Build, RDP): Registrierung im Log; von einer anderen App gehaltenes Kürzel → Start-Warnung mit
   Fehler 1409; Editor nimmt per Tastatur `Strg+Alt+F3` auf, verweigert das Speichern, sobald eine andere App es hält
   („Eine andere App belegt dieses Tastenkürzel schon“). **Nicht belegt** (würde am Server umschalten): Drücken des
   Kürzels, erneutes Drücken als Bestätigung, Tray-Meldung und Menü-Beschriftung optisch → Gaming-PC.
2. URI-Schema `rigshift://apply/<name>` (HKCU-Registrierung durch Velopack-Hook). **Umgesetzt 2026-09-14**:
   `RigShiftUri` macht daraus `apply <name>` (gleicher Weg wie die CLI, also mit Countdown). Bewusst **nur** `apply`
   und **keine** Optionen im Link (kein `?no-confirm`): eine Webseite darf weder Profile ändern noch das
   Sicherheitsnetz abschalten. Registrierung in den Velopack-Hooks nach Installation/Update, entfernt vor der
   Deinstallation; portable Kopien registrieren nicht (Pfad wäre instabil). Am Server belegt: ungültige Links
   (`save`, Query) → Exit 5 + Log-Warnung. **Nicht belegt:** Registry-Eintrag aus dem Hook (erst mit installiertem
   Paket) und Aufruf aus Browser/Win+R.
Zusätzlich vor 1.2.0 (vom Nutzer gewählt 2026-09-14): Schalter „Nach dem Umschalten bestätigen“ in den Einstellungen
statt nur „0 Sekunden“, weil 0 schwer zu finden ist. Gespeichert wird weiter nur `ConfirmTimeoutSeconds` (aus = 0, an
= angezeigter Wert, mind. 1) – kein zweiter Wert, der widersprechen könnte; alte Dateien bleiben gültig. Beim
Ausschalten Hinweis auf den fehlenden Rückweg. Profil-Editor unverändert (eigener Wert 0 = ohne Nachfrage). Am Server
belegt: Schalter aus → `confirmTimeoutSeconds: 0`, Hinweistext wechselt, Sekundenfeld verschwindet; wieder an → 15.

**Veröffentlicht als 1.2.0 (2026-09-14)**, Workflow-Run 34809298875 success; Assets Setup, Portable, full, delta;
`releases/latest` = v1.2.0.

Zuschnitt Punkte 3–5 (vom Nutzer gewählt 2026-09-14): jetzt bauen und am Server mit Unit-Tests belegen, **kein
Release vor dem Gaming-PC-Test**; der Test deckt dann 1.2.0 und diesen Block zusammen ab, danach 1.3.0.

**Gaming-PC-Test 2026-09-14** (per SSH gesteuert, Programme über eine kurzlebige geplante Aufgabe in der
Konsolensitzung gestartet, Belege aus dem Log; Release-Build von `e38b8f9`):
- 1.0.1 → 1.2.0 per Auto-Update beim Neustart installiert; `HKCU\Software\Classes\rigshift` danach vorhanden.
  `rigshift://apply/Rig` über `explorer.exe` (wie Win+R) schaltet; mit Schalter „Bestätigen“ aus kein Dialog.
- Automatik: Regel AMS2 → Rig (zurück, ohne Bestätigung). Spielstart per Steam → nach ~1 s Wechsel (G9 nach
  Fehler‑31‑Wiederholungen aktiv, Ton auf Headset); Spiel beendet → nach 10 s zurück auf Desk.
- EXE-Namen belegt: `Le Mans Ultimate`, `AssettoCorsaEVO`, `AMS2AVX` (laufende Prozesse).
- Lautstärke: Rig 40 % gesetzt; nicht bestätigt (eigene 8 s) → Rückfall inkl. 50 %, Apps nicht gestartet.
- Apps: Start Editor + fensterloses Programm; beim Wechsel zurück Editor per Fenster beendet, fensterloses nach
  5 s hart beendet; läuft der Editor schon → „already runs, not started“.
- Seiten Bildschirme, Automatik, Über & Hilfe in echt angesehen (Screenshots im README), Diagnose-Infos mit echten
  Geräten in der Zwischenablage.
- **Offen (braucht den Nutzer):** Tastenkürzel an der echten Tastatur (Umschalten, erneutes Drücken bestätigt,
  belegtes Kürzel, Anzeige im Tray-Menü), Pausieren über Seite und Tray (UIA-Umschalten wirkte nicht, Ursache
  ungeklärt), Beenden einer Admin-App, Erkennen auf allen Bildschirmen, Umbenennen, Dateiauswahl „eigene EXE“.

3. Mikrofon pro Profil + getrennte Kommunikationsrolle (gleiche API wie Wiedergabe, fast gratis). **War schon seit
   M3 umgesetzt** (Editor: vier Audio-Zeilen, Orchestrator setzt Aufnahme/Kommunikation getrennt).
4. Lautstärke pro Profil (`IAudioEndpointVolume`). Festlegung: **Wiedergabe und Aufnahme** je ein Regler mit
   Schalter „Lautstärke festlegen“ (aus = unverändert); die Kommunikations-Geräte bekommen keinen, weil das den Editor
   überfrachtet und der Pegel meist am selben Gerät hängt. Regler nur aktiv, wenn ein Gerät gewählt ist – die
   Lautstärke gehört zum Gerät, nicht zum gerade zufälligen Standard. Beim Zurückrollen wird die vorherige Lautstärke
   des Geräts wiederhergestellt (wie der Standard), sonst bliebe ein abgelehnter Wechsel halb bestehen.
   **Umgesetzt 2026-09-14**: `AudioAssignment.RecordingVolumePercent`, `IAudioController.GetVolumeAsync`, Regler im
   Editor. Unit-Tests belegen Setzen, Ignorieren ohne Gerät und Wiederherstellen beim Zurückrollen. **Nicht belegt**
   (am Server keine Audio-Endpunkte, Editor nicht angesehen): echte Pegel, Editor-Optik → Gaming-PC.
5. Apps pro Profil starten/beenden. Festlegung (Umfang „Starten + Beenden“): Liste pro Profil in fester Reihenfolge,
   je Eintrag Aktion, Programmpfad (Umgebungsvariablen erlaubt), Argumente (nur Start), Wartezeit danach. Start nur,
   wenn kein Prozess mit dem Namen der EXE läuft; Beenden schließt erst das Fenster und beendet nach 5 s hart.
   Läuft **erst nach der Bestätigung** (bzw. direkt nach Audio ohne Bestätigung): ein abgelehnter Wechsel soll keine
   Apps gestartet oder Arbeit geschlossen haben, und es gibt kein sinnvolles „App-Rückgängig“. Fehler machen den
   Wechsel nicht kaputt, sondern melden „Apps unvollständig“ (wie Audio). Nicht in 1.3: Ziehen zum Sortieren, Start
   als Admin, automatisches Beenden beim Zurückwechseln.
   **Umgesetzt 2026-09-14**: `Profile.Apps` (`AppAction`; Property mit `set`, damit Profile ohne Schlüssel eine leere
   Liste bekommen – Test `Load_ProfileFromVersion1_2_HasNoApps`), `IAppLauncher` + `ProcessAppLauncher` (Abgleich über
   den Prozessnamen, weil der Pfad eines Admin-Prozesses ohne Admin-Rechte nicht lesbar ist), Ausgang
   `SwitchResult.Apps` in Tray-Meldung und CLI. Unit-Tests belegen Reihenfolge nach Bestätigung, Wartezeit, kein
   Doppelstart, Fehler → weiter + unvollständig, keine Apps bei Ablehnung. **Nicht belegt:** echtes Starten/Beenden
   (Schließen per Fenster, harter Abbruch, Admin-Prozess) und Editor-Optik → Gaming-PC.

Zusätzlich in 1.3.0 (Idee und Antworten des Nutzers 2026-09-14): **eigene Monitornamen**. Festlegungen:
**pro Monitor, überall gültig** statt pro Profil – einmal benennen reicht, und genau das unterscheidet zwei gleiche
Modelle (zwei CM27X3). Anzeige **„Name · Modell“** (ohne eigenen Namen wie bisher), in Hauptliste, Editor, Diagnose,
Meldungen und CLI. Vergeben wird der Name **im Profil-Editor** (Feld je Anzeigekarte); keine eigene Oberfläche in der
Diagnose, weil das mehr Fläche für wenig Nutzen wäre.
**Umgesetzt 2026-09-14**: `DisplayAssignment.CustomName` – bewusst nicht in `DisplayIdentity`, weil
`SwitchOrchestrator` Identitäten per Record-Gleichheit mit dem Snapshot vergleicht und ein Name das bräche. Kein
eigener Namensspeicher: `ProfileCatalog.SaveAsync` überträgt den Namen per `DisplayNames.Propagate` auf alle Profile
mit demselben `TargetDevicePath` (auch das Löschen); neu erfasste Anordnungen (`CurrentArrangement`, `Capture`,
CLI `save`/`status`) übernehmen bekannte Namen. So zeigen Kernmeldungen und die CLI den Namen ohne zusätzliche
Abhängigkeit. Unit-Tests belegen Anzeige, Kürzen, Übertragen, Löschen und Übernehmen; Editor und Hauptliste am
Server angesehen.

Ebenfalls in 1.3.0 (Wunsch und Wahl des Nutzers 2026-09-14): **Diagnoseseite entfällt**, stattdessen Abschnitt
**„Fehlersuche“ in den Einstellungen** – letzte Umschaltungen als kurze Liste (Symbol, Uhrzeit, Profil, Ergebnis,
Dauer), Knopf „Diagnose-Infos kopieren“ (Klartext für ein GitHub-Issue: Version, Windows, Bildschirme mit Pfaden und
EDID, Audiogeräte, Verlauf) und „Log-Ordner öffnen“. Grund: rohe Tabellen mit Gerätepfaden helfen Endnutzern nicht,
wer ein Problem meldet, braucht ohnehin einen kopierbaren Text; eine Seite weniger in der Navigation.

Nachgeschärft am selben Tag (Wunsch des Nutzers, die Seitenleiste nicht so leer wirken zu lassen; Auswahl per
Fragerunde): Seitenleiste **Profile · Bildschirme · Über & Hilfe**, Einstellungen unten.
- **Über & Hilfe** statt Abschnitt in den Einstellungen: Version, Updates und Neuigkeiten ziehen aus den Einstellungen
  hierher, dazu die Fehlersuche (letzte Umschaltungen, „Diagnose-Infos kopieren“, „Log-Ordner öffnen“) und Links zu
  GitHub, „Problem melden“ und Lizenz. In den Einstellungen bleiben nur echte Einstellungen.
- **Bildschirme**: angeschlossene Monitore als Karten mit Namensfeld und „Erkennen“ (große Nummer kurz auf jedem
  Bildschirm). Damit lassen sich auch Monitore benennen, die in keinem Profil stehen – deshalb zusätzlich ein
  Namensregister `AppSettings.DisplayNames` (Gerätepfad → Name); Profile tragen den Namen weiter selbst, damit Kern und
  CLI ihn ohne Einstellungen kennen.
- **Automatik** (Regeln wie „Spiel startet → Rig“) erst mit Punkt 6 bzw. 7, kein leerer Platzhalter vorher.

**Umgesetzt 2026-09-14**: `DisplaysPage` (Name wird beim Verlassen des Felds oder mit Enter gespeichert,
`ProfileCatalog.RenameDisplayAsync` schreibt Register und alle betroffenen Profile), `IdentifyWindow` (je aktivem
Bildschirm 3 s, per `NativeWindow.CenterOnRect` in physischen Pixeln mittig auf Position und Modus), `AboutPage` mit
`DiagnosticsReport` (Englisch, invariante Kultur, keine Endpoint-IDs). Die Update-Meldung im Tray führt jetzt auf
„Über & Hilfe“. Am Server belegt (Dev-Build, RDP): Seiten dunkel/hell und de/en angesehen, Name „Remote“ landet in
`settings.json` → `displayNames`, Erkennen zeigt die Nummer, Bericht in der Zwischenablage. **Nicht belegt:**
Erkennen auf mehreren Bildschirmen mit unterschiedlicher Skalierung → Gaming-PC.
6. Prozess-Trigger (WMI `Win32_ProcessStartTrace` oder ETW; Fallback Polling 2 s) + **Spiele-Vorlagen**
   (LMU, iRacing, ACC, AC EVO, rFactor 2, AMS2, F1 – als JSON in `templates/`, per PR erweiterbar).
   Festlegungen (Fragerunde 2026-09-14):
   - **Nur Polling alle 2 s**, kein WMI: `Win32_ProcessStartTrace` braucht Adminrechte, zwei Wege wären doppelt zu
     testen, und bis zu 2 s Verzögerung sind bei Sim-Titeln mit Ladebildschirm egal.
   - Regel = Spiel (Vorlage **oder eigene EXE**) → Profil. Keine Auswahl aus laufenden Prozessen (Rauschen).
     Vorlagen je Spiel eine Datei in `templates/games/`, eingebettet in Core; Tests prüfen jede Datei.
   - **Spielende pro Regel wählbar**: im Profil bleiben / zurück zum vorigen Profil / auf Profil X. Gehandelt wird erst
     nach 10 s ohne Prozess (Neustart ≠ Ende) und nur, solange das Profil der Regel noch aktiv ist – ein vom Nutzer
     inzwischen gewähltes Profil wird nicht überschrieben.
   - **Bestätigung pro Regel wählbar**: Einstellung wie bisher oder „ohne Bestätigung“.
   - Beim ersten Abfragen laufende Spiele setzen nur die Ausgangslage (kein Umschalten beim App-Start neben
     laufendem Spiel); ebenso eine neu angelegte oder auf ein anderes Spiel geänderte Regel.
   - Regeln und „pausiert“ in `settings.json` (`automationRules`, `automationPaused`); Pausieren auf der Seite
     „Automatik“ und im Tray-Menü (nur mit Regeln).
   **Umgesetzt 2026-09-14**: `Core/Automation` (`AutomationRule`, `GameTemplates`, `ProcessNames`, `ProcessTrigger`),
   `IProcessList` + `Windows/Apps/ProcessList`, `AutomationService` (DispatcherTimer, Abfrage im Thread-Pool, keine
   Auswertung während eines Wechsels), `AutomationPage`. Unit-Tests belegen Start, Ausgangslage, Rückkehr nach
   Verzögerung, Neustart, Nutzerwechsel, alle Ende-Aktionen, deaktivierte Regel, Vorlagen und Settings-Roundtrip.
   **Nicht belegt:** echte Spiele und EXE-Namen (LMU, AC EVO aus Websuche) → Gaming-PC.
   Nachtrag 2026-09-14: Vorlagen RaceRoom (`RRRE64.exe`, `RRRE.exe`), AC Rally (`acr.exe`) und Forza Horizon 6
   (`forzahorizon6.exe`), Namen aus den Installationsordnern am Gaming-PC gelesen (nicht gestartet); Issue-Formular
   „Game template request“. **Pausieren auf der Seite am Server belegt** (Dev-Build, ohne Regeln): Schalter per
   UIA-`TogglePattern` → `automationPaused` true/false in `settings.json`, Log „Automation paused/resumed“, Infoleiste
   sichtbar. Der frühere Fehlschlag lag am Testskript: `FindFirst` nach dem Namen trifft zuerst den gleichnamigen
   `TextBlock` der CardControl – nach `ControlType.Button` filtern. Tray-Menüpunkt (nur mit Regeln) bleibt für den
   Gaming-PC.

**v1.2 – Automatik und Komfort**

7. USB-Gerät verbunden (Wheelbase/Dongle per VID/PID).
   **Entschieden 2026-09-14** (Fragerunde mit dem User, alle Empfehlungen angenommen):
   - **Teil der Automatik**, kein eigenes Konzept: eine Regel hat als Auslöser ein Spiel *oder* ein USB-Gerät.
     Ende-Aktion (inkl. 10 s Karenz, z. B. Wheelbase-Neustart), „Ohne Bestätigung“, Pausieren und Ausgangslage
     beim ersten Abfragen gelten unverändert.
   - **Gerätewahl aus den verbundenen Geräten**; gespeichert wird `VID_xxxx&PID_xxxx` (Port-unabhängig) plus Name zur
     Anzeige. Ein gespeichertes, gerade nicht verbundenes Gerät bleibt als „nicht verbunden“ auswählbar.
     Keine manuelle VID/PID-Eingabe.
   - **Keine Kombination** Spiel + Gerät in einer Regel; genau ein Auslöser pro Regel.
   - Erkennung (eigene Entscheidung): **Polling im selben 2-s-Takt** über `CM_Get_Device_ID_List` (Filter USB,
     nur vorhandene Geräte) statt `RegisterDeviceNotification`. Begründung: ein Pfad für beide Auslöser, dieselbe
     getestete Logik (Anwesenheit eines Schlüssels, Ausgangslage, Karenz) ohne verstecktes Fenster; die Abfrage kostet
     wenige Millisekunden, und 2 s Verzögerung sind beim Einschalten einer Wheelbase egal.
   **Umgesetzt 2026-09-14**: `AutomationRule.UsbDeviceId/UsbDeviceName`, `UsbDeviceIds` (VID/PID aus der Instanz-ID,
   auch mit Suffixen wie `&LAMPARRAY` oder `&MI_00`), `ProcessTrigger` → `AutomationTrigger` (Schlüsselmenge aus
   Prozessnamen und `usb:`-Schlüsseln; ein Doppelpunkt kommt in Prozessnamen nicht vor), `IUsbDeviceList` +
   `Windows/Apps/UsbDeviceList` (Hubs ausgefiltert, Name aus BusReportedDeviceDesc → FriendlyName → DeviceDesc),
   Auswahl „USB-Gerät“ auf der Automatik-Seite mit Geräteliste und Aktualisieren. Probe: `RigShift.Probe usb`.
   Am Gaming-PC belegt: Geräteliste mit echten Namen, „nicht verbunden“-Eintrag, beim Start angestecktes Gerät schaltet
   nicht, de + en. **Nicht belegt:** echtes Ein-/Ausschalten eines Geräts (braucht den Nutzer, z. B. Wheelbase).
8. Rennmodus: Fokus-Assistent an, Spielmodus, Standby/Bildschirmschoner aus; alles beim Zurückwechseln zurück.
9. Energieplan pro Profil (`powercfg /setactive`).
   **Entschieden 2026-09-14 für 8 + 9 als ein Block** (Fragerunde mit dem User, alle Empfehlungen angenommen):
   - Pro Profil **„Wach halten“** (kein Standby, kein Bildschirmschoner, Monitore bleiben an) und **Energieplan**
     (aus = unverändert). Beides über dokumentierte APIs und verlustfrei umkehrbar.
   - **Nicht stören und Spielmodus entfallen**: Nicht stören hat keine offizielle API (nur undokumentierte
     Windows-Interna, die mit einem Update still brechen können), den Spielmodus schaltet Windows für erkannte Spiele
     selbst. Stattdessen Doku-Hinweis auf die Windows-Automatik für Vollbild-Spiele.
   - **Gesetzt zusammen mit Audio, beim Zurückrollen zurückgesetzt** – anders als Apps, weil beides ohne Verlust
     umkehrbar ist und sonst während des Countdowns noch der alte Energieplan gälte.
   - Eigene Entscheidungen: Wach halten per **Power Request** (`PowerCreateRequest`, DisplayRequired + SystemRequired)
     statt `SetThreadExecutionState` – an ein Handle gebunden statt an den aufrufenden Thread, in `powercfg /requests`
     als RigShift sichtbar, endet mit dem Prozess. Energieplan per `PowerSetActiveScheme` statt `powercfg`-Aufruf.
     Rückkehr: der Plan von vor dem ersten Profil mit eigenem Plan wird gemerkt und vom nächsten Profil **ohne** Plan
     zurückgesetzt (auch über ein Zwischenprofil hinweg); nur im Speicher, nach einem App-Neustart bleibt der Plan
     einfach. Wach halten folgt dem Profil und wird beim App-Start für das aktive Profil neu gesetzt. Fehler nur im
     Log, kein eigener Ausgang im Ergebnis.
   **Umgesetzt 2026-09-14**: `Profile.KeepAwake/PowerPlan`, `IPowerController` + `Windows/Power/PowerController`,
   Abschnitt „Energie“ im Profil-Editor, Probe `power` und `keep-awake <s>`. Unit-Tests belegen Wach halten folgt dem
   Profil, Plan setzen und Rückkehr über ein Zwischenprofil, Plan des Nutzers bleibt unberührt, Zurückrollen, Dry-Run/
   Blockiert unverändert, Fehler lässt den Wechsel gelingen. **Am Server belegt:** Planliste mit Namen und aktivem Plan,
   Power Request unter DISPLAY und SYSTEM in `powercfg /requests`, nach Prozessende weg. **Nicht belegt:** Plan wirklich
   umschalten (Systemeinstellung, am Server bewusst nicht), Bildschirmschoner/Standby bleiben aus, Editor-Optik →
   Gaming-PC.
10. HDR je Bildschirm (CCD `DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE`), Bildwiederholrate; Nachtlicht nur
    über undokumentierte Registry → als „experimentell" markieren.
    **Entschieden 2026-09-14** (Fragerunde mit dem User, alle Empfehlungen angenommen):
    - **HDR pro Bildschirm** als „Unverändert / An / Aus“. „Aktuellen Zustand speichern“ merkt sich An/Aus für
      HDR-fähige Bildschirme; bestehende Profile bleiben „Unverändert“. Gesetzt direkt nach der Anordnung über CCD
      (`DisplayConfigSetDeviceInfo`), beim Zurückrollen zurück.
    - **Bildwiederholrate im Editor wählbar**: Auswahl der Raten, die der Monitor bei der gespeicherten Auflösung
      meldet. Gespeichert und gesetzt wurde sie schon seit 1.0.
    - **Nachtlicht entfällt** – nur über einen undokumentierten Registry-Blob, der mit einem Windows-Update still
      brechen kann (gleiche Begründung wie „Nicht stören“ in Punkt 8).
    - Eigene Entscheidungen: HDR lesen/setzen erst mit der 24H2-Anfrage (`GET_ADVANCED_COLOR_INFO_2`/`SET_HDR_STATE`,
      trennt HDR von Wide Color/Auto-Farbverwaltung), sonst die ältere „Advanced Color“-Anfrage. Gesetzt im
      Orchestrator nach jedem erfolgreichen `SetDisplayConfig` auf frischem Snapshot (ein gerade eingeschalteter
      Bildschirm meldet HDR erst aktiv) – dadurch stellen Zurückrollen und Wiederherstellen HDR mit dem gemerkten
      Vorzustand zurück. Fehler nur im Log. Raten per **DXGI** (`GetDisplayModeList`) statt `EnumDisplaySettings`, weil
      DXGI exakte Brüche liefert (239761/1000 statt 239) und die Wahl so unverändert durch CCD geht. Raten, die auf
      zwei Nachkommastellen gleich aussehen, erscheinen einmal; die gespeicherte bleibt immer drin.
    **Umgesetzt 2026-09-14**: `DisplayAssignment.Hdr`, `RefreshRate`, `IDisplayConfigurator.SetHdrAsync/
    ListRefreshRatesAsync`, `DxgiModes`, Editor je Bildschirm Auswahl Hz + HDR, Probe `rates`. Unit-Tests belegen
    HDR setzen nur bei Abweichung, unverändert/nicht unterstützt fasst nichts an, Zurückrollen stellt HDR zurück,
    Fehler lässt den Wechsel gelingen. **Am Server belegt:** Probe `rates` liest HDR-Zustand und Raten der
    RDP-Anzeige. **Nicht belegt:** HDR wirklich umschalten, gewählte Rate beim Wechsel, Editor-Optik → Gaming-PC.
11. Lokale HTTP-API (`http://127.0.0.1:<port>/api/profiles`, Token in settings.json) für Skripte und SimHub.
    **Entschieden 2026-09-14** (Fragerunde mit dem User, alle Empfehlungen angenommen):
    - **Standardmäßig aus**, Schalter in den Einstellungen. Beim Einschalten wird ein Token erzeugt und mit Port,
      Kopier-Knopf und Beispielaufruf angezeigt. Kein lauschender Port bei Nutzern ohne Bedarf.
    - **Nur 127.0.0.1, Token Pflicht für alle Aufrufe** (`Authorization: Bearer <token>`). Ohne Token könnte jede
      geöffnete Webseite per `fetch()` an localhost umschalten oder die Profile auslesen; ein eigener Header erzwingt
      im Browser einen Preflight, den die API nicht beantwortet. Kein LAN (Firewall, URL-Reservierung, Angriffsfläche).
    - **Umfang wie die CLI**: `GET /api/status`, `GET /api/profiles`, `POST /api/profiles/{name}/apply`
      (`?dryRun=true`, `?noConfirm=true`). Pausieren und Bestätigen per API erst bei Bedarf.
    - **Umschalten wartet auf das Ergebnis** (wie die CLI) und liefert den Ausgang als JSON.
    - Eigene Entscheidungen: **`TcpListener` auf Loopback mit minimalem HTTP/1.1** statt `HttpListener` (http.sys
      verlangt für `127.0.0.1` eine URL-Reservierung mit Adminrechten und lehnt abweichende Host-Header ab) und statt
      Kestrel (zusätzliches Shared Framework, mehrere MB im Paket für drei Endpunkte). Parser und Endpunkte liegen in
      Core und sind ohne Windows testbar. Standardport **47800**, in `settings.json` änderbar. Ein abgeschlossener
      Wechsel antwortet 200 mit `outcome` (auch `rolledBack`/`failed` – die Anfrage selbst hat geklappt); 401 ohne
      gültiges Token, 404 unbekanntes Profil, 409 bei laufendem Wechsel.
    **Umgesetzt 2026-09-14**: `Core/Api` (`HttpMessages`, `ApiHandler`, `HttpApiServer`), `AppSettings.HttpApiEnabled/
    HttpApiPort/HttpApiToken`, `HttpApiService` (startet/stoppt bei Einstellungsänderung), Karte in den Einstellungen
    (Port, Token, Kopieren, Beispiel, Neues Token, Link auf `docs/http-api.md`). JSON ohne `\uXXXX`-Escapes, weil nur
    als `application/json` ausgeliefert. Unit-Tests belegen Parser, Token, Host-Prüfung, alle Endpunkte und
    Statuscodes sowie echte Aufrufe per `HttpClient`. **Am Server belegt** (Dev-Build): 200/401/403/405/404 per curl,
    Dry-Run über den UI-Thread, Lauschen nur auf 127.0.0.1, „Neues Token“ macht das alte ungültig, Ausschalten schließt
    den Port, Einschalten öffnet ihn wieder, Karte angesehen. **Nicht belegt:** echtes Umschalten per API → Gaming-PC.
12. Home Assistant: MQTT-Discovery (aktives Profil als Sensor, Wechsel als Select-Entität) auf Basis der API.
    **Entschieden 2026-09-14** (Fragerunde mit dem User):
    - **MQTT-Discovery**, RigShift verbindet sich ausgehend zum Broker (z. B. Mosquitto-Add-on). Keine eigene
      HA-Integration über die HTTP-API: die API bliebe sonst nicht auf 127.0.0.1, und ein zweites Repo wäre zu pflegen.
    - Entitäten eines Geräts „RigShift (PC-Name)“: **Select „Profil“** (zeigt und schaltet), **ein Knopf pro Profil**,
      **Schalter „Automatik pausiert“**, **Sensor „Letzter Wechsel“** (Ausgang + Zeitpunkt), Verfügbarkeit per Last Will.
    - **Wechsel aus HA ohne Countdown** – am PC sitzt dann oft niemand, der bestätigen könnte.
    - Eigene Entscheidungen: Bibliothek MQTTnet; Passwort per DPAPI (aktueller Nutzer) verschlüsselt in `settings.json`.

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
  Umsetzung M6: `UpdateService` lädt im Hintergrund und meldet per Tray-Balloon; Velopacks „Auto-Apply beim Start“
  ist nur für die Tray-App an, nicht für CLI-Aufrufe (der Neustart zum Installieren würde den Exit-Code verlieren).
- Release-Notes = Abschnitt der Version aus `CHANGELOG.md`; der Workflow bricht ab, wenn er fehlt. Vor dem Packen
  lädt `vpk download github` das vorige Release, damit ein Delta-Paket entsteht.
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
| Bildschirm im Standby liefert evtl. keinen `monitorDevicePath` → Planner meldet `NotAttached` statt `AttachedButUnavailable` und wartet nicht | **M5 geklärt:** der schlafende G9 meldet sich als verfügbar, fällt beim Aufwachen aber ~3 s ganz vom Bus (Fehler 31, dann 1610) → nach einem Fehlversuch wird auch auf verschwundene Bildschirme gewartet |
| Velopack installiert nach `%LocalAppData%\RigShift` – derselbe Ordner wie Profile/Einstellungen; eine Deinstallation könnte die Profile löschen | **M6 geklärt:** Velopack löscht bei der Deinstallation den ganzen Installationsordner (Doku „Uninstalling“) → Daten liegen ab 1.0 in `%AppData%\RigShift`. Keine Migration im Code, weil es vor 1.0 keine Nutzer gab; der Gaming-PC wird einmalig von Hand umgezogen |
| spacedesk-Display nach Verbindung an falscher Position | FollowUp-Phase plant neu und wendet den vollständigen Pfadsatz erneut an |

Offen (klärt die Entwicklungssession, wenn es ansteht): Port der HTTP-API; Lizenz der Markendateien (MIT
deckt Code, Logo ggf. ausnehmen – vor M6 klären).

Entschieden für M4.5 Markenauftritt (Nutzer, 2026-09-13), Umsetzung Abschnitt 4.8:
- **Tray-Icon zeigt weiter das aktive Profil**, jetzt als Brand-Profilsymbol in Taskleistenfarbe; das RigShift-Logo
  nur ohne erkanntes Profil bzw. während des Wechsels. Begründung: der Status auf einen Blick bleibt erhalten; die
  Markenregel „Icon ist kein Status-Text" betrifft Slogans, nicht Symbole.
- **Fluent + Marken-Akzent statt voller Markenpalette:** WPF-UI folgt weiter Windows (hell/dunkel/Kontrast), die
  Marke kommt über Akzentblau, Symbole, Logo und Tokens. Begründung: kein Umbiegen der WPF-UI-Flächen, hoher
  Kontrast bleibt korrekt.
- **Nur das Tray-Popup folgt dem Mockup**, das Hauptfenster behält Profile/Diagnose/Einstellungen. Begründung: die
  Mockup-Seiten zeigen Funktionen, die es nicht gibt; das Paket sagt selbst, Mockups an den Funktionsumfang anzupassen.
- **Profilsymbole = Fluent System Icons statt Paket-Geometrien:** Desk `Desktop`, Rig `TopSpeed`, VR `HeadsetVr`,
  TV `Tv`, Streaming `Live`; Umriss, aktives Profil gefüllt, Tray immer gefüllt. Begründung: die Paketsymbole
  wirkten auf den Nutzer unmodern und unpassend; die Fluent-Symbole sind dieselbe Familie wie die übrigen
  App-Symbole und bei 16 px besser lesbar. Nicht alle Fluent-Namen sind in der WPF-UI-Schrift enthalten
  (`Desk`, `DesktopTower`, `LaptopPerson` fehlen) – neue Symbole vorher rendern.
- **Nur genutzte Dateien ins Repo**, das Paket bleibt lokal und gitignoriert. Begründung: 12 MB Rastergrößen und
  Originaltafeln gehören nicht in die ausführbare Datei und blähen das Repo.

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
