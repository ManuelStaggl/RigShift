# Umsetzungsplan nach der Analyse von RigShift 1.3.0

Stand: 2026-09-14 · Befunde: `befunde-1.3.md` (IDs darauf bezogen) · Für die Umsetzungs-Session. Regeln: CLAUDE.md,
Skill `dotnet-standards`, `docs/display-topology.md`. Am Server nie umschalten (siehe CLAUDE.md). Nach jedem Paket:
`dotnet build RigShift.slnx` 0 Warnungen, `dotnet test --solution RigShift.slnx` grün, CHANGELOG `[Unreleased]`.

**Vorgabe des Nutzers (2026-09-14): Jeder einzelne Punkt dieses Plans wird abgearbeitet – Paket 1 und Paket 2
vollständig, nichts wird „nach Gelegenheit“ liegen gelassen.** Die Pakete legen nur die Reihenfolge fest: Paket 1
vor dem Release 1.3.1, Paket 2 danach (die Punkte, die Ergebnisse der Hardware-Testrunde brauchen, sind dort
markiert; alle anderen Paket-2-Punkte können direkt nach 1.3.1 in derselben Umsetzungs-Session folgen). Der Abschnitt
„Nutzer entscheidet“ enthält Fragen, die dem Nutzer interaktiv mit Empfehlung gestellt werden; die Antwort wird dann
ebenfalls umgesetzt. Ein Punkt gilt erst als erledigt, wenn Akzeptanzkriterium und Tests erfüllt sind und er in
einer Abhak-Liste am Ende dieser Datei als erledigt eingetragen ist (Commit-Hash dazu).

## Paket 1 – vor der Hardware-Testrunde (Release 1.3.1)

Enthält alle Kritisch/Hoch mit Aufwand S/M sowie die Mittel-S-Punkte, ohne die die Testrunde nicht auswertbar wäre
(Logging, Velopack-Log) oder die den Test selbst verfälschen (Automatik-Zeit, Link-Bestätigung). Reihenfolge = Vorschlag.

### 1.1 Anruf-Absenkung persistent zurückstellen (B-01, Kritisch)
- Dateien: `src/RigShift.Core/Settings/AppSettings.cs`, `src/RigShift.Core/Topology/SwitchOrchestrator.cs`
  (`SwitchDucking`, `CaptureDucking`, `RestoreDucking`), `src/RigShift.App/App.xaml.cs` (`OnStartup`, neben
  `KeepAwakeForActiveProfile`), `src/RigShift.App/Services/AppServices.cs` (`SettingsService`).
- Vorgehen: `AppSettings` bekommt `DuckingBeforeProfiles` (`int?`) und `HasDuckingMemory` (`bool`), beide mit `set`.
  Der Orchestrator bekommt eine kleine Schnittstelle `IDuckingMemory` (Core) mit `Load()`/`Save(int?)`/`Clear()`;
  die App implementiert sie über `SettingsService`. `SwitchDucking` liest die Erinnerung aus der Schnittstelle statt
  aus dem Feld, schreibt sie beim ersten Profil mit Flag, löscht sie beim Zurückstellen. Beim Start: Erinnerung
  vorhanden und aktives Profil (oder keines) ohne Flag → Wert zurückschreiben, Erinnerung löschen, Info-Log.
- Akzeptanz: Ablauf „Rig (Flag) → Prozess beenden → Start → Desk aktiv“ stellt den Wert her; Neustart mit aktivem
  Rig lässt 3 stehen und stellt beim nächsten Desk-Wechsel her.
- Tests: `SwitchOrchestratorTests` mit `FakeDuckingPreference` und einem `InMemoryDuckingMemory`:
  `Switch_DuckingProfile_StoresMemory`, `NewOrchestrator_WithMemory_RestoresOnProfileWithoutFlag`,
  `Switch_Rejected_ClearsNothingButRestoresPreviousValue`; `JsonSettingsStoreTests.Load_FileWithoutDuckingKeys_HasNoMemory`.
- Risiko: gering; Settings-Schreibzugriff pro Wechsel (schon jetzt bei jedem Save).

### 1.2 Beenden und Abmelden während eines Wechsels (B-02, Hoch)
- Dateien: `App.xaml.cs` (`Quit`, `OnExit`, `SessionEnding`), `SwitchCoordinator.cs`, `IProfileSwitcher`.
- Vorgehen: `SwitchCoordinator` hält `Task? Current` und eine `CancellationTokenSource`; `RunAsync` reicht deren
  Token (kombiniert mit dem Aufrufer-Token) an den Orchestrator statt `CancellationToken.None`. `Quit()`: läuft ein
  Wechsel, Token canceln, bis 30 s auf `Current` warten (Dispatcher-Frame oder `async` mit `Shutdown` danach), dann
  beenden. `SessionEnding` gleich behandeln (`e.Cancel` nur, wenn der Rollback noch läuft). Countdown-Fenster:
  Schließen durch App-Ende soll nicht als „abgelehnt“ zählen, wenn der Token bereits gecancelt ist (im
  Orchestrator: bei `OperationCanceledException` nach Apply → Rollback ausführen, dann weiterwerfen).
- Akzeptanz: Debug-Test mit `--preview-confirmation` + Tray „Beenden“ während des Countdowns endet erst nach dem
  Ergebnis; Log zeigt „cancelled, rolled back“ statt „not confirmed (Rejected)“.
- Tests: `Switch_CancelledDuringConfirmation_RollsBackBeforeThrowing` (Core, Fake-Confirmation blockiert bis Cancel).
- Risiko: Hänger beim Beenden, wenn der Rollback selbst hängt → Zeitlimit 30 s, dann `Shutdown` trotzdem.

### 1.3 Automatik: Regelzustand erst nach Erfolg scharf (C-01, Hoch) + Entprellung + monotone Zeit (C-02, C-03)
- Dateien: `src/RigShift.Core/Automation/AutomationTrigger.cs`, `src/RigShift.App/Services/AutomationService.cs`,
  `SwitchCoordinator.cs` (Rückgabewert der Automatik-Überladung).
- Vorgehen: `Evaluate` nimmt `TimeSpan now` (aus `_time.GetTimestamp()`/`GetElapsedTime`) statt `DateTimeOffset`;
  `RunAsync` wertet `SwitchResult?` aus: `null`, `Blocked`, `Failed`, `RolledBack` → `_trigger.Unarm(rule)`
  (`IsRunning=false`, `StartedByRule=false`, `GoneSince=null`), Info-Log mit Grund; bei `Blocked` zusätzlich eine
  Sperrzeit von einer Frist (`ExitDelaySeconds`, mindestens 10 s), damit es nicht alle 2 s erneut versucht. Ende-Aktion
  frühestens beim zweiten aufeinanderfolgenden Poll ohne Gerät, unabhängig von der Frist. `SystemEvents.PowerModeChanged`
  (Resume) → `_trigger.Reset()`.
- Akzeptanz: Trigger-Tests unten grün; Log zeigt bei abgelehntem Countdown „rule disarmed“ und der nächste Poll mit
  verbundenem Gerät löst erneut aus.
- Tests (`AutomationTriggerTests`): `StartRejected_Unarm_NextPollStartsAgain`, `ExitDelayZero_SinglePollGap_DoesNothing`,
  `TwoRulesSameDevice_BothStart`, `NoActiveProfileAtStart_SwitchBackDoesNothing_Logged`,
  `RuleDisabledWhileRunning_NoSwitchBack` (mit O-07 entfallen), `Reset_DuringExitDelay_ClearsGoneSince`.
- Risiko: Verhaltensänderung der Frist bei 0 s (ein Poll später) – gewollt.

### 1.4 Profilseite und Status-Leiste (I-01, I-02, Hoch; I-06, I-03, I-08 Mittel)
- Dateien: `Views/Pages/ProfilesPage.xaml`, `Views/Pages/AutomationPage.xaml`, `Views/ProfileEditorWindow.xaml`,
  `ViewModels/ViewModels.cs` (`ShowStatus`), `Core/Profiles/ProfileEditing.cs` (Namenslänge).
