# ADR-0002: .NET 10, WPF + WPF-UI, CsWin32, Velopack

Status: accepted · Date: 2026-09-13

## Context

The app is Windows-only, must live in the tray, touch low-level display and audio APIs, and ship as a small
self-updating download. Alternatives considered: WinUI 3 (used in the author's HushKey project), Avalonia, MSIX.

## Decision

- **.NET 10 LTS** – supported until November 2028.
- **WPF with WPF-UI 4.3** – Fluent look without the Windows App SDK; smaller single-file publish; stable window
  behaviour during display topology changes; mature tray support via **H.NotifyIcon.Wpf**.
- **CommunityToolkit.Mvvm** + **Microsoft.Extensions.Hosting** for MVVM and DI.
- **CsWin32** for all Win32 interop – generated from metadata, no hand-written struct layouts.
- **Velopack** for installer, delta updates and portable builds, fed from GitHub Releases.
- **xunit v3** on Microsoft.Testing.Platform.

## Consequences

- No Native AOT (WPF does not support it); ReadyToRun + single-file instead (single-file dropped in 1.4 for smaller delta updates, see
  `docs/analysis/umsetzungsplan-1.3.md`, D-02).
- `IPolicyConfig` stays a hand-written COM import because it is undocumented and absent from the metadata.
- WinUI 3 code from HushKey is not reusable as-is; the Win32 patterns are.
