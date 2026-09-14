# RigShift

Windows-Tray-App, die einen Gaming-PC atomar zwischen Profilen (Bildschirm-Topologie + Audio) umschaltet –
erster Anwendungsfall Schreibtisch ↔ Sim Rig. Löst `legacy/DisplayProfile.ps1` ab. Ziel: freies Community-Tool.

**Der Plan ist `docs/PLAN.md`** (Deutsch, verbindlich, alle Entscheidungen mit Begründung). Vor jeder
Architekturfrage dort nachsehen, nicht neu entscheiden. Harte Regeln zur Anzeige-Topologie:
`docs/display-topology.md` – nie verletzen, nie neu lernen.

## Stack

.NET 10 LTS, `RigShift.slnx`, Central Package Management (`Directory.Packages.props`, Versionen exakt gepinnt).

| Projekt | Rolle |
|---|---|
| `src/RigShift.Core` (`net10.0`) | Profile, Planner, Orchestrator, Legacy-Parser – **kein Win32, kein UI**, voll testbar |
| `src/RigShift.Windows` | CCD-API, Core Audio, `IPolicyConfig`, Geräteereignisse – Interop über **CsWin32** (`NativeMethods.txt`) |
| `src/RigShift.App` | WPF + **WPF-UI 4.3**, H.NotifyIcon (Tray), CommunityToolkit.Mvvm, Generic Host, Serilog, System.CommandLine, Velopack |
| `tests/RigShift.Core.Tests` | xunit v3 + Shouldly + NSubstitute auf Microsoft.Testing.Platform |

Für C#-Code gilt der Skill **`dotnet-standards`**. Repo, Code, Commits und Community-Doku auf **Englisch**;
Plan und Gespräch Deutsch. App-UI de + en.

## Kommandos

```powershell
dotnet build RigShift.slnx
dotnet test --solution RigShift.slnx
```

`dotnet test` ohne `--solution` läuft auf .NET 10 in den VSTest-Modus und scheitert – der MTP-Runner ist in
`global.json` (`test.runner`) eingeschaltet.

## Stand

Meilensteine **M0** (Skelett), **M1** (Core-Logik), **M2** (Windows-Schicht, JSON-Store), **M3** (Tray-App) und
**M4** (CLI, Einzelinstanz + Pipe, Profil speichern/bearbeiten), **M4.5** (Markenauftritt, Plan Abschnitt 4.8) und
**M5** (Hardwaretest am Gaming-PC) und **M6** (Release 1.0, Velopack, Auto-Update 1.0.0 → 1.0.1 belegt) fertig,
alle 2026-09-13. **1.1.0** (2026-09-14): Update-Funktionen U1–U4 in der App. Als Nächstes Roadmap v1.1 „Auslösen
und Steuern“ (`docs/PLAN.md`, Abschnitt 6).
Protokolle M5/M6: `docs/PLAN.md`, Abschnitt 5.

M5-Testaufbau (für Nachtests): hier `dotnet publish src/RigShift.App -c Release`, per `scp` nach
`C:\Users\manue\RigShift-M5` auf den Gaming-PC (`ssh -i C:\Users\Administrator\.ssh\gamingpc_ed25519 manue@192.168.178.31`,
laufende Instanz vorher mit `taskkill /im RigShift.exe /f` beenden); der User startet an der Konsole, Logs per SSH aus
`%APPDATA%\RigShift\logs` (ab 1.0; M5-Build noch `%LOCALAPPDATA%`). Über SSH ist die Anzeige nicht abfragbar
(`QueryDisplayConfig` → ACCESS_DENIED).

Release: Version in `Directory.Build.props` + Abschnitt `## [X.Y.Z]` in `CHANGELOG.md`, committen, `git tag -a vX.Y.Z`,
`git push origin vX.Y.Z` → `release.yml` baut und veröffentlicht (ca. 8 min). Lokaler Paket-Test ohne Upload:
`vpk pack` mit denselben Optionen wie im Workflow.

CLI-Prüfung am Server: nur `--help`, `list`, `status`, `save`, `apply <name> --dry-run` – **nie `apply` ohne
`--dry-run`**, auch keine erzeugte Verknüpfung starten.

App-Prüfung am Server ohne Umschalten: App starten, Screenshots per `PrintWindow` (Flag 2) des Fensters –
`CopyFromScreen` fängt verdeckende Fenster mit ein, und computer-use kennt die Dev-EXE nicht. Navigation per
UI-Automation-Fokus + Enter; bei getrennter RDP-Sitzung scheitert `SendKeys` („Der Vorgang wurde erfolgreich
beendet“, kein Eingabedesktop) → Enter per `PostMessage(hwnd, WM_KEYDOWN/WM_KEYUP, VK_RETURN)` ans Fenster schicken.
Update-Zustände live prüfen: aktuellen Code per `vpk pack --packVersion 1.0.0` packen und installieren, dann findet
die App das echte GitHub-Release; `onlyNotifyAboutUpdates: true` in `%AppData%\RigShift\settings.json` verhindert den
Download. Countdown-Dialog nur in
Debug-Builds über `RigShift.exe --preview-confirmation` erreichbar; Tray-Popup (mit aktivem Profil) und Tray-Icon-Bögen
für alle DPI-Stufen über `--preview-branding <ordner>`, Theme erzwingen mit `--preview-theme light|dark`.