- Vorgehen: InfoBar `IsOpen="{Binding IsStatusOpen, Mode=TwoWay}"`, `ShowStatus` setzt erst `false`, dann `true`.
  Profilseite: Kopfzeile als `WrapPanel` (Titel eigene Zeile, Knöpfe darunter, wenn schmal); Karte als Grid mit
  `*`-Spalte für Text (`TextTrimming="CharacterEllipsis"`, Badges in `Auto`-Spalten) und `Auto`-Spalte für Knöpfe;
  Name `MaxLength=60`, `Validate` lehnt längere ab. Regelkarte zweizeilig: Zeile 1 Schalter · Gerät · Aktualisieren ·
  Profil · Löschen, Zeile 2 Ende-Aktion · Frist · Ohne Bestätigung; Gerätename mit `ToolTip`. Editor: Hz + HDR in einer
  `WrapPanel`-Zeile, RadioButton/CheckBox/Löschen in `WrapPanel`, `Width="900"`.
- Akzeptanz: Screenshots bei 720, 784, 980 lg zeigen Titel, Name, Knöpfe und Regelkarte vollständig (Skript im
  Staffelstab); Statusmeldung erscheint nach Schließen mit X erneut.
- Tests: keine automatisierten; Screenshot-Prüfung wie in der Analyse.
- Risiko: Layout-Regressionen im Dunkel-Theme → beide Themes prüfen.

### 1.5 App-Testprojekt (L-01, Hoch) + Settings-Reihenfolge (F-01) + Fixtures (L-02) + Fake (L-03)
- Dateien: neu `tests/RigShift.App.Tests/` (net10.0-windows, xunit v3, in `RigShift.slnx` und CI), `AppServices.cs`
  (`SettingsService.UpdateAsync`: erst `SaveAsync`, dann `Current`), `tests/RigShift.Core.Tests/Fixtures/`,
  `Fakes/FakeDisplayConfigurator.cs`, `JsonSettingsStoreTests.cs`.
- Tests App: `SettingsServiceTests.Update_SaveThrows_KeepsPreviousCurrentAndRaisesNoChange`;
  `SwitchCoordinatorTests.Switch_WhileAnotherRuns_ReturnsNullAndRaisesBusyRejected`, `History_KeepsTenNewest`,
  `PartialResult_RemembersCatchUp_FailedKeepsIt_OtherClears`; `AutomationServiceTests.Tick_WhileSwitching_EvaluatesNothing`,
  `Tick_Paused_ResetsBaselineOnce` (Timer hinter `ITicker`); `CommandPipeServerTests.RoundTrip_ListOverRealPipe`
  (Pipe-Name parametrisieren); `DiagnosticsReportTests.Build_ContainsVersionRecentSwitchesAndDisplayError`.
- Tests Core: `Load_RuleWithoutExitDelay_UsesTenSeconds`; Fixture-Tests `Load_ProfileFixture1_0_LoadsWithAllDefaults`,
  `Load_ProfileFixture1_2_…`, `Load_SettingsFixture1_0_…` (jedes Feld asserten); Fake: Exception-Queue für
  Apply/Query, HDR-Ergebnis pro Display, Raten-Liste; `Switch_ApplyThrows_RestoresPreviousTopologyAndReportsFailed`
  (belegt B-07 nach dem Fix in 1.6), `Switch_HdrFailsOnOneDisplay_OthersStillSet`.
- Risiko: `UpdateService` braucht ein `IUpdateSource`-Wrapper (UpdateManager vermutlich nicht virtuell) – erst in
  Paket 2.

### 1.6 Umschalt-Kern: Wiederherstellung und Rückmeldung (B-05, B-06, B-07, B-08; Mittel/Niedrig S)
- Dateien: `SwitchOrchestrator.cs`, `SwitchResult.cs`, `AppServices.cs` (`SwitchMessages.ForNotification`),
  `Strings.resx`/`.de.resx`.
- Vorgehen: `SwitchResult.Note` (Enum `None | RestoredPrevious | RestoreFailed | ModesFromDatabase`) statt Freitext
  anhängen; Toast zeigt den Note-Text lokalisiert. `CatchUpAsync` merkt den `before`-Snapshot und ruft bei
  Fehlschlag `RestoreAfterFailureAsync`. `ApplyWithRetryAsync`/`PollTopologyAsync` in try/catch (`Win32Exception`,
  `COMException`) → wie Fehlschlag behandeln. Zyklus gilt als transient, wenn irgendein Versuch 31/1610 lieferte.
- Akzeptanz: Tests `Switch_TransientThenNonTransientInOneCycle_StillWaits` ([31, 87, 0] → Applied),
  `CatchUp_ApplyFails_LeavesDisplaysDark_RestoresPrevious`, `Switch_FailureAndNoPreviousDisplayAvailable_ReportsRestoreFailed`
  und der Apply-Throw-Test aus 1.5.

### 1.7 Logging für die Testrunde (K-01, K-02, K-03, K-04; F-03 Velopack-Log)
- Dateien: `SwitchOrchestrator.cs`, `CcdDisplayConfigurator.cs`, `AutomationTrigger.cs`/`AutomationService.cs`,
  `AppLogging.cs`, `Program.cs`, `UpdateService.cs`, `Directory.Packages.props` (+ `Serilog.Extensions.Logging`).
- Vorgehen: Dauer in „finished“/„rolled back“; `GetTimestamp` um Apply, Audio, HDR, Apps, Rettung (ms im Log);
  „confirmed after N s“; `Evaluate` liefert `IReadOnlyList<TriggerEvent>` (`DeviceConnected`, `DeviceGone(delay)`,
  `DeviceBack`, `ExitSkipped(reason)`, `Baseline`, `Disarmed`), Service loggt auf Information, „skipped, switch
  running“ ebenso; `fileSizeLimitBytes: 50 MB`, `rollOnFileSizeLimit: true`; `MinimumLevel.Override("Microsoft", Warning)`
  gegen die sechs Hosting-Zeilen; Velopack: `VelopackApp.Build().SetLogger(...)` und `new UpdateManager(source,
  logger: ...)` mit einem früh erzeugten File-Logger.
- Akzeptanz: ein Dry-Run-Log zeigt Plan, Dauer und Automatik-Ereignisse; Start ohne Hosting-Zeilen.

### 1.8 Sicherheit und Persistenz, klein (H-02, H-01, F-02, F-05)
- H-02: `SwitchRequest.FromLink` (Program.cs setzt es beim Link) → im Orchestrator `confirm = FromLink ||
  (confirmSeconds > 0 && !SkipConfirmation)`, Timeout bei 0 auf `SwitchOptions.DefaultConfirmTimeout`; README:91 und
  ein CHANGELOG-Satz unter `[Unreleased]` („Links always ask now“). Test `Switch_FromLink_TimeoutZero_StillConfirms`.
- H-01: `catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)` mit Retry-Log; Listener-Task
  mit `ContinueWith`-Fehlerlog; Pipe-Name `RigShift.<SessionId>` in `PipeProtocol` (Client identisch).
- F-02: `OnBeforeUninstallFastCallback` zusätzlich `RunKeyAutostart.Disable()`; README-Satz zum Datenordner.
- F-05: `JsonProfileStore.LoadAllAsync` öffnet mit `FileShare.ReadWrite | FileShare.Delete`, ein Retry nach 100 ms,
  liefert die übersprungenen Dateien (`LoadResult`); Profilseite zeigt InfoBar „n Dateien nicht lesbar: …“;
  `CommandRunner.SaveAsync` bricht mit Exit 1 ab, wenn eine Datei nicht lesbar war und der Name nicht gefunden wurde.
  Tests `Load_LockedFile_IsReportedNotSkippedSilently`, `Save_WhenLoadIncomplete_DoesNotCreateDuplicate`.

### 1.9 Doku-Korrekturen vor dem Test (N-01…N-04, N-02 inkl. Code-Kommentare A-06, A-01 Doku-Teil)
- README:91, CHANGELOG 1.2.0-Fußnote, `docs/PLAN.md` 4.1/4.2/4.7 (Listener, `%AppData%`, Diagnoseseite),
  `docs/ARCHITECTURE.md` (Projekte, Core concepts, Interfaces-Tabelle inkl. `IAppLauncher`, `IUsbDeviceList`,
  `IUsbPowerCheck`, `IWindowRescuer`, `IDuckingPreference`, `DisplayChangeWatcher`; Exit-Code 5; „no time limit“),
  `docs/ROADMAP.md` (🟡, Dry-Run streichen), drei Code-Kommentare `%LocalAppData%`, vier Verweise „section 10, M4“ →
  „section 5, M4“, CLAUDE.md (Probe-Befehle, Reihenfolge, Windows.Tests, Pragma auch in `CommandRunnerTests`).

