using System.Runtime.InteropServices;
using OpenCfDrive.Core.Interop;

namespace OpenCfDrive.Tests;

/// <summary>
/// Asserts that the managed interop structs match the native layouts declared
/// in cfapi.h (Windows SDK 10.0.26100.0). These sizes/offsets are the contract
/// with the OS; a mismatch corrupts memory at runtime.
/// </summary>
public class CfInteropLayoutTests
{
    private static int OffsetOf<T>(string field) => (int)Marshal.OffsetOf<T>(field);

    [Fact]
    public void FsMetadata_MatchesNativeLayout()
    {
        // CF_FS_METADATA = FILE_BASIC_INFO(40) + LARGE_INTEGER FileSize(8)
        Assert.Equal(40, Marshal.SizeOf<CF_FILE_BASIC_INFO>());
        Assert.Equal(48, Marshal.SizeOf<CF_FS_METADATA>());
        Assert.Equal(0, OffsetOf<CF_FS_METADATA>(nameof(CF_FS_METADATA.BasicInfo)));
        Assert.Equal(40, OffsetOf<CF_FS_METADATA>(nameof(CF_FS_METADATA.FileSize)));
    }

    [Fact]
    public void PlaceholderCreateInfo_MatchesNativeLayout()
    {
        Assert.Equal(88, Marshal.SizeOf<CF_PLACEHOLDER_CREATE_INFO>());
        Assert.Equal(0, OffsetOf<CF_PLACEHOLDER_CREATE_INFO>(nameof(CF_PLACEHOLDER_CREATE_INFO.RelativeFileName)));
        Assert.Equal(8, OffsetOf<CF_PLACEHOLDER_CREATE_INFO>(nameof(CF_PLACEHOLDER_CREATE_INFO.FsMetadata)));
        Assert.Equal(56, OffsetOf<CF_PLACEHOLDER_CREATE_INFO>(nameof(CF_PLACEHOLDER_CREATE_INFO.FileIdentity)));
        Assert.Equal(64, OffsetOf<CF_PLACEHOLDER_CREATE_INFO>(nameof(CF_PLACEHOLDER_CREATE_INFO.FileIdentityLength)));
        Assert.Equal(68, OffsetOf<CF_PLACEHOLDER_CREATE_INFO>(nameof(CF_PLACEHOLDER_CREATE_INFO.Flags)));
        Assert.Equal(72, OffsetOf<CF_PLACEHOLDER_CREATE_INFO>(nameof(CF_PLACEHOLDER_CREATE_INFO.Result)));
        Assert.Equal(80, OffsetOf<CF_PLACEHOLDER_CREATE_INFO>(nameof(CF_PLACEHOLDER_CREATE_INFO.CreateUsn)));
    }

    [Fact]
    public void SyncRegistration_AndPolicies_MatchNativeLayout()
    {
        Assert.Equal(56, Marshal.SizeOf<CF_SYNC_REGISTRATION>());
        Assert.Equal(0, OffsetOf<CF_SYNC_REGISTRATION>(nameof(CF_SYNC_REGISTRATION.StructSize)));
        Assert.Equal(8, OffsetOf<CF_SYNC_REGISTRATION>(nameof(CF_SYNC_REGISTRATION.ProviderName)));
        Assert.Equal(16, OffsetOf<CF_SYNC_REGISTRATION>(nameof(CF_SYNC_REGISTRATION.ProviderVersion)));
        Assert.Equal(24, OffsetOf<CF_SYNC_REGISTRATION>(nameof(CF_SYNC_REGISTRATION.SyncRootIdentity)));
        Assert.Equal(32, OffsetOf<CF_SYNC_REGISTRATION>(nameof(CF_SYNC_REGISTRATION.SyncRootIdentityLength)));
        Assert.Equal(40, OffsetOf<CF_SYNC_REGISTRATION>(nameof(CF_SYNC_REGISTRATION.FileIdentity)));
        Assert.Equal(48, OffsetOf<CF_SYNC_REGISTRATION>(nameof(CF_SYNC_REGISTRATION.FileIdentityLength)));

        Assert.Equal(20, Marshal.SizeOf<CF_SYNC_POLICIES>());
        Assert.Equal(0, OffsetOf<CF_SYNC_POLICIES>(nameof(CF_SYNC_POLICIES.StructSize)));
        Assert.Equal(4, OffsetOf<CF_SYNC_POLICIES>(nameof(CF_SYNC_POLICIES.Hydration)));
        Assert.Equal(8, OffsetOf<CF_SYNC_POLICIES>(nameof(CF_SYNC_POLICIES.Population)));
        Assert.Equal(12, OffsetOf<CF_SYNC_POLICIES>(nameof(CF_SYNC_POLICIES.InSync)));
        Assert.Equal(16, OffsetOf<CF_SYNC_POLICIES>(nameof(CF_SYNC_POLICIES.HardLink)));
    }

