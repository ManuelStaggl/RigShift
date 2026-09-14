# Umsetzungsplan nach der Analyse von RigShift 1.3.0

Stand: 2026-09-14 · Befunde: `befunde-1.3.md` (IDs darauf bezogen) · Für die Umsetzungs-Session. Regeln: CLAUDE.md,
Skill `dotnet-standards`, `docs/display-topology.md`. Am Server nie umschalten (siehe CLAUDE.md). Nach jedem Paket:
`dotnet build RigShift.slnx` 0 Warnungen, `dotnet test --solution RigShift.slnx` grün, CHANGELOG `[Unreleased]`.

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
  `RuleDisabledWhileRunning_NoSwitchBack`, `Reset_DuringExitDelay_ClearsGoneSince`.
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
| 19 | Diagnosebericht | „Diagnose-Infos kopieren“ im Rig | Inhalt prüfen, keine Endpoint-IDs/Benutzername (H-05) |
| 20 | 2× CM27X3 | linken CM27X3 abstecken, Desk umschalten | Planner blockiert korrekt statt zu raten (B-10, ab Paket 2) |