### 1.10 Release 1.3.1
- CHANGELOG `[1.3.1]` mit Ko-fi-Knopf (aus `[Unreleased]`), M-04 (Prüfung `[Unreleased]` leer) gleich mitnehmen,
  M-01 (Publish-Schritt in `ci.yml`). Version in `Directory.Build.props`, Tag, Push (CLAUDE.md „Release“).

## Paket 2 – nach der Testrunde

| Punkt | IDs | Dateien | Vorgehen | Akzeptanz/Tests |
|---|---|---|---|---|
| Apps-Nachlauf außerhalb des Gates | B-03 | `SwitchOrchestrator.cs`, `SwitchCoordinator.cs`, `SwitchResult.cs` | `SwitchAsync` liefert das Ergebnis vor `RunAppsAsync`; Apps als `Task` mit eigener CTS, die ein neuer Wechsel abbricht; `IsSwitching` fällt mit dem Ergebnis, `AppsOutcome` per Event nachgereicht | `Switch_AppsWaitForDevice_NewSwitchCancelsWait`; Hotkey während Wartezeit meldet nicht „Busy“ |
| Fensterrettung robust | B-04, B-14, B-12 | `WindowRescuer.cs`, `NativeMethods.txt` (`IsHungAppWindow`), `SwitchOrchestrator.cs` | hängende Fenster überspringen, 3-s-Limit; Rettung erst nach Bestätigung; HDR bei `null` nach 1 s erneut | Testrunde belegt Zeit und Verhalten |
| Zeitbudget und Zwillinge | B-09, B-10, B-11 | `SwitchOrchestrator.cs`, `TopologyPlanner.cs` | Budget nach Vor-Warten neu; Zwillings-EDID mit einem Kandidaten → `AmbiguousTwin`-Warnung; nach Datenbank-Modi Nach-Snapshot vergleichen → `Note.ModesFromDatabase`/`AppliedPartially` | `Switch_DisplayWakesLate_ThenError31_HasFreshRetryBudget`, `Plan_TwinMonitors_OneMissingOneMoved_DoesNotGuess`, `Plan_SameMonitorOnStaleAndLiveTarget_PrefersAvailable` |
| Dry-Run ohne Gate | B-13 | `SwitchCoordinator.CheckAsync` | Query + Planner direkt, ohne `_gate`/`IsSwitching` | Hotkey während „Prüfen“ funktioniert |
| Stromspar-Prüfung | C-04, N-06 | `UsbPowerCheck.cs`, `UsbPowerSaving.cs`, `docs/usb-power-saving.md` | nur verbundene Instanzen; `WDF\IdleInWorkingState`/`UserSetDeviceIdleEnabled` lesen; `SecurityException` als Warning; Doku-Sätze | Ergebnis der Testrunde (welcher Wert kippt) fließt ein; `UsbPowerSavingTests` für neue Flags, `IsFlagEnabled` mit `byte[]{1,1}` |
| Automatik-Kleinkram | C-05, C-06, A-07 | `AutomationViewModel.cs`, `AutomationTrigger.cs`, `SwitchCoordinator.cs`, `AutomationService.cs` | Duplikat-Hinweis in der Karte; Info-Log bei fehlendem Vorprofil; `RefreshActiveAsync` im Koordinator awaiten; `SetPausedAsync` mit try/catch | App-Tests |
| Umschalt-Feedback im Fenster | I-04, I-07 | `DesktopServices.cs`, `ViewModels.cs`, `App.xaml.cs` | `SwitchCompleted` → `ShowStatus`; Ballon-Klick bei Failed/Blocked → Über & Hilfe; „Wechsle …“-Zeile; `DispatcherUnhandledException` → `Status_Error`-Ballon | Screenshot |
| Tray-Popup, Editor, Dialoge | I-05, I-09…I-12, I-15, I-16 | `TrayPopupView.xaml`, `SettingsPage.xaml`, `AboutPage.xaml`, `ProfileEditorWindow.xaml(.cs)`, `ProfileEditorViewModel.cs`, `ProfileDialogs.cs`, `AutomationViewModel.cs` | `ScrollViewer MaxHeight`; `TextWrapping` in Card-Headern; `IsDefault` auf Speichern; Dirty-Flag + Rückfrage; Regel-Löschen mit Rückfrage; Hinweis an „Ohne Bestätigung“; Danger-Knopf + Owner; `LostFocus` | Screenshots bei 720 lg |
| Sprache und Texte | I-13, I-14, I-17, J-01…J-03 | `Loc.cs`, ViewModels, `HotkeyFormat.cs`, Pages, resx | `Loc.PropertyChanged` in VMs; `GetKeyNameText`; Formatkultur getrennt von UI-Kultur; `Tray_Active` streichen; `Page.Title` per `Tr`; Typografie-Liste aus J-03 | Sprachwechsel ohne Neustart vollständig |
| Interop-Kleinkram | G-01…G-04, H-04, H-05 | `PolicyConfigAudioController.cs`, `CcdNative.cs`, `ProcessAppLauncher.cs`, `ViewModels.cs`, `CommandPipeServer.cs`, `AboutPage.xaml`, README | `ReleaseComObject` in `finally`; HDR-Fallback nur bei fehlgeschlagener `_2`-Abfrage, beide Codes loggen; Pfadvergleich beim Stop; Backslash-Escaping; 10-s-Lese-Timeout; Hinweis unter „Diagnose kopieren“ | `Probe audio` weiter grün; Testrunde für HDR |
| Aufräumen | A-01 (Interop), A-02, A-04, A-05, A-08, K-05, L-05, M-02, M-03 | `NativeMethods.txt`, `RefreshRate`-Aufrufer, Automation, `.editorconfig`, `Program.cs`, `App.xaml.cs`, `ci.yml`, `global.json` | Einträge streichen; `RefreshRate.Hertz`; `usb:`-Präfix und Schlüssellisten entfernen; CA1848-Zeile weg; `dotnet format` grün + Check in CI; `ServiceCollection` statt `Host`; `latestPatch`; SHA-Pins; `timeout-minutes`, `concurrency`; Tests auf Bereiche statt exakter Versuchszahl | Build 0 Warnungen, `dotnet format --verify-no-changes` grün |
| Struktur | A-03 | `SwitchOrchestrator.cs` → `AudioSwitcher`, `AppRunner`, `DuckingSwitcher`; `ViewModels.cs` → Datei pro Typ | reine Verschiebung, Tests unverändert | 241+ Tests grün |
| Windows-Schicht-Fixture | L-04 | `CcdDisplayConfigurator.cs` (Snapshot-Aufbau als reine Funktion), `tests/RigShift.Windows.Tests/Fixtures/` | einmal am Gaming-PC Rohdaten (`Probe snapshot --raw`) anonymisiert ablegen | Snapshot-Tests ohne Hardware |
| `SECURITY.md` | H-03 | `SECURITY.md` | Update-Kette und Restrisiko benennen | – |

## Nicht umsetzen / Nutzer entscheidet

**Antworten des Nutzers (2026-09-14, Umsetzungs-Session):** D-02 → (b) `PublishSingleFile=false`; O-01 → ja, Häkchen
„Ohne Nachfrage umschalten“; O-02 → ja; O-03 → streichen; O-04 → ja, feste 30 s; O-05 → ja, nur „Bildschirme“;
O-06 → ja; O-07 → **Schalter „Regel an“ streichen** (entgegen der Empfehlung); O-08 → beide behalten, Standard für neue
Regeln „Zu Profil X“. Umsetzung nach Paket 1 (nach dem Release 1.3.1), zusammen mit Paket 2.

- **D-02 Paketgröße (Empfehlung: Variante b).** Frage: Bleibt es bei self-contained + Single-File (Setup 98 MB, Delta
  35 MB), oder (b) `PublishSingleFile=false` (gleiche Größe, aber deutlich kleinere Deltas, kein Selbstextrahieren
  beim Start), oder (c) framework-dependent (Setup ~5 MB, Velopack installiert die .NET-10-Desktop-Runtime nach)?
  Empfehlung (b): ein Release-Zyklus Aufwand, kein Risiko für Community-Nutzer ohne Runtime. Rückweg: Property zurück.