    [Fact]
    public void CallbackInfo_MatchesNativeLayout()
    {
        // SDK 10.0.26100 appended RequestKey after ProcessInfo.
        Assert.Equal(152, Marshal.SizeOf<CF_CALLBACK_INFO>());
        Assert.Equal(0, OffsetOf<CF_CALLBACK_INFO>(nameof(CF_CALLBACK_INFO.StructSize)));
        Assert.Equal(8, OffsetOf<CF_CALLBACK_INFO>(nameof(CF_CALLBACK_INFO.ConnectionKey)));
        Assert.Equal(72, OffsetOf<CF_CALLBACK_INFO>(nameof(CF_CALLBACK_INFO.FileId)));
        Assert.Equal(80, OffsetOf<CF_CALLBACK_INFO>(nameof(CF_CALLBACK_INFO.FileSize)));
        Assert.Equal(88, OffsetOf<CF_CALLBACK_INFO>(nameof(CF_CALLBACK_INFO.FileIdentity)));
        Assert.Equal(96, OffsetOf<CF_CALLBACK_INFO>(nameof(CF_CALLBACK_INFO.FileIdentityLength)));
        Assert.Equal(104, OffsetOf<CF_CALLBACK_INFO>(nameof(CF_CALLBACK_INFO.NormalizedPath)));
        Assert.Equal(112, OffsetOf<CF_CALLBACK_INFO>(nameof(CF_CALLBACK_INFO.TransferKey)));
        Assert.Equal(120, OffsetOf<CF_CALLBACK_INFO>(nameof(CF_CALLBACK_INFO.PriorityHint)));
        Assert.Equal(128, OffsetOf<CF_CALLBACK_INFO>(nameof(CF_CALLBACK_INFO.CorrelationVector)));
        Assert.Equal(136, OffsetOf<CF_CALLBACK_INFO>(nameof(CF_CALLBACK_INFO.ProcessInfo)));
        Assert.Equal(144, OffsetOf<CF_CALLBACK_INFO>(nameof(CF_CALLBACK_INFO.RequestKey)));
    }

    [Fact]
    public void CallbackParameters_FetchDataOverlay_MatchesNativeLayout()
    {
        Assert.Equal(48, Marshal.SizeOf<CF_CALLBACK_PARAMETERS>());
        Assert.Equal(0, OffsetOf<CF_CALLBACK_PARAMETERS>(nameof(CF_CALLBACK_PARAMETERS.ParamSize)));
        Assert.Equal(8, OffsetOf<CF_CALLBACK_PARAMETERS>(nameof(CF_CALLBACK_PARAMETERS.FetchData)));

        Assert.Equal(40, Marshal.SizeOf<CF_CALLBACK_FETCH_DATA_PARAMETERS>());
        Assert.Equal(0, OffsetOf<CF_CALLBACK_FETCH_DATA_PARAMETERS>(nameof(CF_CALLBACK_FETCH_DATA_PARAMETERS.Flags)));
        Assert.Equal(8, OffsetOf<CF_CALLBACK_FETCH_DATA_PARAMETERS>(nameof(CF_CALLBACK_FETCH_DATA_PARAMETERS.RequiredFileOffset)));
        Assert.Equal(16, OffsetOf<CF_CALLBACK_FETCH_DATA_PARAMETERS>(nameof(CF_CALLBACK_FETCH_DATA_PARAMETERS.RequiredLength)));
        Assert.Equal(24, OffsetOf<CF_CALLBACK_FETCH_DATA_PARAMETERS>(nameof(CF_CALLBACK_FETCH_DATA_PARAMETERS.OptionalFileOffset)));
        Assert.Equal(32, OffsetOf<CF_CALLBACK_FETCH_DATA_PARAMETERS>(nameof(CF_CALLBACK_FETCH_DATA_PARAMETERS.OptionalLength)));
    }

