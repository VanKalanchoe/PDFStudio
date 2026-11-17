using CommunityToolkit.Mvvm.ComponentModel;

namespace PdfStudio.App.Services;

/// <summary>
/// Provides live-updating English and German UI strings for compiled XAML bindings.
/// </summary>
public sealed class LocalizationService : ObservableObject
{
    private string _languageCode = "en";

    public string LanguageCode
    {
        get => _languageCode;
        set
        {
            string normalized = string.Equals(value, "de", StringComparison.OrdinalIgnoreCase) ? "de" : "en";
            if (_languageCode == normalized)
            {
                return;
            }

            _languageCode = normalized;
            OnPropertyChanged(string.Empty);
        }
    }

    public bool IsGerman => _languageCode == "de";
    public bool IsEnglish => _languageCode == "en";

    private string T(string en, string de) => IsGerman ? de : en;

    // Window chrome & tabs
    public string MinimizeTooltip => T("Minimize", "Minimieren");
    public string MaximizeTooltip => T("Maximize / Restore", "Maximieren / Wiederherstellen");
    public string CloseWindowTooltip => T("Close", "Schließen");
    public string CloseDocumentTooltip => T("Close document", "Dokument schließen");

    // Toolbar actions & tools
    public string OpenPdfTooltip => T("Open PDF (Ctrl+O)", "PDF öffnen (Strg+O)");
    public string SavePdfTooltip => T("Save PDF (Ctrl+S)", "PDF speichern (Strg+S)");
    public string UndoTooltip => T("Undo (Ctrl+Z)", "Rückgängig (Strg+Z)");
    public string RedoTooltip => T("Redo (Ctrl+Y)", "Wiederholen (Strg+Y)");
    public string SelectToolTooltip => T("Select / Pan (V)", "Auswählen / Verschieben (V)");
    public string EditTextTooltip => T("Edit Text (T)", "Text bearbeiten (T)");
    public string AddImageTooltip => T("Add Image", "Bild einfügen");
    public string HighlightTooltip => T("Highlight", "Hervorheben");
    public string CommentTooltip => T("Comment", "Kommentar");
    public string RedactTooltip => T("Redact", "Schwärzen");
    public string OcrTooltip => T("Run OCR", "OCR ausführen");
    public string ZoomOutTooltip => T("Zoom Out (Ctrl+-)", "Verkleinern (Strg+-)");
    public string ZoomInTooltip => T("Zoom In (Ctrl++)", "Vergrößern (Strg++)");
    public string FindTooltip => T("Find (Ctrl+F)", "Suchen (Strg+F)");
    public string SettingsTooltip => T("Settings", "Einstellungen");

    // Left sidebar & page organizer
    public string PagesTab => T("Pages", "Seiten");
    public string OutlineTab => T("Outline", "Gliederung");
    public string MovePageUp => T("Move Page Up", "Seite nach oben");
    public string MovePageDown => T("Move Page Down", "Seite nach unten");
    public string InsertBlankPage => T("Insert Blank Page", "Leere Seite einfügen");
    public string InsertBlankPageAfter => T("Insert Blank Page After", "Leere Seite danach einfügen");
    public string MergePdfMenu => T("Merge PDF...", "PDF zusammenführen...");
    public string DeletePageMenu => T("Delete Page", "Seite löschen");
    public string DeleteCurrentPageTooltip => T("Delete Current Page", "Aktuelle Seite löschen");
    public string NoOutlinesText => T("No outline bookmarks in this PDF.", "Keine Lesezeichen in diesem PDF vorhanden.");

    // Empty state / dropzone
    public string DropzoneTitle => T("Open a PDF document", "PDF-Dokument öffnen");
    public string DropzoneSubtitle => T(
        "Drag and drop a .pdf file anywhere, or browse from disk.",
        "Ziehen Sie eine .pdf-Datei hierher oder wählen Sie eine Datei aus.");
    public string OpenPdfButton => T("Open PDF...", "PDF öffnen...");
    public string OpenSampleButton => T("Open Sample Report", "Beispielbericht öffnen");

    // Quick find & comment popover
    public string FindWatermark => T("Find in document...", "Im Dokument suchen...");
    public string NextButton => T("Next", "Weiter");
    public string CloseFindTooltip => T("Close Find", "Suche schließen");
    public string CommentNoteTitle => T("Comment Note", "Kommentar");
    public string CommentWatermark => T("Write comment...", "Kommentar schreiben...");
    public string DeleteButton => T("Delete", "Löschen");
    public string CancelButton => T("Cancel", "Abbrechen");
    public string SaveButton => T("Save", "Speichern");

    // Right inspector
    public string TextFontHeader => T("TEXT & FONT", "TEXT & SCHRIFTART");
    public string SearchFontsWatermark => T("Search fonts...", "Schriftarten suchen...");
    public string TextColorTooltip => T("Text Color", "Textfarbe");
    public string BoldTooltip => T("Bold", "Fett");
    public string ItalicTooltip => T("Italic", "Kursiv");
    public string UnderlineTooltip => T("Underline", "Unterstrichen");
    public string AlignLeftTooltip => T("Align Left", "Linksbündig");
    public string AlignCenterTooltip => T("Align Center", "Zentriert");
    public string AlignRightTooltip => T("Align Right", "Rechtsbündig");
    public string LetterSpacingLabel => T("Letter spacing", "Zeichenabstand");
    public string LineSpacingLabel => T("Line spacing", "Zeilenabstand");
    public string ParagraphReflowHeader => T("PARAGRAPH & REFLOW", "ABSATZ & UMBRUCH");
    public string AutoReflowLabel => T("Auto-Reflow Paragraph", "Automatischer Umbruch");
    public string FindReplaceWatermark => T("Find text...", "Text suchen...");
    public string MatchCaseTooltip => T("Match Case", "Groß-/Kleinschreibung");
    public string DocumentActionsHeader => T("DOCUMENT ACTIONS", "DOKUMENT-AKTIONEN");
    public string DetectTextBlocksButton => T("Detect Text Blocks", "Textblöcke erkennen");
    public string RunOcrButton => T("Run OCR", "OCR ausführen");
    public string RewriteBlockButton => T("Edit / Rewrite Block", "Block bearbeiten");
    public string SelectBlockBadge => T("Select a text block in Edit Text mode", "Textblock im Bearbeitungsmodus wählen");