- **O-01 Bestätigungssekunden pro Profil (Empfehlung: ja, vereinfachen).** Frage: Profil-eigene Sekunden durch ein
  Häkchen „Ohne Nachfrage umschalten“ ersetzen, Sekunden nur global? Begründung: die einzige profilspezifische
  Entscheidung ist „fragen oder nicht“; der Erklärtext im Editor entfällt. Rückweg: Feld bleibt im Modell.
- **O-02 Kommunikations-Audio-Slots (Empfehlung: ja).** In einen zugeklappten Bereich „Erweitert“; Standard „wie
  Wiedergabe/Aufnahme“ macht der Code ohnehin.
- **O-03 „Standardprofil beim Start anwenden“ (Empfehlung: streichen, falls ungenutzt).** Zweiter Automatik-Pfad, der
  beim Anmelden mit einer USB-Regel kollidieren kann; Windows stellt die Desk-Anordnung nach dem Neustart selbst her.
- **O-04 Höchstwartezeit „Apps warten auf Gerät“ (Empfehlung: Feld aus dem Editor, feste 30 s).** Niemand kennt einen
  besseren Wert; Modell bleibt.
- **O-05 Monitorname im Editor und auf „Bildschirme“ (Empfehlung: nur „Bildschirme“).** Zwei Eingabeorte für eine
  globale Eigenschaft; der Editor zeigt „Name · Modell“ weiterhin.
- **O-06 „Neu laden“ und Karte „Ordner“ (Empfehlung: ja).** „Neu laden“ streichen (Reload nach CLI-`save` passiert
  ohnehin), „Profilordner öffnen“ zu „Log-Ordner öffnen“ nach Über & Hilfe; die Einstellungen enthalten dann nur
  Einstellungen.
- **O-07 Schalter „Regel an“ je Regel (Empfehlung: weglassen, schwach).** Pausieren und Löschen reichen bei einer
  Regel; bei mehreren Regeln kleiner Nutzen.
- **O-08 Ende-Aktion „Zurück zum vorherigen“ (Frage).** „Zu Profil X“ ist deterministisch; „zurück zum vorherigen“
  hängt vom Zufall ab (TV-Profil?). Empfehlung: beide behalten, Standard für neue Regeln auf „Zu Profil X = Desk“.
- **A-09 synchrone Windows-Aufrufe hinter `Task.FromResult`:** nicht umsetzen, funktioniert; nur dokumentieren.
- **C-07 baugleiche Geräte, C-08 Poll-Doppelarbeit, F-06 Last-Writer-Wins, F-07 Downgrade, D-03 Working Set nach
  50 Saves:** nur Doku-Hinweis bzw. beobachten; gemessen unerheblich.
- **I-18 UIA-ComboBoxen:** nur prüfen, wenn Screenreader-Nutzung ein Thema wird.
- **H-03 Code-Signing:** bleibt gestrichen (Nutzerentscheidung), nur `SECURITY.md`.

## In die Hardware-Testrunde aufnehmen (Gaming-PC, Nutzer an der Konsole, Log per SSH)

Voraussetzung: 1.3.1 mit Paket 1 installiert (Auto-Update 1.3.0 → 1.3.1 ist der erste Prüfschritt, F-03-Log dabei
prüfen: `%AppData%\RigShift\logs` muss die Velopack-Zeilen enthalten).

| Nr. | Was | Prüfschritt | Erwartung / Befund-ID |
|---|---|---|---|
| 1 | Auto-Update 1.3.0 → 1.3.1 | App läuft, 24 h abwarten oder „Nach Updates suchen“, Neustart | Update installiert; Velopack-Zeilen im Log (F-03) |
| 2 | Desk ↔ Rig mit Dauer im Log | je zweimal, einmal G9 im Standby | Log zeigt Plan, Versuche mit Code und ms, Dauer gesamt (K-01); [31, 87]-Folge falls sie auftritt wartet (B-08); Vor-Warten + 31 hat Budget (B-09) |
| 3 | Ducking-Persistenz | Rig (Flag) aktiv → `taskkill /im RigShift.exe /f` → Start → Desk | `UserDuckingPreference` wieder Vorher-Wert (B-01); in Discord hörbar |
| 4 | Beenden im Countdown | Rig umschalten, im Countdown Tray „Beenden“ | Rollback vollständig, danach beendet (B-02) |
| 5 | USB-Trigger Ein/Aus mit Frist | Wheelbase an → Rig; aus → nach Frist Desk; kurz aus/an innerhalb Frist → nichts | Log zeigt jede Entscheidung (K-02); Countdown ablehnen → Wheelbase aus/an löst wieder aus (C-01) |
| 6 | Standby mit Wheelbase aus | Wheelbase aus, PC innerhalb der Frist in Standby, aufwecken | kein Rückschalten beim Aufwachen, Baseline neu (C-03) |
| 7 | Stromspar-Warnung | `Probe usb-power <VID&PID>` vor/nach dem Kästchen im Geräte-Manager, für Wheelbase und Pedale | welcher Wert kippt (C-04); Warnung verschwindet, wenn alle Ports sauber |
| 8 | Fensterrettung | Discord/Steam/SimHub auf CM27X3 links, Rig umschalten | Fenster auf G9, Zustand erhalten; Zeitbedarf im Log (B-04, B-14); nach Ablehnen: wo liegen sie? |
| 9 | Apps warten auf Wheelbase | Rig-Profil mit Wartezeit, Wheelbase erst nach 10 s einschalten; einmal gar nicht | Apps starten beim Erscheinen; Toast „Gerät nicht erkannt – Apps gestartet“; Hotkey währenddessen (B-03: Busy-Toast bis Paket 2) |
| 10 | HDR + Hz | Rig mit HDR an/aus, 240 Hz; Desk 165 Hz | Log der HDR-Aufrufe: `_2` oder Fallback, Codes (G-02); HDR unmittelbar nach Apply gemeldet? (B-12) |
| 11 | Nachholen spacedesk | Rig ohne spacedesk, dann Viewer verbinden; einmal Viewer während der 3-s-Bus-Lücke | Nachholen wendet an; bei Fehlschlag Wiederherstellung (B-06) |
| 12 | Link mit Bestätigung aus | Schalter „Nach dem Umschalten bestätigen“ aus, `rigshift://apply/Rig` aus dem Browser | fragt trotzdem (H-02) |
| 13 | Keep-awake | Rig aktiv, Bildschirm-Timeout 1 min, 3 min warten; dann Desk | kein Abschalten; nach Desk wieder normal; nach Absturz + Start erneut gesetzt |
| 14 | Anruf-Absenkung hörbar | Discord-Anruf im Rig | Spiel bleibt laut; Desk stellt Absenkung her |
| 15 | Hotkeys + Doppeldruck | Strg+Alt+F1 im Spiel; nochmal im Countdown | bestätigt; Konfliktmeldung, falls belegt |
| 16 | Portable-Variante (einmalig) | ZIP entpacken, starten, Update-Prüfung | Updates laufen im Portable-Ordner? Daten in `%AppData%` (F, Vermutung) |
| 17 | Zweites Benutzerkonto (optional) | RigShift läuft als User A, User B startet es | Log von B zeigt Pipe-Warnung statt still (H-01) |
| 18 | Snapshot-Rohdaten (einmalig) | `Probe snapshot` mit Rohausgabe für die Fixture | L-04 |
| 17 | Zweites Benutzerkonto: vom Nutzer abgehakt ohne Test |
| 18 | Snapshot-Rohdaten (L-04): vom Nutzer abgehakt; Probe liegt unter `C:UsersmanueRigShift-Probe` am Gaming-PC, L-04 bleibt offen |
| 19 | Diagnosebericht | „Diagnose-Infos kopieren“ im Rig | Inhalt prüfen, keine Endpoint-IDs/Benutzername (H-05) |
| 20 | 2× CM27X3 (B-10): nicht getestet, Testrunde vom Nutzer beendet |
| Zusatz | Tray-Kontextmenü, Update-Karte „Was ist neu“, R-01 (RDP-Trennung), echte Screenshots: nicht geprüft, Testrunde vom Nutzer beendet (2026-09-14) |
| 20 | 2× CM27X3 | linken CM27X3 abstecken, Desk umschalten | Planner blockiert korrekt statt zu raten (B-10, ab Paket 2) |