    [Fact]
    public void CallbackParameters_RenameOverlay_MatchesNativeLayout()
    {
        // struct { CF_CALLBACK_RENAME_FLAGS Flags; PCWSTR TargetPath; } Rename
        Assert.Equal(8, OffsetOf<CF_CALLBACK_PARAMETERS>(nameof(CF_CALLBACK_PARAMETERS.RenameFlags)));
        Assert.Equal(16, OffsetOf<CF_CALLBACK_PARAMETERS>(nameof(CF_CALLBACK_PARAMETERS.RenameTargetPath)));
        Assert.Equal(8, OffsetOf<CF_CALLBACK_PARAMETERS>(nameof(CF_CALLBACK_PARAMETERS.DeleteFlags)));
    }

    [Fact]
    public void OperationInfo_AndTransferData_MatchNativeLayout()
    {
        // SDK 10.0.26100: StructSize, Type, ConnectionKey, TransferKey,
        // CorrelationVector, SyncStatus, RequestKey => 48 bytes.
        Assert.Equal(48, Marshal.SizeOf<CF_OPERATION_INFO>());
        Assert.Equal(0, OffsetOf<CF_OPERATION_INFO>(nameof(CF_OPERATION_INFO.StructSize)));
        Assert.Equal(4, OffsetOf<CF_OPERATION_INFO>(nameof(CF_OPERATION_INFO.Type)));
        Assert.Equal(8, OffsetOf<CF_OPERATION_INFO>(nameof(CF_OPERATION_INFO.ConnectionKey)));
        Assert.Equal(16, OffsetOf<CF_OPERATION_INFO>(nameof(CF_OPERATION_INFO.TransferKey)));
        Assert.Equal(24, OffsetOf<CF_OPERATION_INFO>(nameof(CF_OPERATION_INFO.CorrelationVector)));
        Assert.Equal(32, OffsetOf<CF_OPERATION_INFO>(nameof(CF_OPERATION_INFO.SyncStatus)));
        Assert.Equal(40, OffsetOf<CF_OPERATION_INFO>(nameof(CF_OPERATION_INFO.RequestKey)));

        // The union's largest member is RetrieveData (40), so the struct is 48.
        Assert.Equal(48, Marshal.SizeOf<CF_OPERATION_PARAMETERS>());
        Assert.Equal(8, OffsetOf<CF_OPERATION_PARAMETERS>(nameof(CF_OPERATION_PARAMETERS.TransferData)));

        Assert.Equal(32, Marshal.SizeOf<CF_OPERATION_TRANSFER_DATA>());
        Assert.Equal(0, OffsetOf<CF_OPERATION_TRANSFER_DATA>(nameof(CF_OPERATION_TRANSFER_DATA.Flags)));
        Assert.Equal(4, OffsetOf<CF_OPERATION_TRANSFER_DATA>(nameof(CF_OPERATION_TRANSFER_DATA.CompletionStatus)));
        Assert.Equal(8, OffsetOf<CF_OPERATION_TRANSFER_DATA>(nameof(CF_OPERATION_TRANSFER_DATA.Buffer)));
        Assert.Equal(16, OffsetOf<CF_OPERATION_TRANSFER_DATA>(nameof(CF_OPERATION_TRANSFER_DATA.Offset)));
        Assert.Equal(24, OffsetOf<CF_OPERATION_TRANSFER_DATA>(nameof(CF_OPERATION_TRANSFER_DATA.Length)));
    }

