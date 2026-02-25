# InstallGWF

CLI tool to install Garmin Watch Face Builder app files (`.prg`) and settings files (`.SET`) to a Garmin device.

## macOS

### Prerequisites

1. Install .NET 8 SDK.
2. Install Homebrew `libmtp`:

```bash
brew install libmtp
```

### Build

```bash
dotnet build InstallGWF.csproj
```

### Run

Use `--` so app arguments are passed to `InstallGWF`:

```bash
dotnet run --project InstallGWF.csproj -- ~/Downloads/file.prg
```

Supported inputs:

1. `.prg` file
2. `.SET` file
3. `.zip` containing one or more `.prg` files
4. `https://` Watch Face Builder URL

Examples:

```bash
dotnet run --project InstallGWF.csproj -- ~/Downloads/file.prg
dotnet run --project InstallGWF.csproj -- ~/Downloads/file.SET
dotnet run --project InstallGWF.csproj -- ~/Downloads/file.zip
dotnet run --project InstallGWF.csproj -- https://garmin.watchfacebuilder.com/watchface/xxxxx/
```

### Logging and Diagnostic Options

Enable app-level verbose logs:

```bash
dotnet run --project InstallGWF.csproj -- --verbose ~/Downloads/file.prg
```

Equivalent env var:

```bash
INSTALLGWF_VERBOSE=1 dotnet run --project InstallGWF.csproj -- ~/Downloads/file.prg
```

Enable native libmtp debug level:

```bash
INSTALLGWF_LIBMTP_DEBUG=1 dotnet run --project InstallGWF.csproj -- ~/Downloads/file.prg
```

Show raw libmtp stderr output (normally suppressed):

```bash
INSTALLGWF_LIBMTP_STDERR=1 dotnet run --project InstallGWF.csproj -- ~/Downloads/file.prg
```

### Notes

1. On macOS, the app tries `libmtp` first, then falls back to mounted-volume mode under `/Volumes`.
2. If your device is not detected, unplug/replug and press Enter in the app prompt to retry MTP detection.