## Abhak-Liste (von der Umsetzungs-Session zu pflegen)

| Punkt | Befund-IDs | Status | Commit |
|---|---|---|---|
| 1.1 Anruf-Absenkung persistent | B-01 | erledigt (F-01 gleich mit: `SettingsService` speichert vor `Current`, Updates serialisiert) | 56bbd16 |
| 1.2 Beenden/Abmelden während Wechsel | B-02 | erledigt (`SessionEnding` verweigert einmal und beendet nach dem Rollback; blockierendes Warten würde den Countdown-Dispatcher verklemmen) | f4bfb62 |
| 1.3 Automatik-Regelzustand, Entprellung, monotone Zeit | C-01, C-02, C-03 | erledigt; **Abweichung:** nur `null`/`Blocked` versuchen nach der Frist (≥ 10 s) erneut, `RolledBack`/`Failed` erst nach Aus-/Einschalten (auch innerhalb der Frist) – sonst käme der abgelehnte Countdown alle 17 s wieder. K-02 (Ereignisliste) gleich mit | 445304e |
| 1.4 Profilseite, InfoBar, Regelkarte, Editor-Layout | I-01, I-02, I-03, I-06, I-08 | erledigt, Screenshots 720/784/980 lg de-hell + en-dunkel geprüft, InfoBar erscheint nach X erneut | 8b73ae2 |
| 1.5 App-Testprojekt, Settings-Reihenfolge, Fixtures, Fake | L-01, F-01, L-02, L-03 | erledigt (11 App-Tests; statt `ITicker` ein internes `PollAsync`) | d74991d |
| 1.6 Wiederherstellung und Rückmeldung | B-05, B-06, B-07, B-08 | erledigt | 8ab5306 |
| 1.7 Logging und Velopack-Log | K-01, K-02, K-03, K-04, F-03 | erledigt; Velopack 1.2.0 hat kein `UpdateManager(logger:)` – `VelopackApp.SetLogger` mit früh erzeugtem Serilog-Logger reicht (UpdateManager nutzt den Locator), Start-Log geprüft: Velopack-Zeilen da, keine Hosting-Zeilen. Dry-Run-Log geprüft: Plan + Dauer (Zeile „Dry run … finished in N ms“ nachgetragen); Automatik-Ereignisse am Server nicht auslösbar, per Tests belegt | fdd3053, 9b5bdbc |
| 1.8 Link-Bestätigung, Pipe, Run-Key, gesperrte Dateien | H-02, H-01, F-02, F-05 | erledigt; **Abweichungen:** `FromLink` reist als verstecktes `--from-link` im Argument (Pipe trägt nur Argumente), Link schlägt auch `--no-confirm`; `RunKeyAutostart.Disable()` entfernt den Wert unabhängig vom Pfad (auch Portable-Autostart); Pipe-Retry alle 2 s; kaputte/neuere Dateien zählen auch als nicht lesbar und blockieren `save`; InfoBar nicht schließbar; Retry-Test wartet echte 100 ms (Store hat keinen `TimeProvider`) | ac6d7f9, 69363db, ae46088 |
| 1.9 Doku-Korrekturen | N-01, N-02, N-03, N-04, A-06, A-01 (Doku) | erledigt; „section 10, M4“-Verweise sind korrekt (Abschnitt 10 = „Entschieden in M4“), nur zwei statt vier – unverändert; Probe-Usage-Kommentar nachgezogen | 06eb33d, (Release-Commit) |
| 1.10 Release 1.3.1 | M-01, M-04 | erledigt (M-04 erlaubt ±1 Tag Abweichung zwischen Überschrift und UTC) | b951d58, Tag v1.3.1 |
| 2 Apps-Nachlauf außerhalb des Gates | B-03 | erledigt; **Abweichungen:** `AppsOutcome.Pending/Cancelled`, `SwitchResult.AppsCompletion` + `SwitchCoordinator.AppsCompleted` (zweiter Toast nur bei unvollständigen Apps/fehlendem Gerät); Abbruch im Orchestrator (`CancelPendingAppsAsync`); CLI `apply` wartet nicht mehr auf Apps; `StopAsync` bricht Apps ab | 571ca3f |
| 2 Fensterrettung robust | B-04, B-14, B-12 | Code erledigt, Beleg in der Testrunde (Tests 8, 10); zusätzlich 250-ms-Probe per `SendMessageTimeout(WM_NULL)`; Rettung nach Bestätigung, nach Nachholen und nach Wiederherstellung; HDR-Wiederholung im Orchestrator (sonst kostet jede Abfrage ohne HDR 1 s) | 471eb2e, 773463b |
| 2 Zeitbudget und Zwillinge | B-09, B-10, B-11 | erledigt; B-10 greift nur innerhalb des Profils (Zwilling außerhalb des Profils ist vom Portwechsel nicht unterscheidbar); B-11: dunkler Bildschirm → `AppliedPartially`, `ModesFromDatabase` nur bei echter Modus-Abweichung (auch beim Nachholen) | aeb7620, 571ca3f |
| 2 Dry-Run ohne Gate | B-13 | erledigt; gilt auch für CLI `apply --dry-run` | 571ca3f |
| 2 Stromspar-Prüfung | C-04, N-06 | Code erledigt, Beleg in der Testrunde (Test 7): welches WDF-Flag beim Häkchen kippt, ist offen; `byte[]{1,1}` gilt als „aus“ (Zahl 257); Liste nicht lesbar → alle Instanzen mit Warning | 80f204a |
| 2 Automatik-Kleinkram | C-05, C-06, A-07 | erledigt; `SetPausedAsync` meldet Fehler an die Seite und stellt den Schalter zurück | 6d57df3, 4e8566a, 5e87c42, 571ca3f |
| 2 Umschalt-Feedback im Fenster | I-04, I-07 | erledigt; „Wechsle …“-Zeile ohne Profilname; UI-Fehler-Ballon höchstens alle 30 s | 101530d |
| 2 Tray-Popup, Editor, Dialoge | I-05, I-09, I-10, I-11, I-12, I-15, I-16 | I-05, I-09, I-10, I-11, I-15, I-16 erledigt; „Erweitert“ startet offen, wenn ein Anruf-Gerät gesetzt ist; I-12 (Regel löschen mit Rückfrage, Hinweis an „Ohne Bestätigung“) in Welle 2 erledigt | 9e47c07, ec16d2a, f52cfc0, 501f677 |
| 2 Sprache und Texte | I-13, I-14, I-17, J-01, J-02, J-03 | erledigt; I-13 Rest (Automatik-Listen, offener Editor) und I-17 (Zugriffstasten der Hauptknöpfe) in Welle 2; J-03-Reste (du-Form, „…“) geprüft, nichts offen; Akzentfarbe exakt nach Plan 4.8 | 9e47c07, 6fe3b57, a02d360, 3219739, 46509a5, cecece5 |
| 2 Interop-Kleinkram | G-01, G-02, G-03, G-04, H-04, H-05 | erledigt (G-02, H-05, G-03 mit Admin-App: Beleg in der Testrunde); G-02: Fehler von `SET_HDR_STATE` wird zurückgegeben statt still auszuweichen; G-03 gilt auch für `IsRunning`; H-05: Hinweis stand schon auf der Seite, Text ergänzt, Benutzerordner in Pfaden des Berichts ersetzt | e02387a, bd1ff9e, be32930, 5c3f88d, ab7454e, d39e4db |
| 2 Aufräumen | A-01, A-02, A-04, A-05, A-08, K-05, L-05, M-02, M-03 | erledigt; **Abweichungen:** A-05 nur `ServiceCollection` statt Generic Host (Hosting-Pakete entfernt) – „ungenutzte Member/doppelte Konstanten“ sind im Befund nicht einzeln belegt, daher nicht umgesetzt; K-05 gibt es in den Befunden nicht; A-08 als `UsbDeviceChoices` zusammen mit U-01; `dotnet format --verify-no-changes` als CI-Schritt | 40be858, 54c31ed, 4bce6ed, 84fca4b, c2f42c0, f5bb118, 881835e, d106470, ab587ce, 7502779 |
| 2 Struktur | A-03 | erledigt; `AudioSwitcher`, `AppRunner`, `DuckingSwitcher` intern, von `SwitchOrchestrator` im Konstruktor gebaut (öffentliche API, DI und Tests unverändert, Log-Kontext bleibt `SwitchOrchestrator`); Keep-awake, Fensterrettung, HDR und Rollback bleiben im Orchestrator; `ViewModels.cs` → eine Datei pro Typ | 09b1423, cb765db |
| 2 Windows-Schicht-Fixture | L-04 | offen (Rohdaten aus Testrunde) | |
| 2 SECURITY.md | H-03 | erledigt (Datei gab es schon, Abschnitt Update-Kette ergänzt) | 785c5c1 |
| 2 Nutzerbefund Testrunde 2026-09-14: Mausrad scrollt nur am rechten Rand, Release-Notes unförmig, Update-Knöpfe mittig neben dem Text | T-01, T-02, T-03 | erledigt; Ursache T-01: WPF-UI setzt `CanContentScroll=True` je Page und legt einen eigenen ScrollViewer darum – Seiten setzen `CanContentScroll=False`, `Controls/WheelScrolling` reicht das Rad an den äußeren ScrollViewer weiter; Release-Notes über `ReleaseNotes.Parse` in zugeklapptem „Was ist neu“ (max. 200 px); Knöpfe in eigener Zeile. Die Update-Karte mit Versionshinweisen erscheint nur in der installierten App – Beleg in der Hardware-Testrunde mit 1.4.0 | 02ca755, 6e88ecf, b10b22d |
| 2 Nutzerwunsch 2026-09-14: eigene USB-Gerätenamen (einmal vergeben, überall: Regeln, „Apps warten auf Gerät“, Toasts; wie Monitornamen) und Regeln mit mehreren Geräten („alle müssen da sein“; Ende, sobald eines nach der Wartezeit fehlt) – beides Nutzerentscheidung mit Empfehlung | U-01, U-02 | erledigt; Namen in `AppSettings.UsbDeviceNames`, Liste „USB-Gerätenamen“ auf der Automatik-Seite zeigt nur Geräte aus Regeln, Profilen und bereits benannte (nicht jeden Hub); Regeln speichern `devices` statt `usbDeviceId`, 1.3-Regeln werden beim Laden migriert. **Abweichung:** Orchestrator-Log und CLI-Ausgabe zum wartenden App-Gerät nennen weiter den Windows-Namen (Core kennt die Einstellungen nicht); Toast, Automatik-Log und Regeln nutzen den eigenen Namen. Gleiche Geräte-Menge in zwei Regeln warnt, ein Einzelgerät, das auch in einer Kombination steckt, nicht | 7502779, 59ad1d1 |
| 2 Beobachtung Update-Log Gaming-PC: nach RDP-Trennung Theme 2× und Tray-Icon 7× neu gesetzt | R-01 | erledigt; gleiches Theme wird übersprungen (Hochkontrast immer angewandt), Tray-Icon nur bei geänderter Kombination aus Symbol, Größe, Taskleistenfarbe | c101afd |
| 2 Nutzerwunsch: vor 1.4.0 alle Seiten, Menüs, Dialoge visuell prüfen (de/en, hell/dunkel, schmal/breit) | V-01 | erledigt am Server (de dunkel/en hell breit, de hell/en dunkel 720 px, jede Seite ganz durchgescrollt, Editor, Countdown, Tray-Popup, Verwerfen-Dialog); Befunde behoben: Automatik mit einem Aktualisieren-Knopf im Kopf, Namensfelder unter dem Text, Profil ohne Symbol ohne Lücke, schmaleres Sekundenfeld, `--preview-theme light` wurde vom Theme-Watcher überschrieben. **Nicht am Server prüfbar:** Tray-Kontextmenü (Rechtsklick) und Update-Karte der installierten App → Hardware-Testrunde | 59ad1d1 |
| Nutzerfragen beantwortet und umgesetzt | D-02, O-01…O-08, A-09, C-07, C-08, F-06, F-07, D-03, I-18 | erledigt: D-02 (Ordner statt Single-File, Publish 203 MB/304 Dateien, `--help` ok; Release 1.4.0: Delta 80,7 MB bei 82,5 MB voll – beim Wechsel von Single-File auf Ordner erwartbar fast voll, erst das Delta ab 1.4.0 zeigt den Gewinn), O-01/O-02/O-04/O-05 (Editor; O-01 migriert alte Zeit 0 → „ohne Nachfrage“), O-03, O-06, O-07 (ausgeschaltete alte Regeln werden beim Laden mit Warning verworfen), O-08; A-09, C-07, C-08, D-03, F-06, F-07, I-18 als „Known limitations“ in `docs/ARCHITECTURE.md`, C-07 zusätzlich README | 366048a, ec16d2a, 448b80e, 89ac95f, dc62609, 471eef6, a45aa62 |