    [Fact]
    public void FetchPlaceholders_AndTransferPlaceholders_MatchNativeLayout()
    {
        // struct { CF_CALLBACK_FETCH_PLACEHOLDERS_FLAGS Flags; PCWSTR Pattern; } FetchPlaceholders
        Assert.Equal(8, OffsetOf<CF_CALLBACK_PARAMETERS>(nameof(CF_CALLBACK_PARAMETERS.FetchPlaceholdersFlags)));
        Assert.Equal(16, OffsetOf<CF_CALLBACK_PARAMETERS>(nameof(CF_CALLBACK_PARAMETERS.FetchPlaceholdersPattern)));

        Assert.Equal(48, Marshal.SizeOf<CF_OPERATION_PARAMETERS_PLACEHOLDERS>());
        Assert.Equal(8, OffsetOf<CF_OPERATION_PARAMETERS_PLACEHOLDERS>(nameof(CF_OPERATION_PARAMETERS_PLACEHOLDERS.TransferPlaceholders)));

        Assert.Equal(32, Marshal.SizeOf<CF_OPERATION_TRANSFER_PLACEHOLDERS>());
        Assert.Equal(0, OffsetOf<CF_OPERATION_TRANSFER_PLACEHOLDERS>(nameof(CF_OPERATION_TRANSFER_PLACEHOLDERS.Flags)));
        Assert.Equal(4, OffsetOf<CF_OPERATION_TRANSFER_PLACEHOLDERS>(nameof(CF_OPERATION_TRANSFER_PLACEHOLDERS.CompletionStatus)));
        Assert.Equal(8, OffsetOf<CF_OPERATION_TRANSFER_PLACEHOLDERS>(nameof(CF_OPERATION_TRANSFER_PLACEHOLDERS.PlaceholderTotalCount)));
        Assert.Equal(16, OffsetOf<CF_OPERATION_TRANSFER_PLACEHOLDERS>(nameof(CF_OPERATION_TRANSFER_PLACEHOLDERS.PlaceholderArray)));
        Assert.Equal(24, OffsetOf<CF_OPERATION_TRANSFER_PLACEHOLDERS>(nameof(CF_OPERATION_TRANSFER_PLACEHOLDERS.PlaceholderCount)));
        Assert.Equal(28, OffsetOf<CF_OPERATION_TRANSFER_PLACEHOLDERS>(nameof(CF_OPERATION_TRANSFER_PLACEHOLDERS.EntriesProcessed)));
    }

    [Fact]
    public void OperationParameters_ParamSizeConstant_IsCorrect()
    {
        // Verified against the running OS by CfExecuteContractTest: the
        // platform accepts StructSize=48 with ParamSize=40 and rejects 48.
        Assert.Equal(48, Marshal.SizeOf<CF_OPERATION_INFO>());
        Assert.Equal(48, Marshal.SizeOf<CF_OPERATION_PARAMETERS>());
        Assert.Equal(40, 8 + Marshal.SizeOf<CF_OPERATION_TRANSFER_DATA>());
    }

    [Fact]
    public void CallbackRegistration_AndConnectionKey_MatchNativeLayout()
    {
        Assert.Equal(16, Marshal.SizeOf<CF_CALLBACK_REGISTRATION>());
        Assert.Equal(0, OffsetOf<CF_CALLBACK_REGISTRATION>(nameof(CF_CALLBACK_REGISTRATION.Type)));
        Assert.Equal(8, OffsetOf<CF_CALLBACK_REGISTRATION>(nameof(CF_CALLBACK_REGISTRATION.Callback)));
        Assert.Equal(8, Marshal.SizeOf<CF_CONNECTION_KEY>());
    }
}
