# Auftrag: Vollständige Analyse von RigShift 1.3 (Performance, Last, Zuverlässigkeit, UI/UX, alles)

Stand: 2026-09-14 · Grundlage: `main` nach Release 1.3.0 · Sprache der Ergebnisse: Deutsch

## 0. Rolle, Ziel, Grenzen – zuerst lesen

**Diese Session analysiert nur und schreibt einen Umsetzungsplan. Sie ändert keinen Code, keine Doku außerhalb von
`docs/analysis/`, keine Einstellungen und veröffentlicht nichts.** Umgesetzt wird später in einer eigenen Session
(Opus) auf Basis des Plans. Wer beim Analysieren einen offensichtlichen Einzeiler-Fix sieht: aufschreiben, nicht fixen.

Ziel: Alles finden, was RigShift langsamer, instabiler, unsicherer, schwerer bedienbar oder schwerer wartbar macht,
als es sein müsste – **bevor** die Hardware-Testrunde am Gaming-PC stattfindet. Maßstab ist das Produktziel des
Nutzers, wörtlich: *„Das Tool sollte eigentlich ein kleines Tool sein das immer nebenher laufen kann und dafür sorgt
das ich einfach und schnell in mein Rig wechseln kann.“* Es läuft dauerhaft im Tray – Leerlaufkosten und
Langzeitstabilität zählen deshalb besonders.

Vorher lesen: `CLAUDE.md` (Regeln und verifizierte Stolperfallen), `docs/PLAN.md` (Abschnitt 4 Architektur,
Abschnitt 6 oben: Umfangsprüfung und aktuelle Entscheidungen), `docs/display-topology.md` (harte Regeln),
`docs/ARCHITECTURE.md`, `CHANGELOG.md` `[1.3.0]`. **Getroffene Entscheidungen werden nicht neu verhandelt** – nur
wenn ein Befund eine Entscheidung ernsthaft infrage stellt, als eigener Punkt mit Begründung.

### Harte Grenzen am Server (dieser Rechner ist der Home-Server, per RDP, nicht der Gaming-PC)

- **Nie umschalten.** CLI nur `--help`, `list`, `status`, `save`, `apply <name> --dry-run`. Keine erzeugte
  Verknüpfung, kein `rigshift://`-Link starten, keine Automatik-Regel mit einem **verbundenen** USB-Gerät und echtem
  Profil anlegen.
- Keine System- oder Registry-Änderungen, keine Dienste, kein Neustart. Probe nur lesend
  (`dotnet run --project tools/RigShift.Probe -- snapshot | rates | audio | usb | usb-power <id>`).
- Die App darf gestartet werden (Messungen, Screenshots). Vorher `%AppData%\RigShift\settings.json` sichern, danach
  wiederherstellen und eingespielte Testprofile löschen; alle RigShift-Prozesse am Ende beenden.
- Der Server hat nur die RDP-Anzeige und 0 Audio-Endpunkte: Anzeige-/Audio-Umschalten ist hier **nicht** messbar.
  Solche Punkte als „braucht Gaming-PC“ markieren und über Code-Lesen + Unit-Tests/Fakes bewerten.
- Stolperfallen für App-Automatisierung (Screenshots, UIA, `PrintWindow`, Fenstergrößen) stehen in `CLAUDE.md` und im
  Staffelstab `.claude/handoff.md`.

### Arbeitsweise

- Jeder Befund braucht einen **Beleg**: `Datei:Zeile`, Messwert mit Messweg, Screenshot-Pfad oder reproduzierbarer
  Schritt. Ohne Beleg nur als „Vermutung“ kennzeichnen.
- Bereiche sind groß: für breite Code-Durchsicht Subagents parallel einsetzen (je Bereich einer), Ergebnisse selbst
  gegenprüfen, bevor sie in den Bericht gehen – False Positives kosten die Umsetzungs-Session Zeit. Einen
  Multi-Agent-Workflow nur starten, wenn der Nutzer das in der Session ausdrücklich verlangt.
