# PdfStudio — Avalonia PDF Editor Handoff & Blueprint

> Drop this file into your new empty project folder (e.g. `E:\dev\PdfStudio\PROJECT_BRIEF.md`), open Antigravity (`agy`) in that folder, and tell the agent:
> **"Read PROJECT_BRIEF.md and scaffold Milestone 1."**

---

## 1. Product Goal

Build an **Adobe Acrobat Pro-style desktop PDF Editor** in **C# / .NET 9 + Avalonia UI** that:
1. Renders PDFs cleanly at high zoom speeds.
2. Understands PDF page layout (reconstructs logical **Text Runs → Lines → Paragraphs → Headings / Blocks** from low-level PDF glyph streams, plus OCR support for scanned pages).
3. Lets users click any paragraph to **edit text, font, size, color, spacing, and reflow inline** and save valid PDFs back cleanly.

**Architectural note:** Keep **`PdfStudio.Theme`** (UI design system) and **`PdfStudio.Core`** (PDF viewing + OCR) cleanly decoupled so they can also be reused later by a separate **German Invoicing / Büro-Software (Billomat / Lexware-style)** project without coupling business accounting / GoBD / ZUGFeRD logic into the PDF editor.

---

## 2. Tech Stack

- **Framework:** `.NET 9` (`9.0.101` installed), **Avalonia UI 11** (`Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Skia`)
- **MVVM:** `CommunityToolkit.Mvvm`
- **PDF Engine:** `PDFium` (`PDFiumCore` + native x64 `pdfium.dll`)
- **2D Rendering / Text Shaping:** `SkiaSharp` + `HarfBuzzSharp` (for fast tile rendering, fallback font measurement, and vector overlays)
- **Future Add-ons (Milestones 3–4):** `Tesseract` (OCR on scanned pages), `QPDF` CLI/bindings (structural merge/split/linearization if needed)

---

## 3. Solution Structure

```text
PdfStudio.sln
├── src/
│   ├── PdfStudio.Theme/         # Reusable dark slate-navy Avalonia ResourceDictionary & ControlThemes
│   ├── PdfStudio.Core/          # PDFium wrapper, page renderer, text-block detector, reflow & save engine
│   └── PdfStudio.App/           # Avalonia desktop shell, ViewModels, Views, custom interactive canvas
└── tests/
    └── PdfStudio.Core.Tests/    # Layout grouping, font fallback, and PDF round-trip save tests
```

---

## 4. Visual Design System (Concept Mockup Reference)

Reference mockup generated during design:
`C:\Users\YA\.gemini\antigravity-cli\brain\d9dcbd28-b683-464c-8c7c-5cb6351d98e5\pdf_editor_avalonia_mockup_1794927219691.jpg`

### Color Tokens (`AppTheme.axaml`)
- **App Canvas Background:** `#10131B`
- **Docked Panels & Sidebars:** `#181C26`
- **Card / Group Headers:** `#202533`
- **Elevated / Hover Surface:** `#2A3144`
- **1px Hairline Borders:** `#333C52`
- **Primary Accent Blue:** `#4287F5` (hover `#5997F9`)
- **Active Toggle Switch Green:** `#3CAD7B`
- **Warning / Danger:** `#D99D39` / `#D54F55`
- **Text Primary / Muted:** `#EAEDF3` / `#949DAF`

### UI Styling Rules
- Ultra-clean flat slate-navy aesthetic with thin `1px` `#333C52` dividers (never thick splitters).
- Compact vector line icons only; crisp geometric chevrons on collapsible cards; pill toggle switches (`#3CAD7B`) for booleans like `Auto-Reflow Paragraph`.
- Right Inspector uses clean two-column property rows inside collapsible rounded cards (`4px` corner radius).
- All popups, dropdowns, and font pickers must clamp strictly inside the window bounds and stay searchable/scrollable.

### Main Window Layout
1. **Top Titlebar + Tab Strip + Action Toolbar**
   - Document tabs (`Filename.pdf *`, close `x`) + Save / Undo / Redo buttons.
   - Centered tool strip: `Select`, `Edit Text (T)` (highlighted `#4287F5` when active), `Add Image`, `Highlight`, `Comment`, `Redact`, `OCR`, `Zoom (%)`, `Find`.
2. **Left Sidebar (`~185px`)**
   - `Pages` / `Outline` slim tabs.
   - Virtualized page thumbnails with page numbers; active page highlighted with `#4287F5` border.
3. **Center Interactive Document Canvas (`#10131B`)**
   - Smooth zoom/pan page viewport rendering sharp PDF bitmaps/tiles via PDFium + SkiaSharp.
   - When `Edit Text` is active: hovering shows subtle block outlines; clicking a block displays a clean `#4287F5` bounding box with handles + live text editor matching the block's font, size, color, and line height.
4. **Right Properties Inspector (`~280px`)**
   - **`TEXT & FONT` card:** Font family, weight/size, color picker swatch, `B / I / U / Align` segmented toggles, letter & line spacing numeric drags, embedded-font / fallback-glyph status badge.
   - **`PARAGRAPH & REFLOW` card:** `X / Y / W / H` numeric fields, `Auto-Reflow Paragraph` pill switch, inline find & replace box.
   - **`DOCUMENT ACTIONS` card:** `Detect Text Blocks`, `Run OCR`, `Rewrite / Translate Block`.
5. **Bottom Status Bar (`24px`)**
   - `Page X of Y • Width × Height • PDF Version • Font Embed Status` on the left; zoom slider on the right.

---

## 5. Implementation Milestones

### Milestone 1 — Solution Scaffold, Dark Theme Shell & PDFium Viewer
- Create `PdfStudio.sln` (`PdfStudio.Theme`, `PdfStudio.Core`, `PdfStudio.App`).
- Implement `PdfStudio.Theme` with the exact color tokens & ControlThemes above.
- Build MainWindow shell (tabs, toolbar, virtualized page thumbnail strip, center zoom/pan canvas, right inspector placeholder, status bar).
- Load PDF files via `PDFiumCore` and render crisp DPI-aware pages onto `SkiaSharp` bitmaps (`File > Open` + drag-and-drop).

### Milestone 2 — Understand PDF Text (`TextBlockDetector`)
- Query `FPDFText_*` + `FPDFPageObj_*` for character positions, bounding boxes, font names, sizes, rotation, and fill colors.
- Group raw glyphs into **Spans → Lines → Logical Paragraphs / Headings** cleanly (handling multi-column layouts cleanly).
- Draw interactive hover/selection bounding boxes in `Edit Text` mode and bind selected block metrics into the Right Inspector (`TEXT & FONT`, `PARAGRAPH & REFLOW`).

### Milestone 3 — In-Place Text Editing, Font Fallback & Save
- Overlay a styled text editor over the clicked paragraph so users can type, delete, and wrap live.
- Detect whether the PDF's embedded font subset contains all newly entered glyphs; seamlessly synthesize/embed a matching system or bundled fallback font (`HarfBuzzSharp` + `SkiaSharp`) when glyphs are missing.
- Commit edits back into `PDFium` page objects (`FPDFPageObj_*`), track Undo/Redo commands, and save cleanly (`Ctrl+S` / `Save As`).

### Milestone 4 — Acrobat Pro Workflow Tools
- Annotations (`Highlight`, `Comment`), `Add Image`, `Redact` (permanently strip covered PDF objects on apply), page reorder/delete/insert/merge, and `Run OCR` via **Tesseract** for scanned PDFs.