## Hardware-Testrunde 1.4.0 (Gaming-PC, 2026-09-14)

| Nr. | Ergebnis |
|---|---|
| 1 | Auto-Update 1.3.1 → 1.4.0 per Delta, Velopack-Zeilen im Log, Neustart als 1.4.0 – ok. Migration `usbDeviceId` → `devices` real nicht prüfbar (Regel schon unter 1.3.1 gelöscht), nur Unit-Tests |
| 2 | Desk 1 Versuch 2,6/2,7 s; Rig (G9 beide Male im Standby) 5 Versuche 31,31 → G9 weg → 31,1610 → Erfolg, 6,9/7,8 s – ok (B-08/B-09 greifen). mit eingeschaltetem G9 identische Folge (G9-Handshake immer) |
| 3 | Kill bei Rig → Start: Rig aktiv, Wach halten neu gesetzt; Desk löscht Erinnerung (`hasDuckingMemory: false`) – ok. Nicht hörbar: Vorher-Wert = aktueller Wert (3) |
| 4 | Beenden im Countdown: „cancelled during confirmation, rolling back“, Desk nach 1 Versuch zurück, Gesamt 13,6 s, Exit-Code 0 – ok (B-02). Rig diesmal Versuch 6 mit Datenbank-Modi erfolgreich |
| 5 | Base an → Rig ohne Nachfrage 7,8 s; aus → nach 10 s Frist Desk 2,7 s; eigener Name „Simagic Base“ in allen Automatik-Logzeilen – ok (K-02, U-01). Kurz aus/an innerhalb der Frist und Countdown ablehnen (C-01) nicht an Hardware geprüft, vom Nutzer als erledigt abgehakt (Unit-Tests) |
| 6 | Standby mit Base aus (C-03): vom Nutzer nicht gewünscht, abgehakt ohne Hardwaretest (Unit-Tests) |
| 7 | Keine Stromspar-Warnung: selective suspend (Netz) ist aus, damit warnt `UsbPowerSaving.ShouldWarn` bewusst nicht, obwohl 3 Instanzen der Base Energiesparen erlauben – Verhalten wie geplant. Welches Flag kippt (C-04) nicht untersucht |
| 8 | Fenster vom linken CM27X3 nach Rig alle auf dem G9 – ok. Windows verschiebt sie selbst, Rettung meldet „Moved 0 window(s) … in 5 ms“ (B-04/B-14: kein Zeitbedarf) |
| 9a | Rig mit SimHub + Warten auf Base (30 s), Base nach 23 s eingeschaltet: „showed up after 23.1 s“, SimHub sofort gestartet – ok |
| 9b/c | Base gar nicht an (Toast mit eigenem Namen) und Hotkey während der Wartezeit (B-03): vom Nutzer abgehakt ohne Hardwaretest |
| 10 | **Abgebrochen, siehe HW-12.** Rig mit G9 HDR an + 240 Hz, Bestätigung global aus: G9 schwarz („kein HDMI-Signal“), harter Neustart nötig . HDR-Teil bis 1.4.1 ausgesetzt, Hz-Teil vom Nutzer abgehakt ohne Hardwaretest. Gegenprobe Rig ohne HDR-Vorgabe ok; SimHub startet nach 30 s ohne Base („DeviceMissing“) – deckt 9b im Verhalten ab |
| 11 | spacedesk nachholen: vom Nutzer am 2026-09-13 geprüft, ok. Die 3-s-Bus-Lücke nicht gezielt geprüft |
| 12 | `rigshift://apply/Rig` bei Bestätigung global aus: „from link: true“, Countdown mit Standard-15-s, abgelaufen → Rollback auf Desk nach 23,7 s – ok (H-02). Erster Versuch blockiert korrekt, weil der G9 nach dem HDR-Absturz nicht am Bus war |
| 13 | Wach halten: wirksam gesetzt/aufgehoben laut Log (auch nach Absturz-Neustart); Bildschirm-Timeout-Probe vom Nutzer abgehakt ohne Hardwaretest |
| 14 | Anruf-Absenkung hörbar: nicht aussagekräftig (Windows schon auf „Nichts unternehmen“, beide Profile mit Haken), vom Nutzer abgehakt |
| 15 | Strg+Alt+F1 (Desk) und Strg+Alt+F2 (Rig) registriert und ausgelöst – ok. Desk-Hotkey während Rig auf die Base wartet: „Cancelling the apps of Rig“, dann Desk – ok (B-03). Doppeldruck im Countdown und Konfliktmeldung nicht geprüft (Bestätigung aus) |
| 16 | Portable-Variante: vom Nutzer abgehakt ohne Hardwaretest |
| 19 | Diagnosebericht (18:51): keine Endpoint-IDs, kein Benutzername, Audio nur mit Namen – ok (H-05). Enthält aber volle Target- und Adapter-Gerätepfade mit Instanz-IDs → HW-17 |
| USB-Kombination | entfällt (Pedale hängen an der Base), nur `AutomationTriggerTests.Combination_*` |