- Messungen mit **Release-Build** (`dotnet publish src/RigShift.App -c Release` in einen Scratch-Ordner), nicht Debug.
- Zwischenstände früh in die Ergebnisdatei schreiben (Abschnitt 3), nicht erst am Ende.

## 1. Was untersucht wird – von A bis Z

Pro Bereich: die Leitfragen beantworten, fehlende Fragen ergänzen. „Nichts gefunden“ ist ein gültiges Ergebnis und
wird mit dem Prüfweg notiert.

### A. Architektur und Codequalität
- Schichtung Core → Windows → App eingehalten? Win32/UI-Abhängigkeiten in Core? Zyklische oder unnötige Kopplung?
- DI: Lebensdauern (Singletons mit Zustand, `IDisposable`/`IAsyncDisposable`, bekannte Falle: `_host.Dispose()` mit nur
  `IAsyncDisposable`), Registrierungen für entfernte Features übrig?
- **Reste der Umfangskürzung** (Spiele-Automatik, HTTP-API, Energieplan, HA): tote Typen, Strings, Settings-Felder,
  NativeMethods-Einträge, Doku, Tests, Issue-Templates.
- Duplikate (z. B. mehrfache USB-Geräteauflistung, Hz-Formatierung), zu große Klassen (`ViewModels.cs`,
  `SwitchOrchestrator`), Namensgebung, Kommentare, die nicht mehr stimmen.
- Analyzer-Unterdrückungen (`#pragma`, `.editorconfig`, `SuppressMessage`) – jede begründet?
- Einhaltung Skill `dotnet-standards` (Logging, async, Null-Safety, Pfade, Regex-Timeouts, NuGet-Pinning).

### B. Umschalt-Kern: Korrektheit und Zuverlässigkeit
- `SwitchOrchestrator` als Zustandsautomat gegen `docs/PLAN.md` 4.3 und `docs/display-topology.md`: jede Regel
  eingehalten? Ein einziger atomarer `SetDisplayConfig`, Wiederholung bei 31/1610, Modus-Fallback, Zeitbudget.
- Rollback und „Wiederherstellen nach Fehler“: stellt wirklich alles zurück (Topologie, HDR, Audio, Lautstärke, Wach
  halten, Anruf-Absenkung)? Reihenfolge? Was, wenn der Rollback selbst scheitert?
- **Absturz/Kill mitten im Wechsel**: welcher Zustand bleibt hängen? Besonders `UserDuckingPreference` (bleibt 3?),
  gemerkter „Wert vorher“ nur im Speicher, Wach halten (endet mit Prozess), halb angewendete Topologie.
- Nebenläufigkeit: Tastenkürzel + Automatik + CLI/Pipe + Tray + `rigshift://` gleichzeitig; `SwitchCoordinator`-Gate,
  Busy-Meldung, Wettläufe mit `CatchUpAsync` und `DisplayChangeWatcher`.
- Neue 1.3-Pfade: Fensterrettung (Koordinaten Workspace/Screen, DPI, minimierte/maximierte Fenster, eigene Fenster
  inkl. Countdown-Dialog, erhöhte Prozesse, 1 s Verzögerung sinnvoll?), HDR nach jedem Apply (auch beim Rollback auf
  Bildschirme ohne HDR), Warten auf USB-Gerät (Abbruch, Timeout, Ergebnisanzeige), DXGI-Raten.
- Randfälle: kein Bildschirm aktiv, doppelte EDID (zwei gleiche Monitore – bekannt: 2× CM27X3), Monitor während des
  Wechsels abgesteckt, Standby/Resume, RDP-Sitzung, Treiber-Reset, GPU-Wechsel, Profil ohne Audio.

### C. Automatik und USB
- Polling-Takt 2 s: CPU-, Allokations- und Handle-Kosten pro Abfrage (`CM_Get_Device_ID_List`, Namensauflösung),
  Verhalten bei vielen USB-Geräten.
