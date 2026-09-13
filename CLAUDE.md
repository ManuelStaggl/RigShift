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

Meilensteine **M0** (Skelett), **M1** (Core-Logik) und **M2** (Windows-Schicht, JSON-Store, Legacy-Import)
fertig. Als Nächstes **M3**: App (Tray, Profilliste, Countdown-Dialog, Einstellungen, Diagnoseseite).
Reihenfolge und Akzeptanzkriterien: `docs/PLAN.md`, Abschnitt 5.

Manuelle Prüfung der Windows-Schicht (nur lesend, ändert nichts):
`dotnet run --project tools/RigShift.Probe -- snapshot | audio | import <ordner> | plan <ordner> <profil>`.
Die Ausgabe enthält Gerätepfade und Endpoint-IDs – nicht ungekürzt veröffentlichen.

## Stolperfallen

- **`Bestehend/` und `AUFTRAG_Entwicklung.md` sind gitignoriert** – sie enthalten private Gerätepfade,
  Endpoint-IDs und IPs. Für M2 sind die `.display`-/`.json`-Dateien dort die lokalen Testdaten; nie ins Repo,
  nie in Fixtures ohne Anonymisierung.
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
- Dieser Server läuft per RDP: der Snapshot zeigt nur die RDP-Indirect-Display, Audio hat 0 Endpunkte.
  Apply und Audio-Umschalten sind hier nicht prüfbar (→ M5 am Gaming-PC).
- Der Rechner hier ist der Home-Server, nicht der Gaming-PC: **M5 (Hardwaretest) findet am Gaming-PC statt**;
  hier nur Build, Tests und Snapshot-Prüfungen mit den vorhandenen Bildschirmen.
