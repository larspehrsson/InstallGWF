using MediaDevices;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Reflection;

IGarminTarget? target = null;
try
{
    Diag.Configure(args);
    target = GetTarget();
    if (target is null)
    {
        return;
    }

    await RunAsync(target, args);
}
finally
{
    target?.Dispose();
}

static async Task RunAsync(IGarminTarget target, string[] args)
{
    Diag.Info($"Args: {(args.Length == 0 ? "<none>" : string.Join(" | ", args.Select((a, i) => $"[{i}]='{a}'")))}");

    var effectiveArgs = args.Where(a => !string.Equals(a, "--verbose", StringComparison.OrdinalIgnoreCase)).ToArray();
    string? filename;
    if (effectiveArgs.Length == 0)
    {
        Diag.Info("Type or drag URL, zip file, prg file, or set file here, then press Enter:");
        filename = Console.ReadLine();
    }
    else
    {
        if (effectiveArgs.Length >= 2 && effectiveArgs[0].EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            Diag.Info($"Detected project path argument '{effectiveArgs[0]}'. Using next argument as input file.");
            filename = effectiveArgs[1];
        }
        else
        {
            filename = effectiveArgs[0];
        }
    }

    if (string.IsNullOrWhiteSpace(filename))
    {
        Diag.Info("Usage:");
        Diag.Info("  InstallGWF <file.zip>");
        Diag.Info("  InstallGWF <file.prg>");
        Diag.Info("  InstallGWF <file.SET>");
        Diag.Info("  InstallGWF https://garmin.watchfacebuilder.com/watchface/xxxxx/");
        return;
    }

    filename = ExpandHomePath(filename.Trim());
    Diag.Info($"Resolved input: '{filename}'");
    if (!filename.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
        var fullPath = Path.GetFullPath(filename);
        Diag.Info($"Absolute path: '{fullPath}'");
        Diag.Info($"Exists: {File.Exists(fullPath)}");
    }

    if (filename.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
        Diag.Info("Downloading...");
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
        Diag.Info("Download complete.");
    }

    var ext = Path.GetExtension(filename);
    Diag.Info($"Detected extension: '{ext}'");

    if (".zip".Equals(ext, StringComparison.OrdinalIgnoreCase))
    {
        Diag.Info("Unzipping...");
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
                Diag.Debug($"Extracted '{entry.FullName}' to '{tmpPrgPath}'.");

                var appName = Path.GetFileNameWithoutExtension(entry.FullName);
                Diag.Info("Copying app...");
                target.UploadPrg(tmpPrgPath, appName);
                Diag.Info($"{appName}.prg done.");
            }

            if (!foundPrg)
            {
                Diag.Error("No .prg file found in zip.");
            }
        }
        catch (Exception ex)
        {
            Diag.Error($"Invalid zip file. ({ex.Message})");
        }
    }
    else if (".prg".Equals(ext, StringComparison.OrdinalIgnoreCase))
    {
        var appName = Path.GetFileNameWithoutExtension(filename);
        Diag.Info($"Uploading PRG '{filename}' as '{appName}.prg'.");
        Diag.Info("Copying app...");
        target.UploadPrg(filename, appName);
        Diag.Info($"{appName}.prg done.");
    }
    else if (".set".Equals(ext, StringComparison.OrdinalIgnoreCase))
    {
        var settingName = Path.GetFileNameWithoutExtension(filename);
        Diag.Info($"Uploading SET '{filename}' as '{settingName}.SET'.");
        Diag.Info("Copying setting file...");
        target.UploadSet(filename, settingName);
        Diag.Info($"{settingName}.SET done.");
    }
    else
    {
        Diag.Error("Invalid input file. Supported: .zip, .prg, .SET, or https:// URL.");
        Diag.Info("Tip: for dotnet run, pass app args after '--': dotnet run --project InstallGWF.csproj -- ~/Downloads/file.prg");
    }
}

