using System.Runtime.InteropServices;

namespace OpenCfDrive.Core.Interop;

// Faithful P/Invoke definitions derived from the Windows SDK headers:
//   um\cfapi.h (Windows SDK 10.0.26100)
//   winbase.h (FILE_BASIC_INFO), winnt.h (USN, NTSTATUS, LARGE_INTEGER)
//
// Field order, widths and natural alignment intentionally mirror the native
// structs. Do not reorder fields: the layouts are asserted by tests in
// OpenCfDrive.Tests/CfInteropLayoutTests.cs.
internal static class CfApi
{
    internal const int S_OK = 0;

    [DllImport("cldapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int CfGetPlatformInfo(out CF_PLATFORM_INFO platformVersion);

    [DllImport("cldapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int CfRegisterSyncRoot(
        string syncRootPath,
        ref CF_SYNC_REGISTRATION registration,
        ref CF_SYNC_POLICIES policies,
        CF_REGISTER_FLAGS registerFlags);

    [DllImport("cldapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int CfUnregisterSyncRoot(string syncRootPath);

    /// <summary>infoClass 0 = CF_SYNC_ROOT_INFO_BASIC (an 8-byte SyncRootFileId).</summary>
    [DllImport("cldapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int CfGetSyncRootInfoByPath(
        string filePath,
        uint infoClass,
        out long infoBuffer,
        uint infoBufferLength,
        out uint returnedLength);

    [DllImport("cldapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int CfConnectSyncRoot(
        string syncRootPath,
        [In] CF_CALLBACK_REGISTRATION[] callbackTable,
        IntPtr callbackContext,
        CF_CONNECT_FLAGS connectFlags,
        out CF_CONNECTION_KEY connectionKey);

    [DllImport("cldapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int CfDisconnectSyncRoot(CF_CONNECTION_KEY connectionKey);

    [DllImport("cldapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int CfCreatePlaceholders(
        string baseDirectoryPath,
        [In, Out] CF_PLACEHOLDER_CREATE_INFO[] placeholderArray,
        uint placeholderCount,
        CF_CREATE_FLAGS createFlags,
        out uint entriesProcessed);

    [DllImport("cldapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int CfExecute(ref CF_OPERATION_INFO opInfo, ref CF_OPERATION_PARAMETERS opParams);

    // Same native entry point; the union is overlaid with its
    // TransferPlaceholders member instead of TransferData.
    [DllImport("cldapi.dll", EntryPoint = "CfExecute", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int CfExecute(ref CF_OPERATION_INFO opInfo, ref CF_OPERATION_PARAMETERS_PLACEHOLDERS opParams);

    // Same native entry point; AckDelete / AckRename member of the union.
    [DllImport("cldapi.dll", EntryPoint = "CfExecute", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int CfExecute(ref CF_OPERATION_INFO opInfo, ref CF_OPERATION_PARAMETERS_ACK opParams);

    [DllImport("cldapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int CfUpdatePlaceholder(
        IntPtr fileHandle,
        IntPtr fsMetadata,
        IntPtr fileIdentity,
        uint fileIdentityLength,
        IntPtr dehydrateRangeArray,
        uint dehydrateRangeCount,
        CF_UPDATE_FLAGS updateFlags,
        out long updateUsn,
        IntPtr overlapped);

    [DllImport("cldapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int CfConvertToPlaceholder(
        IntPtr fileHandle,
        IntPtr fileIdentity,
        uint fileIdentityLength,
        CF_CONVERT_FLAGS convertFlags,
        out long convertUsn,
        IntPtr overlapped);

    [DllImport("cldapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int CfGetPlaceholderInfo(
        IntPtr fileHandle,
        CF_PLACEHOLDER_INFO_CLASS infoClass,
        IntPtr infoBuffer,
        uint infoBufferLength,
        out uint returnedLength);

    [DllImport("cldapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int CfSetInSyncState(
        IntPtr fileHandle,
        CF_IN_SYNC_STATE inSyncState,
        CF_SET_IN_SYNC_FLAGS inSyncFlags,
        out long inSyncUsn);

    [DllImport("cldapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int CfDehydratePlaceholder(
        IntPtr fileHandle,
        long startingOffset,
        long length,
        CF_DEHYDRATE_FLAGS dehydrateFlags,
        IntPtr overlapped);

    [DllImport("cldapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int CfHydratePlaceholder(
        IntPtr fileHandle,
        long startingOffset,
        long length,
        CF_HYDRATE_FLAGS hydrateFlags,
        IntPtr overlapped);

    [DllImport("cldapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.U4)]
    internal static extern CF_PLACEHOLDER_STATE CfGetPlaceholderStateFromAttributeTag(
        uint fileAttributes,
        uint reparseTag);

    [DllImport("cldapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int CfOpenFileWithOplock(
        string filePath,
        CF_OPEN_FILE_FLAGS flags,
        out IntPtr protectedHandle);

    [DllImport("cldapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern void CfCloseHandle(IntPtr fileHandle);

    [DllImport("cldapi.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool CfReferenceProtectedHandle(IntPtr protectedHandle);

    [DllImport("cldapi.dll", ExactSpelling = true)]
    internal static extern void CfReleaseProtectedHandle(IntPtr protectedHandle);

    [DllImport("cldapi.dll", ExactSpelling = true)]
    internal static extern IntPtr CfGetWin32HandleFromProtectedHandle(IntPtr protectedHandle);

    internal static bool Succeeded(int hr) => hr >= 0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CF_PLATFORM_INFO
{
    public uint BuildNumber;
    public uint RevisionNumber;
    public uint IntegrationNumber;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CF_CONNECTION_KEY
{
    public long Internal;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CF_SYNC_REGISTRATION
{
    public uint StructSize;

    [MarshalAs(UnmanagedType.LPWStr)]
    public string ProviderName;

    [MarshalAs(UnmanagedType.LPWStr)]
    public string ProviderVersion;

    public IntPtr SyncRootIdentity;
    public uint SyncRootIdentityLength;
    public IntPtr FileIdentity;
    public uint FileIdentityLength;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CF_HYDRATION_POLICY
{
    public ushort Primary;
    public ushort Modifier;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CF_POPULATION_POLICY
{
    public ushort Primary;
    public ushort Modifier;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CF_SYNC_POLICIES
{
    public uint StructSize;
    public CF_HYDRATION_POLICY Hydration;
    public CF_POPULATION_POLICY Population;
    public CF_INSYNC_POLICY InSync;
    public CF_HARDLINK_POLICY HardLink;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CF_FILE_BASIC_INFO
{
    public long CreationTime;
    public long LastAccessTime;
    public long LastWriteTime;
    public long ChangeTime;
    public uint FileAttributes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CF_FS_METADATA
{
    public CF_FILE_BASIC_INFO BasicInfo;
    public long FileSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CF_PLACEHOLDER_CREATE_INFO
{
    [MarshalAs(UnmanagedType.LPWStr)]
    public string RelativeFileName;

    public CF_FS_METADATA FsMetadata;
    public IntPtr FileIdentity;
    public uint FileIdentityLength;
    public CF_PLACEHOLDER_CREATE_FLAGS Flags;
    public int Result;
    public long CreateUsn;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CF_CALLBACK_INFO
{
    public uint StructSize;
    public CF_CONNECTION_KEY ConnectionKey;
    public IntPtr CallbackContext;
    public IntPtr VolumeGuidName;
    public IntPtr VolumeDosName;
    public uint VolumeSerialNumber;
    public long SyncRootFileId;
    public IntPtr SyncRootIdentity;
    public uint SyncRootIdentityLength;
    public long FileId;
    public long FileSize;
    public IntPtr FileIdentity;
    public uint FileIdentityLength;
    public IntPtr NormalizedPath;
    public long TransferKey;
    public byte PriorityHint;
    public IntPtr CorrelationVector;
    public IntPtr ProcessInfo;

    // Added in later SDKs; trailing, never read by this provider.
    public long RequestKey;
}

// Overlays the native CF_CALLBACK_PARAMETERS union. Only the members this
// provider consumes are modelled; every member starts at the same offset
// (8, immediately after ParamSize and its padding).
[StructLayout(LayoutKind.Explicit)]
internal struct CF_CALLBACK_PARAMETERS
{
    [FieldOffset(0)]
    public uint ParamSize;

    [FieldOffset(8)]
    public CF_CALLBACK_FETCH_DATA_PARAMETERS FetchData;

    [FieldOffset(8)]
    public CF_CALLBACK_RENAME_FLAGS RenameFlags;

    // struct { CF_CALLBACK_RENAME_FLAGS Flags; PCWSTR TargetPath; } Rename;
    [FieldOffset(16)]
    public IntPtr RenameTargetPath;

    [FieldOffset(8)]
    public CF_CALLBACK_DELETE_FLAGS DeleteFlags;

    // struct { CF_CALLBACK_FETCH_PLACEHOLDERS_FLAGS Flags; PCWSTR Pattern; } FetchPlaceholders;
    [FieldOffset(8)]
    public uint FetchPlaceholdersFlags;

    [FieldOffset(16)]
    public IntPtr FetchPlaceholdersPattern;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CF_CALLBACK_FETCH_DATA_PARAMETERS
{
    public uint Flags;
    public long RequiredFileOffset;
    public long RequiredLength;
    public long OptionalFileOffset;
    public long OptionalLength;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CF_OPERATION_INFO
{
    public uint StructSize;
    public CF_OPERATION_TYPE Type;
    public CF_CONNECTION_KEY ConnectionKey;
    public long TransferKey;
    public IntPtr CorrelationVector;

    // Windows 11 / SDK 10.0.26100 added these two trailing members. The
    // platform validates StructSize, so omitting them makes CfExecute fail with
    // ERROR_CLOUD_FILE_INVALID_REQUEST (0x8007017C).
    public IntPtr SyncStatus;
    public long RequestKey;
}

// Overlays the native CF_OPERATION_PARAMETERS union. It is modelled as a
// sequential struct padded to the native size (48) rather than an overlapping
// explicit-layout union: overlapping fields make the struct non-blittable for
// P/Invoke marshalling, which silently zeroes the parameters and makes
// CfExecute fail with ERROR_CLOUD_FILE_INVALID_REQUEST.
[StructLayout(LayoutKind.Sequential, Size = 48)]
internal struct CF_OPERATION_PARAMETERS
{
    public uint ParamSize;

    public CF_OPERATION_TRANSFER_DATA TransferData;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CF_OPERATION_TRANSFER_DATA
{
    public CF_OPERATION_TRANSFER_DATA_FLAGS Flags;
    public int CompletionStatus;
    public IntPtr Buffer;
    public long Offset;
    public long Length;
}

// The same 48-byte union as CF_OPERATION_PARAMETERS, carrying the
// TransferPlaceholders member. A separate struct instead of an overlapping
// field, for the blittability reason given above.
[StructLayout(LayoutKind.Sequential, Size = 48)]
internal struct CF_OPERATION_PARAMETERS_PLACEHOLDERS
{
    public uint ParamSize;

    public CF_OPERATION_TRANSFER_PLACEHOLDERS TransferPlaceholders;
}

// The 48-byte union with its AckDelete / AckRename member:
// struct { flags; NTSTATUS CompletionStatus; } at the union offset (8). The
// fields do not overlap, so the struct stays blittable.
[StructLayout(LayoutKind.Explicit, Size = 48)]
internal struct CF_OPERATION_PARAMETERS_ACK
{
    [FieldOffset(0)]
    public uint ParamSize;

    [FieldOffset(8)]
    public uint Flags;

    [FieldOffset(12)]
    public int CompletionStatus;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CF_OPERATION_TRANSFER_PLACEHOLDERS
{
    public CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS Flags;
    public int CompletionStatus;
    public long PlaceholderTotalCount;
    public IntPtr PlaceholderArray;
    public uint PlaceholderCount;
    public uint EntriesProcessed;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CF_OPERATION_RETRIEVE_DATA
{
    public CF_OPERATION_RETRIEVE_DATA_FLAGS Flags;
    public IntPtr Buffer;
    public long Offset;
    public long Length;
    public long ReturnedLength;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CF_CALLBACK_REGISTRATION
{
    public CF_CALLBACK_TYPE Type;
    public IntPtr Callback;
}

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate void CF_CALLBACK(IntPtr callbackInfo, IntPtr callbackParameters);

internal enum CF_REGISTER_FLAGS : uint
{
    CF_REGISTER_FLAG_NONE = 0x00000000,
    CF_REGISTER_FLAG_UPDATE = 0x00000001,
    CF_REGISTER_FLAG_DISABLE_ON_DEMAND_POPULATION_ON_ROOT = 0x00000002,
    CF_REGISTER_FLAG_MARK_IN_SYNC_ON_ROOT = 0x00000004,
}

internal enum CF_HYDRATION_POLICY_PRIMARY : ushort
{
    CF_HYDRATION_POLICY_PARTIAL = 0,
    CF_HYDRATION_POLICY_PROGRESSIVE = 1,
    CF_HYDRATION_POLICY_FULL = 2,
    CF_HYDRATION_POLICY_ALWAYS_FULL = 3,
}

internal enum CF_HYDRATION_POLICY_MODIFIER : ushort
{
    CF_HYDRATION_POLICY_MODIFIER_NONE = 0x0000,
    CF_HYDRATION_POLICY_MODIFIER_VALIDATION_REQUIRED = 0x0001,
    CF_HYDRATION_POLICY_MODIFIER_STREAMING_ALLOWED = 0x0002,
}

internal enum CF_POPULATION_POLICY_PRIMARY : ushort
{
    CF_POPULATION_POLICY_PARTIAL = 0,
    CF_POPULATION_POLICY_FULL = 2,
    CF_POPULATION_POLICY_ALWAYS_FULL = 3,
}

internal enum CF_POPULATION_POLICY_MODIFIER : ushort
{
    CF_POPULATION_POLICY_MODIFIER_NONE = 0x0000,
}

internal enum CF_INSYNC_POLICY : uint
{
    CF_INSYNC_POLICY_NONE = 0x00000000,
    CF_INSYNC_POLICY_TRACK_FILE_CREATION_TIME = 0x00000001,
    CF_INSYNC_POLICY_TRACK_FILE_READONLY_ATTRIBUTE = 0x00000002,
    CF_INSYNC_POLICY_TRACK_FILE_HIDDEN_ATTRIBUTE = 0x00000004,
    CF_INSYNC_POLICY_TRACK_FILE_SYSTEM_ATTRIBUTE = 0x00000008,
    CF_INSYNC_POLICY_TRACK_DIRECTORY_CREATION_TIME = 0x00000010,
    CF_INSYNC_POLICY_TRACK_DIRECTORY_READONLY_ATTRIBUTE = 0x00000020,
    CF_INSYNC_POLICY_TRACK_DIRECTORY_HIDDEN_ATTRIBUTE = 0x00000040,
    CF_INSYNC_POLICY_TRACK_DIRECTORY_SYSTEM_ATTRIBUTE = 0x00000080,
    CF_INSYNC_POLICY_TRACK_FILE_LAST_WRITE_TIME = 0x00000100,
    CF_INSYNC_POLICY_TRACK_DIRECTORY_LAST_WRITE_TIME = 0x00000200,
    CF_INSYNC_POLICY_TRACK_FILE_ALL = 0x0055550f,
    CF_INSYNC_POLICY_TRACK_DIRECTORY_ALL = 0x00aaaaf0,
    CF_INSYNC_POLICY_TRACK_ALL = 0x00ffffff,
    CF_INSYNC_POLICY_PRESERVE_INSYNC_FOR_SYNC_ENGINE = 0x80000000,
}

internal enum CF_HARDLINK_POLICY : uint
{
    CF_HARDLINK_POLICY_NONE = 0x00000000,
    CF_HARDLINK_POLICY_ALLOWED = 0x00000001,
}

[Flags]
internal enum CF_PLACEHOLDER_CREATE_FLAGS : uint
{
    CF_PLACEHOLDER_CREATE_FLAG_NONE = 0x00000000,
    CF_PLACEHOLDER_CREATE_FLAG_DISABLE_ON_DEMAND_POPULATION = 0x00000001,
    CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC = 0x00000002,
    CF_PLACEHOLDER_CREATE_FLAG_SUPERSEDE = 0x00000004,
}

[Flags]
internal enum CF_CREATE_FLAGS : uint
{
    CF_CREATE_FLAG_NONE = 0x00000000,
    CF_CREATE_FLAG_STOP_ON_ERROR = 0x00000001,
}

internal enum CF_CALLBACK_TYPE : uint
{
    CF_CALLBACK_TYPE_FETCH_DATA = 0,
    CF_CALLBACK_TYPE_VALIDATE_DATA = 1,
    CF_CALLBACK_TYPE_CANCEL_FETCH_DATA = 2,
    CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS = 3,
    CF_CALLBACK_TYPE_CANCEL_FETCH_PLACEHOLDERS = 4,
    CF_CALLBACK_TYPE_NOTIFY_FILE_OPEN_COMPLETION = 5,
    CF_CALLBACK_TYPE_NOTIFY_FILE_CLOSE_COMPLETION = 6,
    CF_CALLBACK_TYPE_NOTIFY_DEHYDRATE = 7,
    CF_CALLBACK_TYPE_NOTIFY_DEHYDRATE_COMPLETION = 8,
    CF_CALLBACK_TYPE_NOTIFY_DELETE = 9,
    CF_CALLBACK_TYPE_NOTIFY_DELETE_COMPLETION = 10,
    CF_CALLBACK_TYPE_NOTIFY_RENAME = 11,
    CF_CALLBACK_TYPE_NOTIFY_RENAME_COMPLETION = 12,
    CF_CALLBACK_TYPE_NONE = 0xffffffff,
}

[Flags]
internal enum CF_CONNECT_FLAGS : uint
{
    CF_CONNECT_FLAG_NONE = 0x00000000,
    CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO = 0x00000002,
    CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH = 0x00000004,
    CF_CONNECT_FLAG_BLOCK_SELF_IMPLICIT_HYDRATION = 0x00000008,
}

internal enum CF_OPERATION_TYPE : uint
{
    CF_OPERATION_TYPE_TRANSFER_DATA = 0,
    CF_OPERATION_TYPE_RETRIEVE_DATA = 1,
    CF_OPERATION_TYPE_ACK_DATA = 2,
    CF_OPERATION_TYPE_RESTART_HYDRATION = 3,
    CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS = 4,
    CF_OPERATION_TYPE_ACK_DEHYDRATE = 5,
    CF_OPERATION_TYPE_ACK_DELETE = 6,
    CF_OPERATION_TYPE_ACK_RENAME = 7,
}

[Flags]
internal enum CF_OPERATION_TRANSFER_DATA_FLAGS : uint
{
    CF_OPERATION_TRANSFER_DATA_FLAG_NONE = 0x00000000,
}

[Flags]
internal enum CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS : uint
{
    CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_NONE = 0x00000000,
    CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_STOP_ON_ERROR = 0x00000001,
    CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_DISABLE_ON_DEMAND_POPULATION = 0x00000002,
}

[Flags]
internal enum CF_OPERATION_RETRIEVE_DATA_FLAGS : uint
{
    CF_OPERATION_RETRIEVE_DATA_FLAG_NONE = 0x00000000,
}

[Flags]
internal enum CF_HYDRATE_FLAGS : uint
{
    CF_HYDRATE_FLAG_NONE = 0x00000000,
}

[Flags]
internal enum CF_DEHYDRATE_FLAGS : uint
{
    CF_DEHYDRATE_FLAG_NONE = 0x00000000,
    CF_DEHYDRATE_FLAG_BACKGROUND = 0x00000001,
}

[Flags]
internal enum CF_CALLBACK_RENAME_FLAGS : uint
{
    CF_CALLBACK_RENAME_FLAG_NONE = 0x00000000,
    CF_CALLBACK_RENAME_FLAG_IS_DIRECTORY = 0x00000001,
    CF_CALLBACK_RENAME_FLAG_SOURCE_IN_SCOPE = 0x00000002,
    CF_CALLBACK_RENAME_FLAG_TARGET_IN_SCOPE = 0x00000004,
}

[Flags]
internal enum CF_CALLBACK_DELETE_FLAGS : uint
{
    CF_CALLBACK_DELETE_FLAG_NONE = 0x00000000,
    CF_CALLBACK_DELETE_FLAG_IS_DIRECTORY = 0x00000001,
    CF_CALLBACK_DELETE_FLAG_IS_UNDELETE = 0x00000002,
}

[Flags]
internal enum CF_UPDATE_FLAGS : uint
{
    CF_UPDATE_FLAG_NONE = 0x00000000,
    CF_UPDATE_FLAG_VERIFY_IN_SYNC = 0x00000001,
    CF_UPDATE_FLAG_MARK_IN_SYNC = 0x00000002,
    CF_UPDATE_FLAG_DEHYDRATE = 0x00000004,
    CF_UPDATE_FLAG_ENABLE_ON_DEMAND_POPULATION = 0x00000008,
    CF_UPDATE_FLAG_DISABLE_ON_DEMAND_POPULATION = 0x00000010,
    CF_UPDATE_FLAG_REMOVE_FILE_IDENTITY = 0x00000020,
    CF_UPDATE_FLAG_CLEAR_IN_SYNC = 0x00000040,
    CF_UPDATE_FLAG_REMOVE_PROPERTY = 0x00000080,
}

[Flags]
internal enum CF_CONVERT_FLAGS : uint
{
    CF_CONVERT_FLAG_NONE = 0x00000000,
    CF_CONVERT_FLAG_MARK_IN_SYNC = 0x00000001,
    CF_CONVERT_FLAG_DEHYDRATE = 0x00000002,
    CF_CONVERT_FLAG_ENABLE_ON_DEMAND_POPULATION = 0x00000004,
}

internal enum CF_PLACEHOLDER_INFO_CLASS : uint
{
    CF_PLACEHOLDER_INFO_BASIC = 0,
    CF_PLACEHOLDER_INFO_STANDARD = 1,
}

internal enum CF_IN_SYNC_STATE : uint
{
    CF_IN_SYNC_STATE_NOT_IN_SYNC = 0,
    CF_IN_SYNC_STATE_IN_SYNC = 1,
}

[Flags]
internal enum CF_SET_IN_SYNC_FLAGS : uint
{
    CF_SET_IN_SYNC_FLAG_NONE = 0x00000000,
}

[Flags]
internal enum CF_PLACEHOLDER_STATE : uint
{
    CF_PLACEHOLDER_STATE_NO_STATES = 0x00000000,
    CF_PLACEHOLDER_STATE_PLACEHOLDER = 0x00000001,
    CF_PLACEHOLDER_STATE_SYNC_ROOT = 0x00000002,
    CF_PLACEHOLDER_STATE_ESSENTIAL_PROP_PRESENT = 0x00000004,
    CF_PLACEHOLDER_STATE_IN_SYNC = 0x00000008,
    CF_PLACEHOLDER_STATE_PARTIAL = 0x00000010,
    CF_PLACEHOLDER_STATE_PARTIALLY_ON_DISK = 0x00000020,
    CF_PLACEHOLDER_STATE_INVALID = 0xffffffff,
}

[Flags]
internal enum CF_OPEN_FILE_FLAGS : uint
{
    CF_OPEN_FILE_FLAG_NONE = 0x00000000,
    CF_OPEN_FILE_FLAG_EXCLUSIVE = 0x00000001,
    CF_OPEN_FILE_FLAG_WRITE_ACCESS = 0x00000002,
    CF_OPEN_FILE_FLAG_DELETE_ACCESS = 0x00000004,
    CF_OPEN_FILE_FLAG_FOREGROUND = 0x00000008,
}