- Zeitmodell: Systemuhr-Sprünge, Sleep/Hibernate (Wartezeit läuft „während Standby“ ab?), Zeitzonenwechsel.
- Baseline-Logik, Pausieren, Regeländerung zur Laufzeit, Regel auf gelöschtes Profil, Regeln ohne Gerät (alte
  Spiel-Regeln), mehrere Regeln auf dasselbe Gerät, Gerät mit Suffix-IDs.
- Interaktion mit manuellem Wechsel des Nutzers und mit „Apps warten auf Gerät“.
- Stromspar-Warnung: Registry-Lesezugriffe pro Aktualisierung, Fehlalarme, Semantik der Flags (belegt oder geraten?).

### D. Performance und Ressourcen (dauerhaft im Tray!)
- **Messen** (Release-Build, App ohne Fenster im Tray bzw. mit Hauptfenster): Startzeit bis Tray-Icon, Arbeitsspeicher
  (Working Set, Private Bytes, GC-Heap), Threads, Handles, GDI/User-Objekte, CPU im Leerlauf – über **mindestens
  30 Minuten** in Abständen protokollieren (z. B. `Get-Process` alle 30 s in eine CSV; `dotnet-counters`, falls
  installierbar ohne Systemänderung). Wachstum = Leck-Verdacht.
- Leerlauf-Aktivität: Timer, Polling, Log-Schreibzugriffe, Settings-/Profil-Lesezugriffe, Theme-/Sprach-Events.
- Zeit für einen Dry-Run-Wechsel (`apply <name> --dry-run`) und für Snapshot-Abfragen; Kosten von `QueryDisplayConfig`
  und DXGI-Enumeration im Editor.
- Paketgröße Setup/Portable, Single-File/ReadyToRun-Effekt auf Start und Größe, unnötige Abhängigkeiten.
- UI: Öffnen von Hauptfenster und Editor, Seitenwechsel, Listen mit vielen Profilen.

### E. Last- und Stresstests (nur nicht-umschaltende Wege)
- Viele Profile (z. B. 50) und viele Regeln (z. B. 20) in Testdaten: Start, Tray-Menü, Editor, Automatik-Polling.
- Schnell wiederholte CLI-/Pipe-Aufrufe (`status`, `list`, `apply --dry-run`) parallel: Hänger, Pipe-Fehler, Deadlocks.
- Kaputte oder riesige `settings.json`/Profil-Dateien, gesperrte Dateien, schreibgeschützter Ordner, fehlender
  `%AppData%\RigShift`, gleichzeitiges `save` per CLI während der Editor speichert.
- Wo echtes Umschalten nötig wäre: Stress über die Core-Tests mit Fakes bewerten (z. B. viele Wechsel hintereinander,
  Timeouts), nicht am echten System.

### F. Persistenz, Kompatibilität, Update
- JSON-Schemata, Source-Generator-Falle (Initialisierer bei fehlendem Schlüssel), Laden von Dateien aus 1.0/1.2,
  unbekannte Felder, atomares Schreiben (`.tmp` + Move), Verhalten bei Schreibfehler.
- Velopack: Update-Pfad 1.2 → 1.3, Auto-Apply und CLI-Aufrufe, Datenordner (`%AppData%` vs. `%LocalAppData%`-Falle),
  Deinstallation, Portable-Variante.

### G. Windows-Integration und Interop
- CsWin32-Aufrufe: Strukturgrößen, Rückgabewerte geprüft, `Marshal.ReleaseComObject`/COM-Lebensdauer (MMDevice,
  DXGI, ShellLink), Fehlercodes geloggt.
- HDR-APIs vor/ab 24H2, Power Request, `RegisterHotKey`, Einzelinstanz (Mutex + Named Pipe), URI-Handler-Registrierung,
  App-Start pro Profil (Anführungszeichen, Umgebungsvariablen, Arbeitsverzeichnis, UAC-pflichtige Programme, Beenden
  per Schließen/Kill nach 5 s).
- DPI (100/125/150/200 %, gemischte DPI), Hell/Dunkel/Hoher Kontrast, Windows 10 vs. 11.

