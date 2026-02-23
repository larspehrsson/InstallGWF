using MediaDevices;
using System.IO.Compression;
using System.Runtime.Versioning;

var target = GetTarget();
if (target is null)
{
    return;
}

string? filename;
if (args.Length == 0)
{
    Console.WriteLine("Type or drag URL, zip file, prg file, or set file here, then press Enter:");
    filename = Console.ReadLine();
}
else
{
    filename = args[0];
}

if (string.IsNullOrWhiteSpace(filename))
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  InstallGWF <file.zip>");
    Console.WriteLine("  InstallGWF <file.prg>");
    Console.WriteLine("  InstallGWF <file.SET>");
    Console.WriteLine("  InstallGWF https://garmin.watchfacebuilder.com/watchface/xxxxx/");
    return;
}

if (filename.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
{
    Console.Write("Downloading...");
    if (!filename.Contains("file=app", StringComparison.OrdinalIgnoreCase))
    {
        filename += (filename.Contains('?') ? "&" : "?") + "file=app";
    }

    using var httpClient = new HttpClient();
    await using var stream = await httpClient.GetStreamAsync(filename);

    var tmpZipPath = Path.ChangeExtension(Path.GetTempFileName(), ".zip");
    await using (var fileStream = new FileStream(tmpZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
    {
        await stream.CopyToAsync(fileStream);
    }

    filename = tmpZipPath;
    Console.WriteLine("done.");
}

var ext = Path.GetExtension(filename);

if (".zip".Equals(ext, StringComparison.OrdinalIgnoreCase))
{
    Console.Write("Unzipping...");
    try
    {
        using var za = ZipFile.OpenRead(filename);
        var foundPrg = false;

        foreach (var entry in za.Entries)
        {
            if (!entry.FullName.EndsWith(".prg", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foundPrg = true;
            var tmpPrgPath = Path.ChangeExtension(Path.GetTempFileName(), ".prg");
            entry.ExtractToFile(tmpPrgPath, overwrite: true);

            var appName = Path.GetFileNameWithoutExtension(entry.FullName);
            Console.Write("Copying app...");
            target.UploadPrg(tmpPrgPath, appName);
            Console.WriteLine($"{appName}.prg done.");
        }

        if (!foundPrg)
        {
            Console.WriteLine("No .prg file found in zip.");
        }
    }
    catch
    {
        Console.WriteLine("Invalid zip file.");
    }
}
else if (".prg".Equals(ext, StringComparison.OrdinalIgnoreCase))
{
    var appName = Path.GetFileNameWithoutExtension(filename);
    Console.Write("Copying app...");
    target.UploadPrg(filename, appName);
    Console.WriteLine($"{appName}.prg done.");
}
else if (".set".Equals(ext, StringComparison.OrdinalIgnoreCase))
{
    var settingName = Path.GetFileNameWithoutExtension(filename);
    Console.Write("Copying setting file...");
    target.UploadSet(filename, settingName);
    Console.WriteLine($"{settingName}.SET done.");
}
else
{
    Console.WriteLine("Invalid input file.");
}

IGarminTarget? GetTarget()
{
    if (OperatingSystem.IsWindows())
    {
        return SelectWindowsDevice();
    }

    return SelectMountedVolume();
}

[SupportedOSPlatform("windows")]
IGarminTarget? SelectWindowsDevice()
{
    var devices = MediaDevice.GetDevices();
    while (true)
    {
        var list = devices.ToList();
        if (list.Count == 0)
        {
            Console.WriteLine("No Garmin device found. Plug in your Garmin device.");
            Console.WriteLine("Press Enter to refresh or Ctrl+C to exit.");
            Console.ReadLine();
            devices = MediaDevice.GetDevices();
            continue;
        }

        Console.WriteLine("Available devices:");
        for (var i = 0; i < list.Count; i++)
        {
            Console.WriteLine($"{i + 1}: {list[i].FriendlyName}");
        }

        if (list.Count == 1 && "Garmin".Equals(list[0].Manufacturer, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Auto selected: {list[0].FriendlyName}");
            return new WindowsGarminTarget(list[0]);
        }

        Console.WriteLine("Enter number to select the device (or press Enter to refresh):");
        var sel = Console.ReadLine();
        if (int.TryParse(sel, out var iSel) && iSel > 0 && iSel <= list.Count)
        {
            return new WindowsGarminTarget(list[iSel - 1]);
        }

        devices = MediaDevice.GetDevices();
    }
}

IGarminTarget? SelectMountedVolume()
{
    while (true)
    {
        var candidates = FindGarminMounts();
        if (candidates.Count == 0)
        {
            Console.WriteLine("No mounted Garmin volume found.");
            Console.WriteLine("On macOS, ensure your device appears under /Volumes and contains a GARMIN folder.");
            Console.WriteLine("Enter a Garmin mount path manually, press Enter to refresh, or Ctrl+C to exit:");
            var input = Console.ReadLine();

            if (!string.IsNullOrWhiteSpace(input))
            {
                if (Directory.Exists(Path.Combine(input, "GARMIN")))
                {
                    return new MountedGarminTarget(input);
                }

                Console.WriteLine("That path is not a valid Garmin mount.");
            }

            continue;
        }

        if (candidates.Count == 1)
        {
            Console.WriteLine($"Using mounted Garmin volume: {candidates[0]}");
            return new MountedGarminTarget(candidates[0]);
        }

        Console.WriteLine("Available Garmin mounts:");
        for (var i = 0; i < candidates.Count; i++)
        {
            Console.WriteLine($"{i + 1}: {candidates[i]}");
        }

        Console.WriteLine("Enter number to select mount (or press Enter to refresh):");
        var sel = Console.ReadLine();
        if (int.TryParse(sel, out var iSel) && iSel > 0 && iSel <= candidates.Count)
        {
            return new MountedGarminTarget(candidates[iSel - 1]);
        }
    }
}

List<string> FindGarminMounts()
{
    var roots = new List<string>();

    if (OperatingSystem.IsMacOS())
    {
        roots.Add("/Volumes");
    }
    else if (OperatingSystem.IsLinux())
    {
        var user = Environment.UserName;
        roots.Add(Path.Combine("/media", user));
        roots.Add(Path.Combine("/run/media", user));
    }

    var mounts = new List<string>();
    foreach (var root in roots)
    {
        if (!Directory.Exists(root))
        {
            continue;
        }

        foreach (var dir in Directory.GetDirectories(root))
        {
            if (Directory.Exists(Path.Combine(dir, "GARMIN")))
            {
                mounts.Add(dir);
            }
        }
    }

    return mounts;
}

interface IGarminTarget
{
    void UploadPrg(string sourceFile, string nameWithoutExtension);
    void UploadSet(string sourceFile, string nameWithoutExtension);
}

[SupportedOSPlatform("windows")]
sealed class WindowsGarminTarget(MediaDevice device) : IGarminTarget
{
    private readonly MediaDevice _device = device;

    public void UploadPrg(string sourceFile, string nameWithoutExtension)
    {
        Upload(sourceFile, $"GARMIN\\Apps\\{nameWithoutExtension}.prg");
    }

    public void UploadSet(string sourceFile, string nameWithoutExtension)
    {
        Upload(sourceFile, $"GARMIN\\Apps\\Settings\\{nameWithoutExtension}.SET");
    }

    private void Upload(string sourceFile, string relativePath)
    {
        _device.Connect();
        try
        {
            var drive = _device.GetDrives()?.FirstOrDefault();
            var root = drive?.RootDirectory.Name ?? "Primary";
            var destFile = $"{root}\\{relativePath}";

            if (_device.FileExists(destFile))
            {
                _device.DeleteFile(destFile);
            }

            _device.UploadFile(sourceFile, destFile);
        }
        finally
        {
            _device.Disconnect();
        }
    }
}

sealed class MountedGarminTarget(string mountPath) : IGarminTarget
{
    private readonly string _mountPath = mountPath;

    public void UploadPrg(string sourceFile, string nameWithoutExtension)
    {
        Upload(sourceFile, Path.Combine(_mountPath, "GARMIN", "Apps", $"{nameWithoutExtension}.prg"));
    }

    public void UploadSet(string sourceFile, string nameWithoutExtension)
    {
        Upload(sourceFile, Path.Combine(_mountPath, "GARMIN", "Apps", "Settings", $"{nameWithoutExtension}.SET"));
    }

    private static void Upload(string sourceFile, string destinationFile)
    {
        var parent = Path.GetDirectoryName(destinationFile);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.Copy(sourceFile, destinationFile, overwrite: true);
    }
}
