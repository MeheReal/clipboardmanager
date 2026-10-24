# Clipboard Manager

A Windows-only .NET 10 WPF clipboard history utility. It starts in the notification area, captures plain text clipboard changes, and opens a compact searchable topmost command bar with **Alt+V**. It shows at most the five newest matching items in single-line rows. Press **Enter** or double-click an item to restore its text to the clipboard and send **Ctrl+V** to the window that was active before the overlay opened.

## Build and run

Requirements: Windows 10 or later and the .NET 10 SDK.

    dotnet restore
    dotnet build -c Release
    dotnet run -c Release

## Release publish

Release builds obfuscate the compiled application assembly with Obfuscar before single-file bundling. WPF binding properties and events remain unchanged; Release debug symbols are disabled.

Publish a self-contained Windows x64 executable with:

    dotnet publish .\ClipboardManager.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -o .\publish\win-x64-mehereal

## Automatic updates from GitHub Releases

The updater is configured for the public repository https://github.com/MeheReal/clipboardmanager. It reads the latest published release from https://api.github.com/repos/MeheReal/clipboardmanager/releases/latest. No direct download URL or GitHub token needs to be embedded.

Each release must use a tag matching the version in ClipboardManager.csproj, for example project version 1.0.0 and GitHub tag v1.0.0. Publish a normal release (not a draft or prerelease) and attach the self-contained executable as a release asset named exactly ClipboardManager.exe. The updater compares versions and verifies the asset size and SHA-256 digest before asking a helper process to replace the running executable and restart the app.

To publish the first release:

1. Push the project source to the main branch of MeheReal/clipboardmanager.
2. Build the standalone app using the command above.
3. On GitHub, open Releases, choose Draft a new release, create tag v1.0.0, and select main as the target.
4. Attach publish/win-x64-mehereal/ClipboardManager.exe and publish the release.

To publish a future update, increment the Version in ClipboardManager.csproj (for example, from 1.0.0 to 1.0.1), push the source change, build again, and publish a GitHub release tagged v1.0.1 with the new ClipboardManager.exe asset. Keep the app installed in a user-writable folder so its updater can replace the executable without administrator permission.
The SQLite database and log are stored under %LOCALAPPDATA%\ClipboardManager. History is limited to 1,000 entries, adjacent duplicate copies are skipped, and individual text entries over 1,000,000 characters are ignored to keep storage bounded. The tray menu can pause capture or exit the application.

## Source layout

- App.xaml and App.xaml.cs: single-instance startup, service composition, and app lifetime.
- MainWindow.xaml and MainWindow.xaml.cs: searchable overlay, focus handling, dismissal, and paste flow.
- GlobalUsings.cs: explicit framework namespaces used alongside WPF and WinForms.
- Models/: clipboard entry and item type.
- ViewModels/: observable history and in-memory filtering.
- Services/ClipboardDatabase.cs: SQLite schema, retention, reads, and writes.
- Services/ClipboardCaptureService.cs: clipboard retries and capture.
- Services/NativeMessageHost.cs and NativeMethods.cs: clipboard listener, global hotkey, foreground restoration, and input injection.
- Services/ClipboardPasteService.cs: clipboard write and paste support.
- Services/TrayIconService.cs: notification area menu.




