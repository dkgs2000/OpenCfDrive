using System.Runtime.InteropServices;
using System.Text;
using OpenCfDrive.Core.Interop;

namespace OpenCfDrive.Core.CloudFiles;

public sealed record PlaceholderCreationResult(int Created, int Failed, IReadOnlyList<string> Errors);

/// <summary>
/// One entry passed to CfCreatePlaceholders. <see cref="RelativeName"/> must be a
/// single name relative to the batch directory: the API rejects names that
/// contain path separators.
/// </summary>
public sealed record PlaceholderEntry(
    string RelativeName,
    bool IsDirectory,
    long Size,
    DateTimeOffset? ModifiedUtc,
    string FileIdentity);

/// <summary>
/// Entries that all live in the same directory, so they can be created in one
/// CfCreatePlaceholders call. <see cref="ParentRelativePath"/> is empty for the
/// sync root itself.
/// </summary>
public sealed record PlaceholderBatch(string ParentRelativePath, IReadOnlyList<PlaceholderEntry> Entries);

public sealed record PlaceholderState(
    bool Exists,
    bool IsPlaceholder,
    bool InSync,
    bool Partial,
    long FileId,
    uint RawAttributes,
    uint ReparseTag,
    uint RawState = 0);

/// <summary>
/// Implements Pillar 2: turn cloud metadata into real cfapi placeholders
/// (0-byte logical files that Explorer renders with a cloud icon) and read
/// back the Windows file id for the SQLite mapping.
/// </summary>
public sealed class CloudPlaceholderService
{
    private const uint FILE_ATTRIBUTE_ARCHIVE = 0x00000020;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    private const uint IO_REPARSE_TAG_CLOUD_MASK = 0xFFFF0000;
    private const uint IO_REPARSE_TAG_CLOUD = 0x90000000;
    private const uint IO_REPARSE_TAG_CLOUD_1 = 0x90001000;
    private const int HRESULT_ALREADY_EXISTS = unchecked((int)0x800700B7);

    /// <summary>Identity blob attached to every placeholder; this is what cfapi hands back in callbacks.</summary>
    public static byte[] BuildFileIdentity(string remotePath) => Encoding.UTF8.GetBytes(remotePath);

    /// <summary>
    /// Creates the placeholders for a flattened remote listing. Directories are
    /// created first (shallow to deep) as directory placeholders, then files
    /// are created inside their parent.
    /// CfCreatePlaceholders takes names relative to one directory, so entries
    /// are grouped by parent instead of passing full relative paths.
    /// </summary>
    public PlaceholderCreationResult CreatePlaceholders(
        string syncRootPath,
        IReadOnlyList<RcloneItemMetadata> items)
    {
        if (!CloudFilesCapability.Current.IsSupported)
        {
            return new PlaceholderCreationResult(0, items.Count, [CloudFilesCapability.Current.Reason]);
        }

        var created = 0;
        var failed = 0;
        var errors = new List<string>();

        foreach (var batch in PlanBatches(items))
        {
            var directoryPath = string.IsNullOrEmpty(batch.ParentRelativePath)
                ? syncRootPath
                : Path.Combine(syncRootPath, batch.ParentRelativePath);

            // The parent is a directory placeholder created by an earlier batch.
            // If it is missing (or was never a placeholder because the item was
            // a plain folder) fall back to a real directory so its children still
            // show up in the namespace.
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            // Bound the native array size; large directories are common.
            const int batchSize = 64;
            for (var start = 0; start < batch.Entries.Count; start += batchSize)
            {
                var slice = batch.Entries.Skip(start).Take(batchSize).ToList();
                var (batchCreated, batchFailed, batchErrors) = CreateBatch(directoryPath, slice);
                created += batchCreated;
                failed += batchFailed;
                errors.AddRange(batchErrors);
            }
        }

        return new PlaceholderCreationResult(created, failed, errors);
    }