### H. Sicherheit und Datenschutz
- Named Pipe: wer darf verbinden (ACL, andere Benutzer/Sitzungen)? Kann eine fremde Anwendung umschalten oder
  Profile speichern?
- `rigshift://`-Links: Eingabevalidierung, Sonderzeichen, lange Namen, Missbrauch aus dem Browser (nur Umschalten mit
  Bestätigung?).
- Apps pro Profil: kann eine manipulierte Profildatei beliebige Programme starten (erwartbar, aber dokumentiert?).
- Diagnosebericht und Logs: welche persönlichen Daten (Gerätepfade, Endpoint-IDs, Benutzername in Pfaden)?
- Registry-Schreibzugriffe (HKCU Run, URI-Handler, Ducking), unsignierte Updates über GitHub – Risiko benennen.
- `dotnet list package --vulnerable --include-transitive` und `--outdated`.

### I. UI/UX – jede Oberfläche
Seiten und Fenster: **Profile, Bildschirme, Automatik, Über & Hilfe, Einstellungen, Profil-Editor,
Bestätigungs-Countdown, Tray-Popup, Tray-Kontextmenü, Erkennen-Overlay, Benachrichtigungen/Toasts,
Fehlermeldungen, Erster Start ohne Profile.**
- Mit Screenshots belegen (Muster im Staffelstab): **de und en**, **hell und dunkel**, Fenster in Mindestgröße und
  breit; Countdown/Tray/Branding über `--preview-confirmation`, `--preview-branding`, `--preview-theme` (Debug-Build).
- Informationsarchitektur: Findet ein neuer Nutzer in 2 Minuten zu „Desk- und Rig-Profil anlegen, Wheelbase-Regel
  einrichten“? Was fehlt im ersten Start (leere Zustände, Hinweise)?
- Texte: verständlich für Sim-Racer ohne Technikwissen? Einheitliche Begriffe (Profil/Anordnung/Hauptanzeige/
  Hauptbildschirm …), Tonfall, Tippfehler, abgeschnittene oder umbrechende Texte, de/en inhaltlich gleich.
- Layout: Abstände, Ausrichtung, Spaltenbreiten (bekannt: HDR-Auswahl bricht im Editor unter 1000 px um; Texte in der
  Regelkarte unter 1400 px abgeschnitten), Scrollverhalten, Fokus-Rahmen, Symbolwahl, Konsistenz der Knöpfe.
- Rückmeldung: Was sieht der Nutzer während eines Wechsels, bei Erfolg, Teil-Erfolg, Blockiert, Rollback, Fehler,
  „Gerät nicht erkannt – Apps trotzdem gestartet“? Verständlich und handlungsleitend?
- Fehlbedienung: gefährliche Aktionen (Profil löschen, Regel löschen, „Ohne Bestätigung umschalten“) abgesichert?
- Barrierefreiheit: Tastaturbedienung aller Seiten (Tab-Reihenfolge, Enter/Esc), UIA-Namen und -Rollen (bekannt:
  ComboBoxen der Regelkarte per UIA nicht auffindbar), LiveSetting für Statusmeldungen, Kontrast, Skalierung.

### J. Lokalisierung
- Schlüssel-Parität `Strings.resx` ↔ `Strings.de.resx`, ungenutzte Schlüssel (nach Entfernen der Features),
  fest verdrahtete Texte im Code/XAML, Formatierung mit `Loc.Instance.Culture`, Pluralformen.

### K. Logging und Diagnose
- Reichen die Logs, um die kommende Hardware-Testrunde auszuwerten (jeder Schritt eines Wechsels mit Dauer, jede
  Automatik-Entscheidung, HDR/Fenster/Ducking-Ergebnis)? Zu laut im Leerlauf? Strukturierte Platzhalter statt
  Interpolation? Rotation/Größe? Fehlende Warnungen an fehlbaren Stellen?

### L. Tests
- Abdeckung gegen Risiko: welche kritischen Pfade haben keine Tests (Windows-Schicht, ViewModels, Pipe, CLI-Parsing,
  Settings-Migration)? Zeitabhängige oder wackelige Tests? Aussagekraft der Fakes gegenüber echtem Verhalten.