IGarminTarget? GetTarget()
{
    Diag.Info($"OS: {RuntimeInformation.OSDescription}");

    if (OperatingSystem.IsWindows())
    {
        Diag.Info("Selecting Windows MediaDevices backend.");
        return SelectWindowsDevice();
    }

    if (OperatingSystem.IsMacOS())
    {
        // On macOS prefer MTP if available, then fallback to mounted volumes.
        Diag.Info("Selecting macOS backend: trying libmtp first.");
        var mtp = TrySelectLibMtpDevice();
        if (mtp is not null)
        {
            return mtp;
        }
        Diag.Info("libmtp unavailable or no device detected; falling back to mounted volume mode.");
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
            Diag.Info("No Garmin device found. Plug in your Garmin device.");
            Diag.Info("Press Enter to refresh or Ctrl+C to exit.");
            Console.ReadLine();
            devices = MediaDevice.GetDevices();
            continue;
        }

        Diag.Info("Available devices:");
        for (var i = 0; i < list.Count; i++)
        {
            Diag.Info($"{i + 1}: {list[i].FriendlyName}");
        }

        if (list.Count == 1 && "Garmin".Equals(list[0].Manufacturer, StringComparison.OrdinalIgnoreCase))
        {
            Diag.Info($"Auto selected: {list[0].FriendlyName}");
            return new WindowsGarminTarget(list[0]);
        }

        Diag.Info("Enter number to select the device (or press Enter to refresh):");
        var sel = Console.ReadLine();
        if (int.TryParse(sel, out var iSel) && iSel > 0 && iSel <= list.Count)
        {
            return new WindowsGarminTarget(list[iSel - 1]);
        }

        devices = MediaDevice.GetDevices();
    }
}

IGarminTarget? TrySelectLibMtpDevice()
{
    if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
    {
        return null;
    }

    var backend = LibMtpBackend.TryCreate();
    if (backend is null)
    {
        Diag.Info("libmtp not available (install with: brew install libmtp). Falling back to mounted volume mode.");
        return null;
    }

    try
    {
        Diag.Info("libmtp initialized, checking connected MTP devices.");
        if (!backend.HasAnyDevice())
        {
            Diag.Info("No MTP devices found by libmtp.");
            backend.Dispose();
            return null;
        }

        Diag.Info("Using libmtp backend.");
        return backend;
    }
    catch
    {
        backend.Dispose();
        return null;
    }
}

IGarminTarget? SelectMountedVolume()
{
    while (true)
    {
        if (OperatingSystem.IsMacOS())
        {
            Diag.Debug("Retrying libmtp detection from fallback loop.");
            var mtp = TrySelectLibMtpDevice();
            if (mtp is not null)
            {
                return mtp;
            }
        }

        var candidates = FindGarminMounts();
        if (candidates.Count == 0)
        {
            Diag.Info("No mounted Garmin volume found.");
            Diag.Info("On macOS, ensure your device appears under /Volumes and contains a GARMIN folder.");
            Diag.Info("Enter a Garmin mount path manually, press Enter to refresh, or Ctrl+C to exit:");
            var input = Console.ReadLine();

            if (!string.IsNullOrWhiteSpace(input))
            {
                if (Directory.Exists(Path.Combine(input, "GARMIN")))
                {
                    return new MountedGarminTarget(input);
                }

                Diag.Error("That path is not a valid Garmin mount.");
            }

            continue;
        }

        if (candidates.Count == 1)
        {
            Diag.Info($"Using mounted Garmin volume: {candidates[0]}");
            return new MountedGarminTarget(candidates[0]);
        }

        Diag.Info("Available Garmin mounts:");
        for (var i = 0; i < candidates.Count; i++)
        {
            Diag.Info($"{i + 1}: {candidates[i]}");
        }

        Diag.Info("Enter number to select mount (or press Enter to refresh):");
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
        Diag.Debug($"Scanning mount root: {root}");
        if (!Directory.Exists(root))
        {
            Diag.Debug($"Mount root does not exist: {root}");
            continue;
        }

        foreach (var dir in Directory.GetDirectories(root))
        {
            if (Directory.Exists(Path.Combine(dir, "GARMIN")))
            {
                mounts.Add(dir);
                Diag.Info($"Found Garmin mount: {dir}");
            }
        }
    }

    return mounts;
}

static string ExpandHomePath(string input)
{
    if (!input.StartsWith("~", StringComparison.Ordinal))
    {
        return input;
    }

    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    if (string.IsNullOrWhiteSpace(home))
    {
        return input;
    }

    if (input == "~")
    {
        return home;
    }

    var trimmed = input.TrimStart('~').TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    return Path.Combine(home, trimmed);
}

interface IGarminTarget : IDisposable
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

    public void Dispose()
    {
        // No-op, connection is managed per transfer.
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

    public void Dispose()
    {
        // No-op.
    }
}