### Befunde

| ID | Befund | Plan |
|---|---|---|
| **HW-12 (Kritisch)** | Rig mit G9 HDR an (240 Hz): Anordnung „Attempt 5 succeeded“ 18:13:48.849, danach der Snapshot für HDR – dann **keine Logzeile mehr** bis zum Neustart 18:20. Der native `DisplayConfigSetDeviceInfo` (SET_HDR_STATE) kehrte nicht zurück; geloggt wird erst nach dem Aufruf. Kein Countdown (Bestätigung global aus), kein Rollback. G9 „kein HDMI-Signal“, Kernel-Power 41 + EventLog 6008 (harter Reset), WER LiveKernelEvent 193 (dxgkrnl-Livedump) um 18:14:26/:29. Vermutung: HDR-Umschaltung sofort nach der Modusänderung, während der G9 noch seinen Handshake macht (er fällt dabei sekundenlang vom Bus), bzw. HDR + 240 Hz über HDMI an der Bandbreitengrenze. G9 hängt per **HDMI**, HDR wurde dort noch nie über Windows eingeschaltet – Ursache Monitor/Kabel vs. RigShift offen. **Gegenprobe 18:25:** Rig ohne HDR-Vorgabe lief (Countdown bestätigt); Windows zeigte HDR am G9 bereits aktiv (vom ersten Aufruf gesetzt). HDR **in den Windows-Einstellungen** ausgeschaltet → wieder schwarz, PC weder per RDP noch TeamViewer erreichbar, harter Reset 18:28 (Kernel-Power 41) – RigShift war daran nicht beteiligt (keine Logzeile). ⇒ Das HDR-Umschalten des G9 über HDMI legt den Grafikstapel dieses PCs lahm, unabhängig von RigShift. LiveKernelEvent 193 tritt auch ohne Wechsel auf (18:22–18:23) | 1.4.1: (a) Logzeile **vor** jedem nativen HDR-Aufruf; (b) HDR-Aufruf in eigenem Thread mit Zeitlimit, Wechsel läuft danach weiter/rollt zurück; (c) vor HDR warten, bis der Bildschirm stabil aktiv ist (Snapshot zweimal gleich); (d) ~~Countdown bei HDR erzwingen~~ – vom Nutzer gestrichen (2026-09-14): hilft nicht, wenn das System hängt; (e) Hinweis im Editor beim HDR-Schalter: erst einmal über Windows prüfen |
| HW-01 | NumberBox zu schmal: X- und Spin-Knöpfe verdecken die Zahl (Automatik-Wartezeit, App-Wartezeit, Bestätigungs-Sekunden) | 1.4.1; lokal behoben: `ClearButtonEnabled=False`, Breite 150 |
| HW-02 | Bestätigung global aus (`confirmTimeoutSeconds: 0`): Profil-Haken „Ohne Bestätigung“ wirkungslos, Sekundenfeld unsichtbar – Nutzer findet die Einstellung nicht | 1.4.1: Haken ausgegraut mit Hinweis, Sekundenfeld sichtbar, nur deaktiviert |
| HW-03 | Rig meldet orange „teilweise angewendet“, obwohl nur der optionale spacedesk fehlt | 1.4.1: fehlender optionaler Bildschirm zählt als angewendet |
| HW-06 | Rückstellung der Anruf-Absenkung loggt nichts, wenn der Wert schon stimmt (`RestoreRemembered`) | 1.4.1: Debug-Zeile |
| HW-07 | Beim Beenden im Countdown loggt das Fenster „Confirmation finished: TimedOut“ statt „Cancelled“ – irreführend | 1.4.1: eigenes Ergebnis bzw. Log-Text |
| HW-08 | Geräteliste „Vor dem App-Start auf Gerät warten“ zeigt nur verbundene Geräte und das eigene gespeicherte – die schon benannte, in einer Regel genutzte Base fehlte, solange sie aus war. Nutzer musste sie extra einschalten | 1.4.1: `UsbDeviceChoices` in Editor und Automatik mit allen bekannten Geräten füllen (Regeln, Warte-Geräte aller Profile, benannte Geräte) |
| HW-09 | Profile-Seite zeigt nicht, dass ein Profil Apps enthält | 1.4.1: Hinweis auf der Profilkarte (App-Namen bzw. Anzahl, Warte-Gerät) |
| HW-10 | Beim Wechsel auf Desk: `WindowRescuer` „Lost window of explorer not moved: the program is not responding“, Rettung 263 ms | beobachten; kein sichtbarer Effekt |
| HW-11 | Nutzerwunsch: moderne App-Auswahl statt Dateidialog – Liste installierter Programme (Startmenü-Verknüpfungen) und laufender Apps mit Icon und Suche; „Durchsuchen…“ bleibt als Rückfall | nach der Testrunde |
| HW-13 | Profil-Editor bietet für spacedesk nur 30 Hz: `ListRefreshRatesAsync` liest Raten nur von **aktiven** Bildschirmen (DXGI über den GDI-Namen); ein gerade inaktiver zeigt nur den gespeicherten Wert. Im Log nie „spacedesk offers …“ | 1.4.1: gelesene Raten pro Bildschirm merken (wie Namen) und bei inaktiven anbieten; sonst Hinweis „Raten erst lesbar, wenn der Bildschirm aktiv ist“ |
| HW-14 | Toast „Rig nicht möglich – Fehlende Bildschirme: spacedesk, Rig · Odyssey G93SC“ nennt auch den optionalen spacedesk; blockiert hat nur der G9 (Log: „Required displays are missing: Rig · Odyssey G93SC“) | 1.4.1: Toast nennt nur Pflicht-Bildschirme |
| HW-15 | G9 nach dem HDR-Absturz eingeschaltet: **Windows** stellt selbst die zuletzt mit dem G9 gespeicherte Anordnung her (nur G9); RigShift loggt nur „Active profile: Rig“ – ohne Audio, Wach halten, Apps, ohne Countdown. Profil wirkt aktiv, ist es aber nur halb | nach der Testrunde entscheiden: z. B. Toast „Rig erkannt – Rest anwenden?“ |
| **HW-16 (Nutzervorgabe)** | „RigShift muss unbedingt in der Lage sein, jeden Monitor aus dem Standby aufzuwecken, da dies das gängige Szenario ist.“ Stand: G9 im Standby, aber am Bus → wird geweckt (Test 2, 31/1610-Folge, ~7 s). G9 **nicht am Bus** („NotAttached“, 18:36) → sofort blockiert; über HDMI/DP kann Software einen Monitor, der sich abgemeldet hat, nicht wecken (kein CEC an GPUs, DDC/CI braucht die Verbindung) | 1.4.1: bei fehlendem Pflicht-Bildschirm nicht sofort blockieren, sondern Toast „G9 einschalten“ und bis ~30 s auf ihn warten, dann anwenden; Doku/Hinweis: Monitor-OSD so einstellen, dass er im Standby am Bus bleibt (z. B. Samsung „Standby-Modus“/Auto-Quelle, „Deep Sleep“ aus). Prüfen, ob der G9 nur nach dem HDR-Absturz verschwand |
| HW-17 | Diagnosebericht zeigt pro Bildschirm die vollen Gerätepfade (`\?DISPLAY#…#5&…&UID…#{…}`, `\?PCI#VEN_…&SUBSYS_…#…`). Für ein öffentliches Issue unnötig identifizierend; zur Fehlersuche reichen Hersteller-/Produktcode und UID. „Recent switches“ zeigt bei Rig „AppliedPartially … error 1610 – spacedesk“ – der Zwischenfehler 1610 wirkt wie die Ursache | 1.4.1: Pfade kürzen (z. B. `AUS32F6 · UID4353`, Adapter `VEN_10DE&DEV_2702`); letzter Fehlercode nur bei gescheiterten Wechseln |
| HW-04 | Nutzerwunsch: Bildschirme-Seite zeigt dieselben Nummern wie Windows-Einstellungen | nach der Testrunde |
| HW-05 | Nutzerwunsch: „Identifizieren“ aus der App – existiert seit 1.3.0 als „Erkennen“ (eigenes Fenster), Nutzer hatte es übersehen; offen nur, ob die Nummern zu Windows passen (HW-04) | mit HW-04 |