- Vorschläge konkret: welcher Test, welche Datei, was er belegt.

### M. Build, CI, Release
- Warnings-as-errors wirksam, Versionen gepinnt, CI-Workflow vs. Release-Workflow (gleiche Checks?), Release-Notes aus
  CHANGELOG, Tag/Version-Prüfung, Reproduzierbarkeit, Dauer.

### N. Dokumentation
- README-Aussagen gegen tatsächliches Verhalten prüfen (jede Funktionsbehauptung), CHANGELOG, ROADMAP, ARCHITECTURE,
  PLAN-Konsistenz nach der Kürzung, `docs/usb-power-saving.md` fachlich korrekt, CONTRIBUTING/SECURITY/Issue-Vorlagen,
  veraltete Stolperfallen in `CLAUDE.md`.

### O. Produktumfang
- Gibt es trotz Kürzung Funktionen oder Einstellungen, die mehr Komplexität als Nutzen bringen? Was könnte man
  vereinfachen, ohne das Ziel zu verletzen? Nur Empfehlungen, keine Entscheidung – die trifft der Nutzer.

## 2. Priorisierung der Befunde

| Schweregrad | Bedeutung |
|---|---|
| **Kritisch** | Datenverlust, hängender Systemzustand (z. B. Einstellung bleibt verstellt), Absturz, Umschalten schlägt fehl, Sicherheitslücke |
| **Hoch** | Fehlverhalten in üblichen Situationen, spürbare Leerlaufkosten/Lecks, stark irreführende UI |
| **Mittel** | Randfälle, Wartbarkeit, UX-Reibung, fehlende Tests für riskante Pfade |
| **Niedrig** | Kosmetik, Wortwahl, kleine Aufräumarbeiten |

Zusätzlich je Befund: **Aufwand** S/M/L, **Beleg** (belegt/Vermutung), **braucht Gaming-PC** ja/nein.

## 3. Ergebnis – genau zwei Dateien, beide in `docs/analysis/`

1. **`befunde-1.3.md`**
   - Kurzfazit (max. 10 Zeilen): Gesamteindruck, größte Risiken, Messwerte im Leerlauf.
   - Methode: was gemessen/angesehen wurde, womit, wie lange; was **nicht** prüfbar war.
   - Befund-Tabelle: `ID` (z. B. B-07) · Bereich (A–O) · Titel · Schweregrad · Aufwand · Beleg · Gaming-PC · Empfehlung.
   - Darunter je Befund ab „Mittel“ ein Absatz: Beobachtung, Ursache, Auswirkung, Empfehlung.
   - Messwerte als Tabelle; Screenshots unter `docs/analysis/screenshots/` (keine Gerätepfade/Endpoint-IDs sichtbar).
2. **`umsetzungsplan-1.3.md`** – für die spätere Opus-Session
   - **Paket 1 – vor der Hardware-Testrunde (1.3.1):** alle Kritisch/Hoch mit Aufwand S/M.
   - **Paket 2 – nach der Testrunde:** Rest, der sich lohnt.
   - **Nicht umsetzen / Nutzer entscheidet:** mit Begründung; Umfangsfragen als Fragen mit Empfehlung formulieren.
   - Je Punkt: Befund-IDs, betroffene Dateien, konkretes Vorgehen, Akzeptanzkriterien, zu ergänzende Tests, Risiko.
   - Liste **„In die Hardware-Testrunde aufnehmen“**: was nur am Gaming-PC geprüft werden kann, mit Prüfschritt.

Am Ende: beide Dateien committen (`docs: analysis findings and fix plan for 1.3`) und pushen, den Staffelstab
`.claude/handoff.md` auf „Umsetzung laut `docs/analysis/umsetzungsplan-1.3.md`“ umstellen und dem Nutzer in wenigen
Zeilen berichten: Anzahl Befunde je Schweregrad, die drei wichtigsten, offene Nutzerentscheidungen.