Manuelle Prüfung der Windows-Schicht (nur lesend, ändert nichts):
`dotnet run --project tools/RigShift.Probe -- snapshot | audio | import <ordner> | plan <ordner> <profil>`.
Die Ausgabe enthält Gerätepfade und Endpoint-IDs – nicht ungekürzt veröffentlichen.

## Stolperfallen

- **Velopack-Setup leert einen vorhandenen `%LocalAppData%\RigShift`** (am Server 2026-09-13 belegt: alte Logs und
  Profile weg), die Deinstallation löscht ihn ganz. Daten deshalb nur in `%AppData%\RigShift`; nie etwas in den
  Installationsordner legen.
- Velopack-Auto-Apply startet die App mit den ursprünglichen Argumenten neu → für CLI-Aufrufe abgeschaltet
  (`Program.cs`), sonst ginge der Exit-Code verloren. `UpdateService` prüft nur, wenn `UpdateManager.IsInstalled`.

- **`Bestehend/` und `AUFTRAG_Entwicklung.md` sind gitignoriert** – sie enthalten private Gerätepfade,
  Endpoint-IDs und IPs. Für M2 sind die `.display`-/`.json`-Dateien dort die lokalen Testdaten; nie ins Repo,
  nie in Fixtures ohne Anonymisierung.
- Settings-JSON (Source-Generator, `init`-Properties): Property-Initialisierer wie `= true` greifen bei **fehlendem
  Schlüssel nicht** – der Wert wird `default`. Neue Einstellungen so benennen, dass `false`/`null` der gewünschte
  Standard ist (Test `Load_FileFromVersion1_0_…`).
- CsWin32: Konstanten wie `ERROR_GEN_FAILURE` nicht einzeln in `NativeMethods.txt` eintragen, sondern das
  Enum `WIN32_ERROR` (sonst PInvoke004).
- Analyzer laufen mit Warnungen als Fehler: Serilog-Sinks brauchen `formatProvider: CultureInfo.InvariantCulture`
  (CA1305); Testnamen mit Unterstrich sind nur im `tests/`-Ordner erlaubt (`tests/.editorconfig`).
- `RigShift.App` liefert `Main` selbst (`Program.cs`, `EnableDefaultApplicationDefinition=false`), weil
  `VelopackApp.Build().Run()` vor allem anderen laufen muss.
- Core-Tests mit Wartezeiten nutzen `tests/.../Fakes/AutoAdvanceTimeProvider` (Timer feuern sofort, Uhr springt
  vor) – keine echten Delays in Tests. xUnit1051 ist in `SwitchOrchestratorTests` per Pragma aus, weil
  NSubstitute-Aufrufe Token-Matcher statt echter Tokens übergeben.
- CsWin32-Formen nicht raten: generierten Code mit `dotnet build -p:EmitCompilerGeneratedFiles=true
  -p:CompilerGeneratedFilesOutputPath=<scratch>` ausgeben und nachsehen. Konstanten mit eigenem Enum
  (z. B. `DEVICE_STATE`) über den Enum-Namen eintragen.
- WPF-UI 4.3: `ui:TextBlock` direkt auf dem Seitenhintergrund wird im Dark-Theme zu dunkel gezeichnet →
  `Foreground="{DynamicResource TextFillColorPrimaryBrush}"` explizit setzen. Navigationspunkte haben kein
  UIA-Invoke-Muster, sind aber per Tastatur (Fokus + Enter) bedienbar. API-Namen aus den XML-Docs bzw. per
  `grep -a` in `Wpf.Ui.dll` prüfen (Symbol-Enums sind nicht dokumentiert).
- Sprache: `CultureInfo.CurrentUICulture` in einer async-Methode zu setzen wirkt nach dem `await` beim Aufrufer
  nicht mehr (Kultur fließt mit dem ExecutionContext). `Loc` hält die Kultur deshalb selbst (`Loc.Instance.Culture`);
  Formatierung im UI-Code immer damit, nicht mit `CurrentCulture`.
- `App.Exit()` kollidiert mit `Application.Exit` (CS0108) und `Exit` als Interface-Member mit CA1716 → `Quit()`.
- Dieser Server läuft per RDP: der Snapshot zeigt nur die RDP-Indirect-Display, Audio hat 0 Endpunkte.
  Apply und Audio-Umschalten sind hier nicht prüfbar (→ M5 am Gaming-PC).
- CLI-Ausgabe prüfen: `Start-Process -Wait` wartet auf den ganzen Prozessbaum, also auch auf die vom CLI
  gestartete Tray-App → hängt. Stattdessen `Process.Start` mit umgeleitetem stdout + `ReadToEnd()`. Aus demselben
  Grund startet der CLI-Prozess die Tray-App mit `UseShellExecute = true` (sonst erbt sie stdout).
- System.CommandLine lokalisiert Hilfe und Fehler nach `CurrentUICulture` schon beim Anlegen der Symbole →
  `CliParser.Parse` läuft komplett unter `InvariantCulture`.
- UI-Tests per UI-Automation: `InvokePattern.Invoke()` blockiert, solange ein dadurch geöffneter modaler Dialog
  offen ist → im Thread-Job auslösen. Modale Dialoge hängen im UIA-Baum unter ihrem Besitzerfenster.
- Der Rechner hier ist der Home-Server, nicht der Gaming-PC: **M5 (Hardwaretest) findet am Gaming-PC statt**;
  hier nur Build, Tests und Snapshot-Prüfungen mit den vorhandenen Bildschirmen.
