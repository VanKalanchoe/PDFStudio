# PDFStudio

[![CI & Multi-Platform Release](https://github.com/VanKalanchoe/PDFStudio/actions/workflows/ci-release.yml/badge.svg)](https://github.com/VanKalanchoe/PDFStudio/actions/workflows/ci-release.yml)
[![Release](https://img.shields.io/github/v/release/VanKalanchoe/PDFStudio?include_prereleases&label=Release)](https://github.com/VanKalanchoe/PDFStudio/releases)
![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4)
![Platforms](https://img.shields.io/badge/Platforms-Windows%20%7C%20macOS-1D2539)

A fast, offline-first desktop PDF viewer, editor, and workflow tool built with **C# / .NET 9**, **Avalonia UI**, **PDFium**, **SkiaSharp**, and **Tesseract OCR**.

> **Status:** Early Alpha (`v0.x`)

## Features

- **Crisp Viewing & Navigation:** DPI-aware smooth zoom, pan, continuous scrolling, virtualized page thumbnails, and bookmark outline tree.
- **Direct In-Place Text Editing:** Click any detected text block to edit inline while preserving background colors, font matching, fallback glyph embedding, and auto-reflow.
- **Movable & Resizable Images:** Insert PNG/JPEG/BMP/WebP images, drag to reposition, resize via 8 handles, or delete with undo/redo (`Ctrl+Z` / `Ctrl+Y`).
- **Annotations & Redaction:** Yellow highlights, interactive sticky-note comment pins, and true content-stripping black redactions.
- **Page Organizer & Quick Find:** Reorder pages, insert blank pages, merge PDFs, delete pages, and search across pages (`Ctrl+F`).
- **Offline OCR (Windows):** Recognize scanned PDFs with Tesseract (`eng.traineddata`) and embed an invisible searchable text layer.
- **Bilingual UI:** Switch live between **English** and **Deutsch** in Settings.

## Download

Grab the latest prebuilt builds from [GitHub Releases](https://github.com/VanKalanchoe/PDFStudio/releases):

- **Windows (`PdfStudio-win-x64.zip`):** Extract and run `PdfStudio.App.exe` (self-contained, no .NET install needed).
- **macOS (`PdfStudio-macos-x64.zip`):** Extract `PdfStudio.app` (on first launch for unsigned alpha builds: right-click → **Open**).

## Build from Source

```powershell
# Run tests
dotnet test tests/PdfStudio.Core.Tests/PdfStudio.Core.Tests.csproj

# Run the desktop app
dotnet run --project src/PdfStudio.App/PdfStudio.App.csproj
```