    // Status bar & presets
    public string ReadyStatusText => T(
        "Ready • Open a PDF or drop a file into the canvas",
        "Bereit • PDF öffnen oder in die Arbeitsfläche ziehen");
    public string FitWidthPreset => T("Fit Width", "Seitenbreite");
    public string FitPagePreset => T("Fit Page", "Ganze Seite");

    // Settings dialog
    public string SettingsTitle => T("Settings", "Einstellungen");
    public string LanguageLabel => T("Language", "Sprache");
    public string LanguageDescription => T(
        "Choose the display language for menus, tools, and status messages.",
        "Wählen Sie die Anzeigesprache für Menüs, Werkzeuge und Statusmeldungen.");
    public string ShortcutsHeading => T("Keyboard Shortcuts", "Tastenkürzel");
    public string ShortcutsBody => T(
        "Ctrl+O Open  •  Ctrl+S Save  •  Ctrl+Z/Y Undo/Redo\nCtrl+F Find  •  T Edit Text  •  V Select  •  Del Remove Image",
        "Strg+O Öffnen  •  Strg+S Speichern  •  Strg+Z/Y Rückgängig\nStrg+F Suchen  •  T Text bearbeiten  •  V Auswahl  •  Entf Bild löschen");
    public string CloseButton => T("Close", "Schließen");

    // Dynamic status messages
    public string MsgOpenPdfFirst => T("Open a PDF document first.", "Bitte zuerst ein PDF-Dokument öffnen.");
    public string MsgSampleLoaded => T("Loaded built-in sample PDF.", "Beispiel-PDF geladen.");
    public string MsgAlreadySaved => T("All changes already saved.", "Alle Änderungen bereits gespeichert.");
    public string MsgSavedFile(string fileName) => T($"Saved {fileName}", $"{fileName} gespeichert");
    public string MsgOpenedFile(string fileName) => T($"Opened {fileName}", $"{fileName} geöffnet");
    public string MsgSaveFailed(string err) => T($"Save failed: {err}", $"Speichern fehlgeschlagen: {err}");
    public string MsgUndidEdit => T("Undid edit.", "Änderung rückgängig gemacht.");
    public string MsgRedidEdit => T("Redid edit.", "Änderung wiederhergestellt.");
    public string MsgChooseImage => T("Choose an image file to place on the page...", "Bilddatei zum Platzieren auswählen...");
    public string MsgHighlightHint => T("Highlight active • Drag across text or an area on the page.", "Hervorheben aktiv • Über Text oder Bereich ziehen.");
    public string MsgCommentHint => T("Comment active • Click anywhere to drop a note, or click a pin.", "Kommentar aktiv • Klicken für Notiz oder auf Pin klicken.");
    public string MsgRedactHint => T("Redact active • Drag across sensitive content to permanently blackout & strip it.", "Schwärzen aktiv • Bereich zum dauerhaften Entfernen aufziehen.");
    public string MsgEditTextHint(int count, int page) => T(
        $"Edit Text active • Click any block to edit ({count} detected on page {page})",
        $"Textbearbeitung aktiv • Block anklicken ({count} auf Seite {page} erkannt)");
    public string MsgDetectedBlocks(int count, int page) => T(
        $"Detected {count} text block(s) on page {page}.",
        $"{count} Textblock/-blöcke auf Seite {page} erkannt.");
    public string MsgImageReady(string name) => T(
        $"Image \"{name}\" ready • Drag a rectangle on a page to place it.",
        $"Bild \"{name}\" bereit • Auf einer Seite aufziehen oder klicken.");
    public string MsgImageInserted(string name, int page) => T(
        $"Inserted \"{name}\" on page {page} • Drag to move, handles to resize, Del to remove.",
        $"\"{name}\" auf Seite {page} eingefügt • Ziehen zum Verschieben, Griffe zum Skalieren, Entf zum Löschen.");
    public string MsgImageUpdated(int page) => T($"Updated image on page {page}.", $"Bild auf Seite {page} aktualisiert.");
    public string MsgImageRemoved(int page) => T($"Removed image from page {page}.", $"Bild von Seite {page} entfernt.");
    public string MsgRunningOcr(int page) => T($"Running OCR on page {page}...", $"OCR läuft auf Seite {page}...");
    public string MsgOcrSuccess(int page, int lines, int chars, double conf) => T(
        $"OCR page {page}: {lines} line(s), {chars} chars ({conf:F0}% conf).",
        $"OCR Seite {page}: {lines} Zeile(n), {chars} Zeichen ({conf:F0}% Konf.).");
    public string MsgOcrNoText(int page) => T(
        $"OCR page {page}: no text detected.",
        $"OCR Seite {page}: kein Text erkannt.");
}
