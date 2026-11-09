# RevitDialogDismisser

RevitDialogDismisser is a small Windows utility for closing repetitive Autodesk Revit warning dialogs. It watches running Revit instances and dismisses only the messages listed in `config.json`.

It runs as a separate application, not a Revit add-in. When a message matches, it requests the dialog's normal **Close (X)** action. It does not select **Yes** or **No**.

When opening a godawful model filled with cad, pdf, ifc, and all manners of error-filled cancerous files, this helps in speeding up the model loading.

## Features

- **Automatic dialog dismissal** — Closes warning dialogs matching your configured messages.
- **Multiple Revit instances** — Monitors all running `Revit.exe` processes.
- **Fuzzy text matching** — Tolerates minor differences in spelling, punctuation, and formatting.
- **Customizable warnings** — Add or remove warning messages through `config.json`.
- **Live configuration reload** — Changes take effect without restarting the application.
- **Background monitoring** — Continuously checks for matching dialogs while the utility is running.
- **Safety checks** — Helps prevent unrelated dialogs and Revit's main window from being closed.

## Download & Installation

Get the latest build from [Releases](../../releases).

| Release                | Requirement                                                                                                 |
| ---------------------- | ----------------------------------------------------------------------------------------------------------- |
| **Standalone**   | Windows x64. No separate .NET installation needed.                                                          |
| **RequiresNET8** | Windows x64 with [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) installed. |

Extract the ZIP. Keep both files together:

```text
RevitDialogDismisser.exe
config.json
```

Run `RevitDialogDismisser.exe` and leave the console open while using Revit. Press **Ctrl+C** in the console to stop it.

## Configuration

Open `config.json` and enter the full text of each warning you want to dismiss:

```json
{
  "similarityThreshold": 0.90,
  "dialogTexts": [
    "Some numerical data within the imported file was out of range. This numerical data has been truncated.",
    "Import detected no valid elements in the file's Paper space. Do you want to import from the Model space?"
  ]
}
```

- `dialogTexts` is the list of permitted warning messages. Add another quoted string, separated by a comma, for each new warning.
- `similarityThreshold` accepts values from `0.90` to `1.00`. Higher values require a closer text match.

Changes are picked up automatically when you save the file. The matcher ignores differences in capitalization, punctuation and spacing, and allows minor text variations. It also checks for changes in critical words, numbers and the direction of Paper-space/Model-space prompts.

**Only add messages you are comfortable dismissing without reviewing them.**

## Build from source

Requires Windows and the [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0). Run the commands below from the folder containing `RevitDialogDismisser.csproj`.

To run the built-in text-matching tests:

```powershell
dotnet run --project .\RevitDialogDismisser.csproj -- --self-test
```

### Publish a standalone EXE

The following builds a compressed, self-contained Windows x64 EXE. The release files go directly into `bin\Release\publish`; no extra distribution folder is needed.

```powershell
Remove-Item .\bin\Release\publish -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish .\RevitDialogDismisser.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishReadyToRun=false -p:PublishTrimmed=false -p:DebugType=none -p:DebugSymbols=false -o .\bin\Release\publish

if ($LASTEXITCODE -ne 0) { throw "Publish failed" }
Copy-Item .\config.json .\bin\Release\publish\config.json -Force
```

The files to distribute are:

```text
bin\Release\publish\
├── RevitDialogDismisser.exe
└── config.json
```

To keep `config.json` editable beside the EXE, make sure your `.csproj` includes this item:

```xml
<ItemGroup>
  <None Update="config.json">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>
    <ExcludeFromSingleFile>true</ExcludeFromSingleFile>
  </None>
</ItemGroup>
```

For a smaller **framework-dependent** build, change `--self-contained true` to `--self-contained false`, remove `-p:IncludeNativeLibrariesForSelfExtract=true` if desired, and use a separate output folder such as `bin\Release\publish-net8`. That build requires .NET 8 Desktop Runtime on the recipient's PC.

## Notes

- A dialog that cannot be cancelled may stay open. The watcher will not choose Yes or No as a fallback.
- Matching is intentionally limited to dialogs belonging to `Revit.exe`, but fuzzy matching is not infallible.
- Closing a warning does not fix the underlying issue in an imported or linked CAD file.
- Test new configuration entries before relying on them during production work.

This project is independent of Autodesk and is not affiliated with or endorsed by Autodesk.
