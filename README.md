# MDExport

A minimalist, dark-themed Markdown editor for Windows with live preview and export to **HTML**, **PDF**, and **DOCX**.

Built with .NET 8 / WPF. Distributed as an MSI built with WiX 4.

## Features

- **Two-pane layout** — AvalonEdit on the left, WebView2 live preview on the right.
- **Live preview** with 250 ms debounce.
- **Export** to:
  - **HTML** — self-contained styled HTML document.
  - **PDF** — via WebView2's `PrintToPdfAsync`, so the PDF matches the preview exactly.
  - **DOCX** — generated directly from the Markdig AST using the OpenXML SDK (no Word required).
- **Local images** — `![alt](images/pic.png)` resolves relative to the saved `.md` file (absolute and `file://` paths work too). Images are embedded into the preview and into every export, so HTML/PDF/DOCX output is self-contained. Relative paths need the document saved first; unresolved or remote URLs are left as-is.
- **Modern dark theme** for the app chrome (preview/export uses a clean light "GitHub-like" stylesheet, since that's what reads well on a printed page).
- **WiX 4 MSI** installer with Start Menu shortcut and standard add/remove programs entry.

## Project layout

```
MDExport.sln
MDExport/                          # WPF app
  MDExport.csproj
  App.xaml / App.xaml.cs
  MainWindow.xaml / MainWindow.xaml.cs
  Themes/DarkTheme.xaml            # palette + control styles
  Assets/PreviewTemplate.html      # HTML template used for preview, HTML and PDF export
  Services/
    MarkdownRenderer.cs            # Markdig pipeline + template wrapping
    ImageEmbedder.cs               # Resolves local image paths → inline data: URIs
    HtmlExporter.cs
    PdfExporter.cs                 # WebView2 PrintToPdfAsync
    DocxExporter.cs                # Walks Markdig AST → OpenXML
  app.manifest
MDExport.Installer/                # WiX 4 MSI
  MDExport.Installer.wixproj
  Product.wxs
```

## Dependencies

| Package | Purpose |
|---------|---------|
| Markdig | Markdown parsing + HTML rendering |
| AvalonEdit | Code editor with line numbers / monospace font |
| Microsoft.Web.WebView2 | Embedded Edge for preview + PDF rendering |
| DocumentFormat.OpenXml | DOCX generation |
| WixToolset.Sdk 5.0.2 | MSI build SDK |
| WixToolset.UI.wixext 4.0.5 | Standard install dialogs |

## Prerequisites

- **.NET 8 SDK**
- **Windows 10 / 11**
- **WebView2 Runtime** — preinstalled on Windows 11 and most up-to-date Windows 10 systems.
- For the MSI: **WiX 5** is pulled in via the `WixToolset.Sdk` SDK reference, no separate install needed.

## Build & Run

### 1. Run the app

```powershell
dotnet run --project MDExport
```

### 2. Publish self-contained binaries (used by the MSI)

```powershell
dotnet publish MDExport -c Release -r win-x64 --self-contained false -o MDExport\bin\Release\net8.0-windows\win-x64\publish
```

> Use `--self-contained true` if you want the MSI to ship the .NET runtime inline (~70 MB heavier). For most desktops, the framework-dependent build above is plenty.

### 3. Build the MSI

```powershell
dotnet build MDExport.Installer -c Release
```

The MSI is written to `MDExport.Installer\bin\x64\Release\MDExport-1.0.0-x64.msi`.

### 4. Install

Double-click the MSI, or:

```powershell
msiexec /i MDExport.Installer\bin\x64\Release\MDExport-1.0.0-x64.msi
```

The installer requires elevation (per-machine install to `Program Files\MDExport`) and creates a Start Menu shortcut.

## Design notes

- **Why WebView2 for both preview and PDF?** WebView2 is already required for the live preview, and it ships `CoreWebView2.PrintToPdfAsync`, which renders the *same* HTML the user sees with proper CSS, web fonts, and print media queries. That removes the need for any extra PDF library.
- **Why a custom DOCX writer instead of HTML→DOCX?** Going through HTML loses Markdown structure (headings, code, lists); walking the Markdig AST and emitting OpenXML directly keeps the document semantic and small.
- **Dark chrome, light preview.** The editor and chrome are dark for low eye-strain. The preview matches what the export will look like on paper and on the web — both of which are typically light. This is the same split VSCode uses for its Markdown preview.

## Customizing

- **Colors / theme** — edit `MDExport/Themes/DarkTheme.xaml`.
- **Preview & export styling** — edit `MDExport/Assets/PreviewTemplate.html`.
- **Markdown extensions** — `MarkdownRenderer.Pipeline` uses `UseAdvancedExtensions()`; tweak there to enable/disable tables, footnotes, math, etc.

## Known limitations

- DOCX export uses literal bullet characters and tab stops for lists rather than registering a `numbering.xml` part. Renders fine in Word but doesn't carry "list style" metadata.
- PDF page size is hard-coded to US Letter, 0.5" margins. Edit `PdfExporter.cs` to change.