### Umsetzung 1.4.1 (2026-09-14, ohne Hardwaretest)

| ID | Umsetzung | Beleg |
|---|---|---|
| HW-01 | NumberBox `ClearButtonEnabled=False`, Breite 150 (Automatik, Einstellungen, Editor) | nur Build |
| HW-02 | Einstellungen: Sekundenfeld immer sichtbar, bei „aus“ deaktiviert. Editor: Haken „Ohne Nachfrage“ deaktiviert + Hinweis `Editor_ConfirmationOff` | nur Build |
| HW-03 | `SwitchOutcome.Applied` auch bei fehlendem optionalem Bildschirm, `AppliedPartially` nur noch bei dunklem Bildschirm (Datenbank-Modi). Nachholen folgt `Plan.ShouldRetryLater`. Toast „… folgt, sobald verbunden“ | `Switch_OptionalDisplayMissing_CountsAsApplied`, `PartialResult_RemembersCatchUp_…` |
| HW-06 | Debug-Zeile in `DuckingSwitcher.RestoreRemembered` | – |
| HW-07 | `ConfirmationResult.Cancelled` (Fenster bei Abbruch, Orchestrator im Catch) | – |
| HW-08 | `UsbDeviceChoices.Known`: Regel-Geräte, Warte-Geräte aller Profile, benannte Geräte – in Automatik und Editor | `DeviceChoices_OfferKnownDevicesThatAreNotConnected` |
| HW-09 | `ProfileItem.AppsLine` („Apps: SimHub, X (beenden) · wartet auf Base“) auf der Profilkarte | nur Build |
| HW-12 | (a) `Setting HDR of … to …` vor dem nativen Aufruf; (b) Aufruf auf eigenem Thread (`LongRunning`), `WaitAsync(HdrCallTimeout = 10 s)`, danach kein weiterer HDR-Aufruf in diesem Wechsel; (c) vor HDR zwei gleiche Snapshots in Folge (`HdrSettleBudget = 10 s`, sonst HDR unverändert); nur wenn ein Bildschirm abweicht oder keinen Zustand meldet; (e) Warnhinweis im Editor, sobald HDR an/aus gewählt ist | `Switch_HdrCallHangs_…`, `Switch_DisplaysStillChangingAfterApply_…`, `Switch_DisplaysNeverSettle_…` |
| HW-13 | `RefreshRateMemory` in `settings.json` (`refreshRates`, Schlüssel Pfad + Auflösung); gemerkt im Editor und nach jedem Wechsel für alle aktiven Bildschirme; Editor bietet gemerkte Raten, sonst Hinweis `Editor_RefreshRatesUnknown` | `RefreshRateMemoryTests` |
| HW-14 | `SwitchCoordinator.MissingForRecord`: bei `Blocked` nur Pflicht-Bildschirme | `Blocked_AsksForTheDisplay_AndNamesOnlyRequiredDisplays` |
| HW-16 | Fehlt ein Pflicht-Bildschirm ganz (`NotAttached`): Ereignis `WaitingForDisplays` → Toast „Bildschirm einschalten“, Warten bis `MissingDisplayWaitBudget = 30 s`, dann anwenden oder blockieren. Doku `docs/monitor-standby.md` (OSD allgemein, keine geratenen Menünamen), README verlinkt | `Switch_RequiredDisplayNotAttached_AsksForItAndBlocksAfterTheWait`, `Switch_RequiredDisplaySwitchedOnWhileWaiting_Applies` |
| HW-17 | Diagnose: `ShortTargetPath` (`AUS32F6 · UID4353`), `ShortAdapterPath` (`VEN_10DE&DEV_2702`), Fehlercode nur bei `Failed` | `ShortTargetPath_…`, `ShortAdapterPath_…`, `Build_ErrorCodeOnlyForFailedSwitches` |

In die nächste Hardware-Testrunde: HW-16 (G9 ausgeschaltet → Toast, einschalten, Wechsel), HW-12 nur Log-Reihenfolge ohne HDR-Umschaltung am G9, HW-13 (spacedesk-Raten nach einem Rig-Wechsel im Editor), HW-03-Toast.

### Umsetzung 1.5.0 (2026-09-14, ohne Hardwaretest)

Die drei offenen Nutzerwünsche der Testrunde 1.4.0. Damit ist aus HW-01…HW-17 nur noch HW-10 (beobachten) offen.

| ID | Umsetzung | Beleg |
|---|---|---|
| HW-04/05 | Windows bietet keine API für die Nummern der Einstellungsseite (Microsoft Q&A: „never a design goal“). Nummer = `n` aus dem GDI-Namen `\\.\DISPLAYn` der aktiven Quelle (`CcdRawSnapshot.Sources`, `AttachedDisplay.WindowsNumber`); fehlt sie bei einem aktiven Bildschirm, wie bisher links nach rechts (`DisplayNumbers.Assign`). Log „Display numbers: 1 = … (Windows 1)“ | `DisplayNumbersTests`, `Build_ActiveTarget_TakesTheWindowsNumberFromItsSource`, `ParseWindowsNumber_ReadsTheGdiName` |
| HW-11 | `AppDiscovery`: Startmenü-Verknüpfungen (alle Nutzer + eigener, Ziel `.exe`, ohne Windows-Ordner und Deinstaller) und laufende Apps mit Fenster, pro EXE ein Eintrag, laufende zuerst. `AppPickerWindow` mit Suche (jedes Wort in Name oder Dateiname, bester Treffer vorausgewählt), Icons nachgeladen, „Datei suchen …“ als Rückfall. „App hinzufügen“ öffnet den Picker, Abbrechen fügt nichts hinzu. Probe `apps` | `AppDiscoveryTests`, `AppPickerViewModelTests` |
| HW-15 | `SwitchCoordinator.NoticeDisplayChange(vorher, nachher)` nach Anzeigeänderung + Nachholen: anderes Profil aktiv, kein Wechsel läuft, Profil hat mehr als Bildschirme → Ereignis `RestoredByWindows` → Toast „Rig erkannt“; Klick wendet mit `SwitchRequest.KeepDisplays` Audio, Wach halten, Anruf-Absenkung, Fensterrettung und Apps an – ohne Anzeige-Änderung und ohne Countdown; nur wenn das Profil beim Klick noch aktiv ist | `NoticeDisplayChange_OffersTheRest_…`, `KeepDisplays_AppliesTheRest_WithoutTouchingDisplaysOrAsking` |

In die nächste Hardware-Testrunde zusätzlich: Nummern der Bildschirme-Seite mit Einstellungen → Anzeige → „Identifizieren“ vergleichen (Log-Zeile „Display numbers“); G9 bei aktivem Desk ausschalten/einschalten bzw. Windows die Rig-Anordnung herstellen lassen → Toast „Rig erkannt“, Klick → Audio/Apps; App-Auswahl mit SimHub/Crew Chief.