    /// <summary>
    /// Groups a remote listing into the CfCreatePlaceholders calls that create
    /// it. Directory batches come first and are ordered by depth so a parent
    /// always exists before its children are created.
    /// </summary>
    public static IReadOnlyList<PlaceholderBatch> PlanBatches(IReadOnlyList<RcloneItemMetadata> items)
    {
        var batches = new List<PlaceholderBatch>();

        var directories = items
            .Where(i => i.IsDirectory)
            .Select(i => (Relative: NormalizeRelative(i.RelativePath), Item: i))
            .OrderBy(x => Depth(x.Relative))
            .ThenBy(x => x.Relative, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in directories.GroupBy(x => ParentOf(x.Relative), StringComparer.OrdinalIgnoreCase))
        {
            batches.Add(new PlaceholderBatch(
                group.Key,
                group.Select(x => ToEntry(x.Item, x.Relative)).ToList()));
        }

        foreach (var group in items
                     .Where(i => !i.IsDirectory)
                     .Select(i => (Relative: NormalizeRelative(i.RelativePath), Item: i))
                     .GroupBy(x => ParentOf(x.Relative), StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            batches.Add(new PlaceholderBatch(
                group.Key,
                group.Select(x => ToEntry(x.Item, x.Relative)).ToList()));
        }

        return batches;
    }

    private static PlaceholderEntry ToEntry(RcloneItemMetadata item, string relative) =>
        new(
            RelativeName: Path.GetFileName(relative),
            IsDirectory: item.IsDirectory,
            Size: item.IsDirectory ? 0 : item.Size,
            ModifiedUtc: item.ModifiedUtc,
            FileIdentity: item.RemotePath);

    private static string NormalizeRelative(string path) =>
        path.Replace('/', Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar);

    private static string ParentOf(string relative) =>
        Path.GetDirectoryName(relative) ?? string.Empty;

    private static int Depth(string relative) =>
        string.IsNullOrEmpty(relative) ? 0 : relative.Count(c => c == Path.DirectorySeparatorChar);

    /// <summary>
    /// The native create record for one entry, shared by CfCreatePlaceholders
    /// and the FETCH_PLACEHOLDERS transfer. The identity buffer is owned by
    /// the caller and must outlive the native call.
    /// </summary>
    internal static CF_PLACEHOLDER_CREATE_INFO BuildCreateInfo(
        PlaceholderEntry entry,
        IntPtr identityPtr,
        uint identityLength) =>
        new()
        {
            RelativeFileName = entry.RelativeName,
            FsMetadata = new CF_FS_METADATA
            {
                BasicInfo = new CF_FILE_BASIC_INFO
                {
                    CreationTime = ToFileTime(entry.ModifiedUtc),
                    LastAccessTime = ToFileTime(entry.ModifiedUtc),
                    LastWriteTime = ToFileTime(entry.ModifiedUtc),
                    ChangeTime = ToFileTime(entry.ModifiedUtc),
                    FileAttributes = entry.IsDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_ARCHIVE,
                },
                FileSize = entry.Size,
            },
            FileIdentity = identityPtr,
            FileIdentityLength = identityLength,
            // MARK_IN_SYNC reflects reality: the placeholder metadata already
            // matches the remote object. Files stay dehydrated (0 bytes on
            // disk, cloud icon); their data is pulled on demand by FETCH_DATA.
            // Directories keep on-demand population enabled so every listing
            // is answered by FETCH_PLACEHOLDERS from the live remote.
            Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC,
        };

    /// <summary>
    /// Applies a server-side change to a file placeholder: new size and
    /// timestamps, local copy dropped so the next read fetches the new
    /// content. VERIFY_IN_SYNC makes the platform refuse the update if the
    /// user modified the file since it was last in sync, so a local edit is
    /// never thrown away. Returns the HRESULT.
    /// </summary>
    /// <param name="discardLocalChanges">
    /// Server wins: replace the file even if it was edited locally since it
    /// was last in sync. Without it the platform refuses such a file
    /// (VERIFY_IN_SYNC, ERROR_CLOUD_FILE_NOT_IN_SYNC).
    /// </param>
    public int UpdateFromRemote(string localPath, long size, DateTimeOffset? modifiedUtc, bool discardLocalChanges = false)
    {
        if (!CloudFilesCapability.Current.IsSupported)
        {
            return -1;
        }

        // A file that is not fully on disk has no local data to protect, and
        // a write-access open would download all of it just to drop it.
        return QueryState(localPath).Partial
            ? WithMetadataHandle(localPath, handle => UpdateFromRemote(handle, size, modifiedUtc, discardLocalChanges))
            : WithWin32Handle(localPath, handle => UpdateFromRemote(handle, size, modifiedUtc, discardLocalChanges));
    }

    private static int UpdateFromRemote(IntPtr handle, long size, DateTimeOffset? modifiedUtc, bool discardLocalChanges)
    {
        var metadataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<CF_FS_METADATA>());
        try
        {
            var time = modifiedUtc is null ? 0 : ToFileTime(modifiedUtc);
            Marshal.StructureToPtr(
                new CF_FS_METADATA
                {
                    // Zero fields mean "unchanged" (FILE_BASIC_INFO semantics).
                    BasicInfo = new CF_FILE_BASIC_INFO
                    {
                        LastWriteTime = time,
                        ChangeTime = time,
                    },
                    FileSize = size,
                },
                metadataPtr,
                fDeleteOld: false);

            return CfApi.CfUpdatePlaceholder(
                handle,
                metadataPtr,
                IntPtr.Zero,
                0,
                IntPtr.Zero,
                0,
                (discardLocalChanges ? CF_UPDATE_FLAGS.CF_UPDATE_FLAG_NONE : CF_UPDATE_FLAGS.CF_UPDATE_FLAG_VERIFY_IN_SYNC) |
                CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC |
                CF_UPDATE_FLAGS.CF_UPDATE_FLAG_DEHYDRATE,
                out _,
                IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(metadataPtr);
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> with a Win32 handle to the placeholder.
    /// CfOpenFileWithOplock returns a *protected* handle, which
    /// CfUpdatePlaceholder rejects with E_HANDLE (pinned by
    /// UpdatePlaceholderContractTest). The Win32 handle behind it is only
    /// valid while the protected handle is referenced: an oplock break (another
    /// program opening the file) invalidates it, and then the reference fails
    /// instead of the operation touching a file someone else is using.
    /// </summary>
    private static int WithWin32Handle(string localPath, Func<IntPtr, int> action)
    {
        var hr = CfApi.CfOpenFileWithOplock(
            localPath,
            CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_WRITE_ACCESS,
            out var protectedHandle);

        if (!CfApi.Succeeded(hr) || protectedHandle == IntPtr.Zero)
        {
            return CfApi.Succeeded(hr) ? E_HANDLE : hr;
        }

        try
        {
            if (!CfApi.CfReferenceProtectedHandle(protectedHandle))
            {
                return E_HANDLE;
            }

            try
            {
                return action(CfApi.CfGetWin32HandleFromProtectedHandle(protectedHandle));
            }
            finally
            {
                CfApi.CfReleaseProtectedHandle(protectedHandle);
            }
        }
        finally
        {
            CfApi.CfCloseHandle(protectedHandle);
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> with a handle that has no data access
    /// (WRITE_DAC only), for updates that change only a placeholder's
    /// metadata. A write-access open of a placeholder that is not fully on
    /// disk makes the platform download it first (pinned by
    /// UpdatePlaceholderContractTest).
    /// </summary>
    private static int WithMetadataHandle(string localPath, Func<IntPtr, int> action)
    {
        var handle = CreateFileW(
            localPath,
            WRITE_DAC,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        if (handle == INVALID_HANDLE_VALUE)
        {
            return Marshal.GetHRForLastWin32Error();
        }

        try
        {
            return action(handle);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const int E_HANDLE = unchecked((int)0x80070006);

    /// <summary>
    /// Re-enables on-demand population on an existing directory placeholder.
    /// Directories created by earlier versions had it disabled, and
    /// CfCreatePlaceholders leaves an existing directory's flags untouched.
    /// </summary>
    public bool EnableOnDemandPopulation(string directoryPath)
    {
        if (!CloudFilesCapability.Current.IsSupported || !Directory.Exists(directoryPath))
        {
            return false;
        }

        return CfApi.Succeeded(WithWin32Handle(directoryPath, handle => CfApi.CfUpdatePlaceholder(
            handle,
            IntPtr.Zero,
            IntPtr.Zero,
            0,
            IntPtr.Zero,
            0,
            CF_UPDATE_FLAGS.CF_UPDATE_FLAG_ENABLE_ON_DEMAND_POPULATION,
            out _,
            IntPtr.Zero)));
    }

    private (int Created, int Failed, List<string> Errors) CreateBatch(
        string directoryPath,
        IReadOnlyList<PlaceholderEntry> batch)
    {
        var infos = new CF_PLACEHOLDER_CREATE_INFO[batch.Count];
        var allocations = new List<IntPtr>(batch.Count);
        var created = 0;
        var failed = 0;
        var errors = new List<string>();

        try
        {
            for (var i = 0; i < batch.Count; i++)
            {
                var entry = batch[i];
                var identity = BuildFileIdentity(entry.FileIdentity);
                var identityPtr = Marshal.AllocHGlobal(identity.Length == 0 ? 1 : identity.Length);
                Marshal.Copy(identity, 0, identityPtr, identity.Length);
                allocations.Add(identityPtr);

                infos[i] = BuildCreateInfo(entry, identityPtr, (uint)identity.Length);
            }

            var hr = CfApi.CfCreatePlaceholders(
                directoryPath,
                infos,
                (uint)infos.Length,
                CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE,
                out var processed);

            // ERROR_ALREADY_EXISTS from the batch call is not fatal: the API
            // reports that at least one entry already existed, but it still
            // processes the batch and records a per-entry result. Only a call
            // that reports a different failure is a real batch failure.
            if (!CfApi.Succeeded(hr) && hr != HRESULT_ALREADY_EXISTS)
            {
                errors.Add($"CfCreatePlaceholders failed in '{directoryPath}' with {HResult.Describe(hr)} ({processed}/{infos.Length} processed).");
                return (0, batch.Count, errors);
            }

            for (var i = 0; i < infos.Length; i++)
            {
                var itemResult = infos[i].Result;
                var localPath = Path.Combine(directoryPath, infos[i].RelativeFileName);
                var isDirectory = batch[i].IsDirectory;
                var present = isDirectory ? Directory.Exists(localPath) : File.Exists(localPath);

                if (CfApi.Succeeded(itemResult) || itemResult == HRESULT_ALREADY_EXISTS)
                {
                    if (present)
                    {
                        // Already present means a previous run created the
                        // placeholder; the namespace is already in the desired
                        // state, so treat it as created.
                        created++;
                    }
                    else
                    {
                        failed++;
                        errors.Add($"{infos[i].RelativeFileName}: placeholder missing after creation ({HResult.Describe(itemResult)}).");
                    }
                }
                else
                {
                    failed++;
                    errors.Add($"{infos[i].RelativeFileName}: {HResult.Describe(itemResult)}");
                }
            }

            return (created, failed, errors);
        }
        finally
        {
            foreach (var ptr in allocations)
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
    }

    /// <summary>Reads the placeholder state and real Windows file id for a created item.</summary>
    public PlaceholderState QueryState(string localPath)
    {
        // Directories are placeholders too, and File.Exists is false for them.
        if (!CloudFilesCapability.Current.IsSupported ||
            (!File.Exists(localPath) && !Directory.Exists(localPath)))
        {
            return new PlaceholderState(false, false, false, false, 0, 0, 0);
        }

        GetAttributes(localPath, out var attributes, out var reparseTag);
        var state = CfApi.CfGetPlaceholderStateFromAttributeTag(attributes, reparseTag);

        // Deliberately metadata-only: opening the placeholder for data access
        // (as CfOpenFileWithOplock does) triggers implicit hydration, which
        // would defeat the whole point of a dehydrated placeholder and stall
        // the caller for the full hydration timeout.
        var fileId = GetNtfsFileId(localPath);

        return new PlaceholderState(
            Exists: true,
            IsPlaceholder: (state & CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_PLACEHOLDER) != 0,
            InSync: (state & CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_IN_SYNC) != 0,
            Partial: (state & CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_PARTIAL) != 0,
            FileId: fileId,
            RawAttributes: attributes,
            ReparseTag: reparseTag,
            RawState: (uint)state);
    }

    /// <summary>Marks a hydrated/uploaded placeholder as in sync (green check overlay).</summary>
    /// <param name="fileSize">
    /// The file's size as uploaded. The platform keeps its own record of a
    /// placeholder's size, which writes do not change; without the new one,
    /// a file an edit made bigger is downloaded to the old size after Free
    /// up space (the rest reads as zeros). Nextcloud's updatePlaceholderInfo
    /// passes it on every upload too.
    /// </param>
    public bool MarkInSync(string localPath, byte[] fileIdentity, long? fileSize = null)
    {
        if (!CloudFilesCapability.Current.IsSupported)
        {
            return false;
        }

        return UpdateWithIdentity(localPath, fileIdentity, CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC, fileSize);
    }

    private static bool UpdateWithIdentity(string localPath, byte[] fileIdentity, CF_UPDATE_FLAGS flags, long? fileSize = null)
    {
        var identityPtr = Marshal.AllocHGlobal(fileIdentity.Length == 0 ? 1 : fileIdentity.Length);
        var metadataPtr = fileSize is null ? IntPtr.Zero : Marshal.AllocHGlobal(Marshal.SizeOf<CF_FS_METADATA>());
        try
        {
            Marshal.Copy(fileIdentity, 0, identityPtr, fileIdentity.Length);
            if (fileSize is { } size)
            {
                // Zero times and attributes mean "unchanged".
                Marshal.StructureToPtr(new CF_FS_METADATA { FileSize = size }, metadataPtr, fDeleteOld: false);
            }

            return CfApi.Succeeded(WithMetadataHandle(localPath, handle => CfApi.CfUpdatePlaceholder(
                handle,
                metadataPtr,
                identityPtr,
                (uint)fileIdentity.Length,
                IntPtr.Zero,
                0,
                flags,
                out _,
                IntPtr.Zero)));
        }
        finally
        {
            Marshal.FreeHGlobal(identityPtr);
            if (metadataPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(metadataPtr);
            }
        }
    }

    /// <summary>
    /// Replaces the identity blob attached to a placeholder, used after a
    /// rename so future callbacks resolve to the new remote path.
    /// </summary>
    public bool UpdateFileIdentity(string localPath, byte[] fileIdentity)
    {
        if (!CloudFilesCapability.Current.IsSupported)
        {
            return false;
        }

        return UpdateWithIdentity(localPath, fileIdentity, CF_UPDATE_FLAGS.CF_UPDATE_FLAG_NONE);
    }

    /// <summary>
    /// Evicts the local copy of a hydrated placeholder so the next read has to
    /// go through FETCH_DATA again. Length -1 (CF_EOF) means the whole file.
    /// Only a file in sync is freed.
    /// </summary>
    /// <param name="fileSize">
    /// Given again with it, as Nextcloud does: the next read downloads as much
    /// as the platform's own record of the size says, and a placeholder marked
    /// in sync without its new size after an edit still has the old one.
    /// </param>
    public bool Dehydrate(string localPath, long? fileSize = null)
    {
        if (!CloudFilesCapability.Current.IsSupported)
        {
            return false;
        }

        if (fileSize is { } size)
        {
            return CfApi.Succeeded(UpdateFromRemote(localPath, size, modifiedUtc: null));
        }

        return CfApi.Succeeded(WithWin32Handle(localPath, handle => CfApi.CfDehydratePlaceholder(
            handle,
            0,
            -1,
            CF_DEHYDRATE_FLAGS.CF_DEHYDRATE_FLAG_NONE,
            IntPtr.Zero)));
    }

    /// <summary>
    /// Downloads the whole file into the placeholder (the platform raises
    /// FETCH_DATA, which this process serves). Used for "Always keep on this
    /// device". Returns the HRESULT.
    /// Through a handle without data access, which the platform accepts: a
    /// write handle refuses every program that opens the file for reading
    /// only (sharing read), and it is still open for a moment after the file
    /// shows as downloaded.
    /// </summary>
    public int Hydrate(string localPath)
    {
        if (!CloudFilesCapability.Current.IsSupported)
        {
            return -1;
        }

        return WithMetadataHandle(localPath, handle => CfApi.CfHydratePlaceholder(
            handle,
            0,
            -1,
            CF_HYDRATE_FLAGS.CF_HYDRATE_FLAG_NONE,
            IntPtr.Zero));
    }

    /// <summary>
    /// Bytes actually allocated on disk. A dehydrated placeholder reports its
    /// full logical size through <see cref="FileInfo.Length"/> but zero here.
    /// </summary>
    public static long GetOnDiskSize(string localPath)
    {
        var low = GetCompressedFileSizeW(localPath, out var high);
        if (low == 0xFFFFFFFF && Marshal.GetLastWin32Error() != 0)
        {
            return -1;
        }

        return ((long)high << 32) + low;
    }

    /// <summary>
    /// Converts a local file or folder into a managed placeholder so it can be
    /// tracked. A folder converted with <paramref name="enableOnDemandPopulation"/>
    /// asks the provider for its contents when it is opened.
    /// </summary>
    public bool ConvertToPlaceholder(string localPath, byte[] fileIdentity, bool markInSync, bool enableOnDemandPopulation = false)
    {
        if (!CloudFilesCapability.Current.IsSupported)
        {
            return false;
        }

        var identityPtr = Marshal.AllocHGlobal(fileIdentity.Length == 0 ? 1 : fileIdentity.Length);
        try
        {
            Marshal.Copy(fileIdentity, 0, identityPtr, fileIdentity.Length);
            var flags = markInSync
                ? CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC
                : CF_CONVERT_FLAGS.CF_CONVERT_FLAG_NONE;
            if (enableOnDemandPopulation)
            {
                flags |= CF_CONVERT_FLAGS.CF_CONVERT_FLAG_ENABLE_ON_DEMAND_POPULATION;
            }

            return CfApi.Succeeded(WithWin32Handle(localPath, handle => CfApi.CfConvertToPlaceholder(
                handle,
                identityPtr,
                (uint)fileIdentity.Length,
                flags,
                out _,
                IntPtr.Zero)));
        }
        finally
        {
            Marshal.FreeHGlobal(identityPtr);
        }
    }

    public static bool IsCloudPlaceholder(uint attributes, uint reparseTag)
    {
        if ((reparseTag & IO_REPARSE_TAG_CLOUD_MASK) != IO_REPARSE_TAG_CLOUD)
        {
            return false;
        }

        return reparseTag == IO_REPARSE_TAG_CLOUD || reparseTag == IO_REPARSE_TAG_CLOUD_1;
    }

    private static long ToFileTime(DateTimeOffset? value) =>
        (value ?? DateTimeOffset.UnixEpoch).UtcDateTime.ToFileTimeUtc();

    private static void GetAttributes(string path, out uint attributes, out uint reparseTag)
    {
        attributes = 0;
        reparseTag = 0;

        var data = new WIN32_FILE_ATTRIBUTE_DATA();
        if (!GetFileAttributesExW(path, 0, ref data))
        {
            return;
        }

        attributes = data.FileAttributes;

        if ((attributes & FILE_ATTRIBUTE_REPARSE_POINT) == 0)
        {
            return;
        }

        // WIN32_FILE_ATTRIBUTE_DATA carries no reparse tag; query it through a
        // handle opened onto the reparse point itself.
        var handle = CreateFileW(
            path,
            0,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero);

        if (handle == INVALID_HANDLE_VALUE)
        {
            return;
        }

        try
        {
            var tagInfo = new FILE_ATTRIBUTE_TAG_INFO();
            if (GetFileInformationByHandleEx(handle, FileAttributeTagInfo, ref tagInfo, (uint)Marshal.SizeOf<FILE_ATTRIBUTE_TAG_INFO>()))
            {
                attributes = tagInfo.FileAttributes;
                reparseTag = tagInfo.ReparseTag;
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;
    private const uint FILE_READ_ATTRIBUTES = 0x00000080;
    private const uint WRITE_DAC = 0x00040000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const int FileAttributeTagInfo = 9;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    /// <summary>
    /// The NTFS file id cfapi reports in callbacks, read without touching file
    /// data so a dehydrated placeholder stays dehydrated.
    /// </summary>
    private static long GetNtfsFileId(string path)
    {
        var handle = CreateFileW(
            path,
            FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero);

        if (handle == INVALID_HANDLE_VALUE)
        {
            return 0;
        }

        try
        {
            return GetFileInformationByHandle(handle, out var info)
                ? ((long)info.FileIndexHigh << 32) | info.FileIndexLow
                : 0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WIN32_FILE_ATTRIBUTE_DATA
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ATTRIBUTE_TAG_INFO
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        IntPtr fileHandle,
        out BY_HANDLE_FILE_INFORMATION fileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string fileName, out uint fileSizeHigh);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetFileAttributesExW(
        string fileName,
        int infoLevel,
        ref WIN32_FILE_ATTRIBUTE_DATA fileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(
        IntPtr fileHandle,
        int fileInformationClass,
        ref FILE_ATTRIBUTE_TAG_INFO fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>Metadata required to create a placeholder for one remote item.</summary>
public sealed record RcloneItemMetadata(
    string RemotePath,
    string RelativePath,
    long Size,
    DateTimeOffset? ModifiedUtc,
    bool IsDirectory);
