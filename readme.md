
# RevitDialogDismisser

**Automatically dismiss selected Autodesk Revit warning dialogs using configurable text matching.**

RevitDialogDismisser is a lightweight Windows utility that monitors running Autodesk Revit instances and automatically dismisses warning dialogs specified in `config.json`.

It runs independently of Revit, requiring **no add-in installation**, and attempts to close matching dialogs through the equivalent of the window's **X** button without automatically selecting **Yes** or **No**.

When opening a godawful model filled with cad, pdf, ifc, and all manners of error-filled cancerous files, this helps in speeding up the model loading.

## Features

- **Automatic dialog dismissal** — Closes warning dialogs matching your configured messages.
- **Multiple Revit instances** — Monitors all running `Revit.exe` processes.
- **Fuzzy text matching** — Tolerates minor differences in spelling, punctuation, and formatting.
- **Customizable warnings** — Add or remove warning messages through `config.json`.
- **Live configuration reload** — Changes take effect without restarting the application.
- **Background monitoring** — Continuously checks for matching dialogs while the utility is running.
- **Safety checks** — Helps prevent unrelated dialogs and Revit's main window from being closed.

## Installation

1. Download the latest version from [Releases](../../releases).
2. Extract both files into the same directory:

   ```text
   RevitDialogDismisser/
   ├── RevitDialogDismisser.exe
   └── config.json
   ```
3. Run `RevitDialogDismisser.exe`.
4. Keep the console window open while working in Revit. Press **Ctrl+C** to stop monitoring.

**Requirements:** Windows x64 and Autodesk Revit.

Self-contained releases do not require a separate .NET installation. Framework-dependent releases require the [Microsoft .NET 8 Desktop Runtime (Windows x64)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0).

## Configuration

Open `config.json` with any text editor.

Example:

```json
{
  "similarityThreshold": 0.90,
  "dialogTexts": [
    "Some numerical data within the imported file was out of range. This numerical data has been truncated.",
    "Import detected no valid elements in the file's Paper space. Do you want to import from the Model space?"
  ]
}
```

**Configuration parameters**

- `similarityThreshold` — Controls text matching sensitivity, between `0.90` and `1.00`. Higher values require closer matches.
- `dialogTexts` — An array containing the complete warning messages you want automatically dismissed.

To add another warning, insert its full message as a new quoted string, separated by commas.

The utility automatically reloads the configuration whenever the file is saved.

### How matching works

The application compares each configured warning against the text detected in Revit's dialog windows.

It normalizes capitalization, punctuation, and spacing, then uses fuzzy matching to accommodate minor wording variations.

Additional checks help distinguish messages that look similar but have different meanings, such as reversed Paper-space and Model-space questions.

## Building from Source

Requires Windows and the [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0).

The source project is currently named `RevitDialogueWatcher.csproj`. To publish the executable using the new name:

```powershell
dotnet publish .\RevitDialogueWatcher.csproj -c Release -r win-x64 --self-contained true -p:AssemblyName=RevitDialogDismisser -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=none -p:DebugSymbols=false -o .\publish
```

For distribution, place only `RevitDialogDismisser.exe` and `config.json` in a clean folder.

You can also run the built-in text-matching tests:

```powershell
dotnet run --project .\RevitDialogueWatcher.csproj -- --self-test
```

## Limitations

- Some Revit dialogs cannot be dismissed through the window's close/cancel operation. The utility does not force a Yes or No response.
- Fuzzy matching is not perfect. Add only warnings you are comfortable automatically dismissing.
- Automatically dismissing warnings does not resolve underlying CAD import problems.
- Behavior may vary depending on the Revit version and Windows permissions.

**Disclaimer:** This is an independent utility and is not affiliated with or endorsed by Autodesk. Revit is a trademark of Autodesk, Inc.