sealed class LibMtpBackend : IGarminTarget
{
    private bool _disposed;

    private LibMtpBackend()
    {
    }

    public static LibMtpBackend? TryCreate()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return null;
        }

        if (!NativeMethods.TryLoadLibMtp())
        {
            return null;
        }

        NativeMethods.LIBMTP_Init();
        NativeMethods.LIBMTP_Set_Debug(Diag.LibMtpDebugLevel);
        return new LibMtpBackend();
    }

    public bool HasAnyDevice()
    {
        using var session = OpenDeviceSession();
        Diag.Info($"libmtp device pointer: 0x{session.Device.ToInt64():X}");
        return session.Device != IntPtr.Zero;
    }

    public void UploadPrg(string sourceFile, string nameWithoutExtension)
    {
        UploadToPath(sourceFile, $"GARMIN/Apps/{nameWithoutExtension}.prg");
    }

    public void UploadSet(string sourceFile, string nameWithoutExtension)
    {
        UploadToPath(sourceFile, $"GARMIN/Apps/Settings/{nameWithoutExtension}.SET");
    }

    private void UploadToPath(string sourceFile, string mtpPath)
    {
        EnsureNotDisposed();
        Diag.Info($"MTP upload start. Source='{sourceFile}', destination='{mtpPath}'");

        using var session = OpenDeviceSession();
        if (session.Device == IntPtr.Zero)
        {
            throw new InvalidOperationException("No MTP device available.");
        }

        var fileName = Path.GetFileName(mtpPath);
        var folderParts = mtpPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (folderParts.Length == 0)
        {
            throw new InvalidOperationException("Invalid destination path.");
        }

        var foldersOnly = folderParts.Take(folderParts.Length - 1).ToArray();
        var location = ResolveOrCreateFolderPath(session.Device, foldersOnly);
        Diag.Info($"Resolved MTP folder parent_id={location.ParentId}, storage_id={location.StorageId}");

        var fileMetaPtr = NativeMethods.LIBMTP_new_file_t();
        if (fileMetaPtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("LIBMTP_new_file_t failed.");
        }

        try
        {
            var fileMeta = Marshal.PtrToStructure<LibMtpFile>(fileMetaPtr);
            fileMeta.parent_id = location.ParentId == uint.MaxValue ? 0 : location.ParentId;
            fileMeta.storage_id = location.StorageId;
            fileMeta.filesize = (ulong)new FileInfo(sourceFile).Length;
            // libmtp takes ownership of this filename pointer and frees it in LIBMTP_destroy_file_t.
            fileMeta.filename = Marshal.StringToHGlobalAnsi(fileName);

            Marshal.StructureToPtr(fileMeta, fileMetaPtr, fDeleteOld: false);

            var rc = NativeMethods.LIBMTP_Send_File_From_File_Quiet(session.Device, sourceFile, fileMetaPtr, IntPtr.Zero, IntPtr.Zero);
            Diag.Info($"LIBMTP_Send_File_From_File returned {rc}");
            if (rc != 0)
            {
                throw new InvalidOperationException($"MTP upload failed with code {rc}.");
            }
        }
        finally
        {
            NativeMethods.LIBMTP_destroy_file_t(fileMetaPtr);
        }
    }

    private static FolderLocation ResolveOrCreateFolderPath(IntPtr device, IReadOnlyList<string> segments)
    {
        var parentId = 0u; // Root for folder matching/traversal.
        var storageId = 0u;

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            var listPtr = NativeMethods.LIBMTP_Get_Folder_List_Quiet(device);
            var found = i == 0
                ? FindRootFolderByName(listPtr, segment)
                : FindFolderByNameAndParent(listPtr, segment, parentId);
            if (listPtr != IntPtr.Zero)
            {
                NativeMethods.LIBMTP_destroy_folder_t(listPtr);
            }

            if (found is { } existing)
            {
                Diag.Debug($"MTP folder exists: '{segment}' id={existing.folder_id} parent={existing.parent_id} storage={existing.storage_id}");
                parentId = existing.folder_id;
                storageId = existing.storage_id;
                continue;
            }

            // libmtp expects parent=0 for root and a valid storage id at root-level.
            var createParent = parentId;
            if (i == 0 && storageId == 0)
            {
                // If the first segment doesn't exist and we don't know storage yet, try to infer from existing roots.
                var roots = NativeMethods.LIBMTP_Get_Folder_List_Quiet(device);
                storageId = FindAnyRootStorageId(roots);
                if (roots != IntPtr.Zero)
                {
                    NativeMethods.LIBMTP_destroy_folder_t(roots);
                }
            }

            Diag.Debug($"Creating MTP folder: '{segment}' under parent={createParent} storage={storageId}");
            var createdId = NativeMethods.LIBMTP_Create_Folder_Quiet(device, segment, createParent, storageId);
            Diag.Debug($"LIBMTP_Create_Folder returned id={createdId} for '{segment}'");
            if (createdId == 0 || createdId == uint.MaxValue)
            {
                throw new InvalidOperationException($"Failed to create MTP folder '{segment}'.");
            }

            parentId = createdId;
            if (storageId == 0)
            {
                var refreshed = NativeMethods.LIBMTP_Get_Folder_List_Quiet(device);
                var created = FindFolderById(refreshed, createdId);
                if (refreshed != IntPtr.Zero)
                {
                    NativeMethods.LIBMTP_destroy_folder_t(refreshed);
                }

                if (created is { } c)
                {
                    storageId = c.storage_id;
                }
            }
        }

        return new FolderLocation(parentId, storageId);
    }

    private static LibMtpFolder? FindFolderByNameAndParent(IntPtr folderPtr, string name, uint parentId)
    {
        foreach (var folder in EnumerateFolders(folderPtr))
        {
            if (folder.parent_id != parentId)
            {
                continue;
            }

            var currentName = Marshal.PtrToStringAnsi(folder.name);
            if (string.Equals(currentName, name, StringComparison.OrdinalIgnoreCase))
            {
                return folder;
            }
        }

        return null;
    }

    private static LibMtpFolder? FindRootFolderByName(IntPtr folderPtr, string name)
    {
        foreach (var folder in EnumerateFolders(folderPtr))
        {
            if (!IsRootParent(folder.parent_id))
            {
                continue;
            }

            var currentName = Marshal.PtrToStringAnsi(folder.name);
            if (string.Equals(currentName, name, StringComparison.OrdinalIgnoreCase))
            {
                return folder;
            }
        }

        return null;
    }

    private static uint FindAnyRootStorageId(IntPtr folderPtr)
    {
        foreach (var folder in EnumerateFolders(folderPtr))
        {
            if (IsRootParent(folder.parent_id) && folder.storage_id != 0)
            {
                return folder.storage_id;
            }
        }

        return 0;
    }

    private static bool IsRootParent(uint parentId) => parentId == 0 || parentId == uint.MaxValue;

    private static LibMtpFolder? FindFolderById(IntPtr folderPtr, uint id)
    {
        foreach (var folder in EnumerateFolders(folderPtr))
        {
            if (folder.folder_id == id)
            {
                return folder;
            }
        }

        return null;
    }

    private static IEnumerable<LibMtpFolder> EnumerateFolders(IntPtr folderPtr)
    {
        if (folderPtr == IntPtr.Zero)
        {
            yield break;
        }

        var stack = new Stack<IntPtr>();
        stack.Push(folderPtr);

        while (stack.Count > 0)
        {
            var ptr = stack.Pop();
            if (ptr == IntPtr.Zero)
            {
                continue;
            }

            var folder = Marshal.PtrToStructure<LibMtpFolder>(ptr);
            yield return folder;

            if (folder.sibling != IntPtr.Zero)
            {
                stack.Push(folder.sibling);
            }
            if (folder.child != IntPtr.Zero)
            {
                stack.Push(folder.child);
            }
        }
    }

    private DeviceSession OpenDeviceSession()
    {
        IntPtr deviceList = IntPtr.Zero;
        var rc = NativeMethods.LIBMTP_Get_Connected_Devices_Quiet(ref deviceList);
        Diag.Debug($"LIBMTP_Get_Connected_Devices rc={rc}, ptr=0x{deviceList.ToInt64():X}");
        if (rc != 0 || deviceList == IntPtr.Zero)
        {
            return new DeviceSession(IntPtr.Zero, IntPtr.Zero);
        }

        return new DeviceSession(deviceList, deviceList);
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private readonly record struct FolderLocation(uint ParentId, uint StorageId);

    private sealed class DeviceSession(IntPtr listHead, IntPtr device) : IDisposable
    {
        public IntPtr Device { get; } = device;

        public void Dispose()
        {
            if (listHead != IntPtr.Zero)
            {
                NativeMethods.LIBMTP_Release_Device_List(listHead);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LibMtpFolder
    {
        public uint folder_id;
        public uint parent_id;
        public uint storage_id;
        public IntPtr name;
        public IntPtr sibling;
        public IntPtr child;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LibMtpFile
    {
        public uint item_id;
        public uint parent_id;
        public uint storage_id;
        public IntPtr filename;
        public ulong filesize;
        public long modificationdate;
        public int filetype;
        public IntPtr next;
    }

    private static class NativeMethods
    {
        private const string LibMtp = "libmtp";
        private static IntPtr _libHandle;
        private static bool _resolverConfigured;

        public static bool TryLoadLibMtp()
        {
            if (_libHandle != IntPtr.Zero)
            {
                return true;
            }

            if (!_resolverConfigured)
            {
                NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, ResolveLibMtp);
                _resolverConfigured = true;
            }

            string[] candidates;
            if (OperatingSystem.IsMacOS())
            {
                candidates =
                [
                    Path.Combine(AppContext.BaseDirectory, "libmtp.dylib"),
                    Path.Combine(AppContext.BaseDirectory, "lib", "libmtp.dylib"),
                    "libmtp.dylib",
                    "/opt/homebrew/lib/libmtp.dylib",
                    "/usr/local/lib/libmtp.dylib"
                ];
            }
            else
            {
                candidates = ["libmtp.so", "libmtp.so.9", "libmtp"];
            }

            foreach (var candidate in candidates)
            {
                Diag.Debug($"Trying to load libmtp from '{candidate}'");
                if (NativeLibrary.TryLoad(candidate, out _libHandle))
                {
                    Diag.Info($"Loaded libmtp from '{candidate}'");
                    return true;
                }
            }

            Diag.Info("Failed to load libmtp shared library.");
            return false;
        }

        private static IntPtr ResolveLibMtp(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (!string.Equals(libraryName, LibMtp, StringComparison.Ordinal))
            {
                return IntPtr.Zero;
            }

            if (_libHandle != IntPtr.Zero)
            {
                return _libHandle;
            }

            return IntPtr.Zero;
        }

        [DllImport(LibMtp, CallingConvention = CallingConvention.Cdecl)]
        public static extern void LIBMTP_Init();

        [DllImport(LibMtp, CallingConvention = CallingConvention.Cdecl)]
        public static extern void LIBMTP_Set_Debug(int debugFlags);

        [DllImport(LibMtp, CallingConvention = CallingConvention.Cdecl)]
        public static extern int LIBMTP_Get_Connected_Devices(ref IntPtr deviceList);

        public static int LIBMTP_Get_Connected_Devices_Quiet(ref IntPtr deviceList)
        {
            if (!ShouldSuppressNativeOutput())
            {
                return LIBMTP_Get_Connected_Devices(ref deviceList);
            }

            IntPtr local = IntPtr.Zero;
            var rc = InvokeWithNativeOutputMuted(() => LIBMTP_Get_Connected_Devices(ref local));
            deviceList = local;
            return rc;
        }

        [DllImport(LibMtp, CallingConvention = CallingConvention.Cdecl)]
        public static extern void LIBMTP_Release_Device_List(IntPtr deviceList);

        [DllImport(LibMtp, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr LIBMTP_Get_Folder_List(IntPtr device);

        public static IntPtr LIBMTP_Get_Folder_List_Quiet(IntPtr device)
        {
            if (!ShouldSuppressNativeOutput())
            {
                return LIBMTP_Get_Folder_List(device);
            }

            return InvokeWithNativeOutputMuted(() => LIBMTP_Get_Folder_List(device));
        }

        [DllImport(LibMtp, CallingConvention = CallingConvention.Cdecl)]
        public static extern void LIBMTP_destroy_folder_t(IntPtr folders);

        [DllImport(LibMtp, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint LIBMTP_Create_Folder(IntPtr device, string name, uint parentId, uint storageId);

        public static uint LIBMTP_Create_Folder_Quiet(IntPtr device, string name, uint parentId, uint storageId)
        {
            if (!ShouldSuppressNativeOutput())
            {
                return LIBMTP_Create_Folder(device, name, parentId, storageId);
            }

            return InvokeWithNativeOutputMuted(() => LIBMTP_Create_Folder(device, name, parentId, storageId));
        }

        [DllImport(LibMtp, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr LIBMTP_new_file_t();

        [DllImport(LibMtp, CallingConvention = CallingConvention.Cdecl)]
        public static extern void LIBMTP_destroy_file_t(IntPtr file);

        [DllImport(LibMtp, CallingConvention = CallingConvention.Cdecl)]
        public static extern int LIBMTP_Send_File_From_File(IntPtr device, string path, IntPtr fileMeta, IntPtr callback, IntPtr data);

        public static int LIBMTP_Send_File_From_File_Quiet(IntPtr device, string path, IntPtr fileMeta, IntPtr callback, IntPtr data)
        {
            if (!ShouldSuppressNativeOutput())
            {
                return LIBMTP_Send_File_From_File(device, path, fileMeta, callback, data);
            }

            return InvokeWithNativeOutputMuted(() => LIBMTP_Send_File_From_File(device, path, fileMeta, callback, data));
        }

        private static bool ShouldSuppressNativeOutput()
        {
            return Diag.SuppressLibMtpNativeStderr && (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux());
        }

        private static T InvokeWithNativeOutputMuted<T>(Func<T> action)
        {
            var savedStdout = dup(1);
            var savedStderr = dup(2);
            if (savedStdout < 0 || savedStderr < 0)
            {
                if (savedStdout >= 0) _ = close(savedStdout);
                if (savedStderr >= 0) _ = close(savedStderr);
                return action();
            }

            var devNull = open("/dev/null", O_WRONLY);
            if (devNull >= 0)
            {
                _ = dup2(devNull, 1);
                _ = dup2(devNull, 2);
                _ = close(devNull);
            }

            try
            {
                return action();
            }
            finally
            {
                // Flush all C stdio buffers while fds still point to /dev/null,
                // so buffered libmtp output doesn't leak out after fd restore.
                _ = fflush(IntPtr.Zero);
                _ = dup2(savedStdout, 1);
                _ = dup2(savedStderr, 2);
                _ = close(savedStdout);
                _ = close(savedStderr);
            }
        }

        private const int O_WRONLY = 1;

        [DllImport("libc", CallingConvention = CallingConvention.Cdecl)]
        private static extern int fflush(IntPtr stream); // IntPtr.Zero flushes all C stdio streams

        [DllImport("libc", CallingConvention = CallingConvention.Cdecl)]
        private static extern int dup(int oldfd);

        [DllImport("libc", CallingConvention = CallingConvention.Cdecl)]
        private static extern int dup2(int oldfd, int newfd);

        [DllImport("libc", CallingConvention = CallingConvention.Cdecl)]
        private static extern int close(int fd);

        [DllImport("libc", CallingConvention = CallingConvention.Cdecl)]
        private static extern int open(string path, int oflag);
    }
}

static class Diag
{
    private static bool _verbose;
    private static int _libMtpDebugLevel;
    private static bool _suppressLibMtpNativeStderr;

    public static void Configure(string[] args)
    {
        _verbose = args.Any(a => string.Equals(a, "--verbose", StringComparison.OrdinalIgnoreCase)) ||
                   string.Equals(Environment.GetEnvironmentVariable("INSTALLGWF_VERBOSE"), "1", StringComparison.Ordinal);
        _libMtpDebugLevel = int.TryParse(Environment.GetEnvironmentVariable("INSTALLGWF_LIBMTP_DEBUG"), out var parsed)
            ? parsed
            : 0;
        _suppressLibMtpNativeStderr = !string.Equals(Environment.GetEnvironmentVariable("INSTALLGWF_LIBMTP_STDERR"), "1", StringComparison.Ordinal);
        if (_libMtpDebugLevel != 0)
        {
            Info($"Using libmtp debug level: {_libMtpDebugLevel}");
        }
        if (!_suppressLibMtpNativeStderr)
        {
            Info("Native libmtp stderr passthrough enabled (INSTALLGWF_LIBMTP_STDERR=1).");
        }
    }

    public static int LibMtpDebugLevel => _libMtpDebugLevel;
    public static bool SuppressLibMtpNativeStderr => _suppressLibMtpNativeStderr;

    public static void Info(string message)
    {
        Console.WriteLine($"[info] {message}");
    }

    public static void Error(string message)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"[error] {message}");
        Console.ForegroundColor = prev;
    }

    public static void Debug(string message)
    {
        if (_verbose)
        {
            Console.WriteLine($"[debug] {message}");
        }
    }
}
