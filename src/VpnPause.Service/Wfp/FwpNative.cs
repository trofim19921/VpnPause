using System.Runtime.InteropServices;

namespace VpnPause.Service.Wfp;

// Layouts are x64 only (the product ships win-x64). Offsets match fwpmtypes.h.

[StructLayout(LayoutKind.Sequential)]
struct FWPM_DISPLAY_DATA0
{
    public IntPtr name;
    public IntPtr description;
}

[StructLayout(LayoutKind.Sequential)]
struct FWP_BYTE_BLOB
{
    public uint size;
    public IntPtr data;
}

[StructLayout(LayoutKind.Explicit, Size = 16)]
struct FWP_VALUE0
{
    [FieldOffset(0)] public uint type;
    [FieldOffset(8)] public byte uint8;
    [FieldOffset(8)] public uint uint32;
    [FieldOffset(8)] public IntPtr pointer;
}

[StructLayout(LayoutKind.Explicit, Size = 40)]
struct FWPM_FILTER_CONDITION0
{
    [FieldOffset(0)] public Guid fieldKey;
    [FieldOffset(16)] public uint matchType;
    [FieldOffset(24)] public FWP_VALUE0 conditionValue;
}

[StructLayout(LayoutKind.Explicit, Size = 72)]
struct FWPM_SESSION0
{
    [FieldOffset(0)] public Guid sessionKey;
    [FieldOffset(16)] public FWPM_DISPLAY_DATA0 displayData;
    [FieldOffset(32)] public uint flags;
    [FieldOffset(36)] public uint txnWaitTimeoutInMSec;
    [FieldOffset(40)] public uint processId;
    [FieldOffset(48)] public IntPtr sid;
    [FieldOffset(56)] public IntPtr username;
    [FieldOffset(64)] public int kernelMode;
}

[StructLayout(LayoutKind.Explicit, Size = 64)]
struct FWPM_PROVIDER0
{
    [FieldOffset(0)] public Guid providerKey;
    [FieldOffset(16)] public FWPM_DISPLAY_DATA0 displayData;
    [FieldOffset(32)] public uint flags;
    [FieldOffset(40)] public FWP_BYTE_BLOB providerData;
    [FieldOffset(56)] public IntPtr serviceName;
}

[StructLayout(LayoutKind.Explicit, Size = 72)]
struct FWPM_SUBLAYER0
{
    [FieldOffset(0)] public Guid subLayerKey;
    [FieldOffset(16)] public FWPM_DISPLAY_DATA0 displayData;
    [FieldOffset(32)] public uint flags;
    [FieldOffset(40)] public IntPtr providerKey;
    [FieldOffset(48)] public FWP_BYTE_BLOB providerData;
    [FieldOffset(64)] public ushort weight;
}

[StructLayout(LayoutKind.Explicit, Size = 200)]
struct FWPM_FILTER0
{
    [FieldOffset(0)] public Guid filterKey;
    [FieldOffset(16)] public FWPM_DISPLAY_DATA0 displayData;
    [FieldOffset(32)] public uint flags;
    [FieldOffset(40)] public IntPtr providerKey;
    [FieldOffset(48)] public FWP_BYTE_BLOB providerData;
    [FieldOffset(64)] public Guid layerKey;
    [FieldOffset(80)] public Guid subLayerKey;
    [FieldOffset(96)] public FWP_VALUE0 weight;
    [FieldOffset(112)] public uint numFilterConditions;
    [FieldOffset(120)] public IntPtr filterCondition;
    [FieldOffset(128)] public uint actionType;
    [FieldOffset(132)] public Guid actionFilterType;
    [FieldOffset(152)] public Guid providerContextKey;
    [FieldOffset(168)] public IntPtr reserved;
    [FieldOffset(176)] public ulong filterId;
    [FieldOffset(184)] public FWP_VALUE0 effectiveWeight;
}

[StructLayout(LayoutKind.Sequential)]
struct FWP_V4_ADDR_AND_MASK
{
    public uint addr;
    public uint mask;
}

[StructLayout(LayoutKind.Sequential, Size = 17)]
unsafe struct FWP_V6_ADDR_AND_MASK
{
    public fixed byte addr[16];
    public byte prefixLength;
}

static class FwpConst
{
    public const uint RPC_C_AUTHN_WINNT = 10;
    public const uint FWPM_SESSION_FLAG_DYNAMIC = 0x1;
    public const uint FWPM_PROVIDER_FLAG_PERSISTENT = 0x1;
    public const uint FWPM_SUBLAYER_FLAG_PERSISTENT = 0x1;
    public const uint FWPM_FILTER_FLAG_PERSISTENT = 0x1;
    public const uint FWPM_FILTER_FLAG_CLEAR_ACTION_RIGHT = 0x8;

    public const uint FWP_ACTION_BLOCK = 0x1001;
    public const uint FWP_ACTION_PERMIT = 0x1002;

    public const uint FWP_EMPTY = 0;
    public const uint FWP_UINT8 = 1;
    public const uint FWP_UINT32 = 3;
    public const uint FWP_UINT64 = 4;
    public const uint FWP_BYTE_BLOB_TYPE = 12;
    public const uint FWP_SID = 13;
    public const uint FWP_V4_ADDR_MASK = 0x100;
    public const uint FWP_V6_ADDR_MASK = 0x101;

    public const uint FWP_MATCH_EQUAL = 0;
    public const uint FWP_MATCH_FLAGS_ALL_SET = 6;

    public const uint FWP_CONDITION_FLAG_IS_LOOPBACK = 0x1;

    public const uint FWP_E_ALREADY_EXISTS = 0x80320009;
    public const uint FWP_E_NOT_FOUND = 0x80320008;
    public const uint FWP_E_FILTER_NOT_FOUND = 0x80320003;
    public const uint FWP_E_PROVIDER_NOT_FOUND = 0x80320005;
    public const uint FWP_E_SUBLAYER_NOT_FOUND = 0x80320007;
    public const uint FWP_E_IN_USE = 0x8032000A;

    public static readonly Guid LayerConnectV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    public static readonly Guid LayerConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    public static readonly Guid LayerRecvAcceptV4 = new("e1cd9fe7-f4b5-4273-96c0-592e487b8650");
    public static readonly Guid LayerRecvAcceptV6 = new("a3b42c97-9f04-4672-b87e-cee9c483257f");

    public static readonly Guid CondAppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    public static readonly Guid CondLocalInterface = new("4cd62a49-59c3-4969-b7f3-bda5d32890a4");
    public static readonly Guid CondFlags = new("632ce23b-5167-435c-86d7-e903684aa80c");
    public static readonly Guid CondPackageId = new("71bc78fa-f17c-4997-a602-6abb261f351c");
    public static readonly Guid CondRemoteAddress = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
}

static unsafe class FwpNative
{
    const string Lib = "fwpuclnt.dll";

    [DllImport(Lib)] public static extern uint FwpmEngineOpen0([MarshalAs(UnmanagedType.LPWStr)] string? serverName, uint authnService, IntPtr authIdentity, FWPM_SESSION0* session, out IntPtr engineHandle);
    [DllImport(Lib)] public static extern uint FwpmEngineClose0(IntPtr engineHandle);
    [DllImport(Lib)] public static extern uint FwpmTransactionBegin0(IntPtr engineHandle, uint flags);
    [DllImport(Lib)] public static extern uint FwpmTransactionCommit0(IntPtr engineHandle);
    [DllImport(Lib)] public static extern uint FwpmTransactionAbort0(IntPtr engineHandle);
    [DllImport(Lib)] public static extern uint FwpmProviderAdd0(IntPtr engineHandle, FWPM_PROVIDER0* provider, IntPtr sd);
    [DllImport(Lib)] public static extern uint FwpmProviderDeleteByKey0(IntPtr engineHandle, Guid* key);
    [DllImport(Lib)] public static extern uint FwpmSubLayerAdd0(IntPtr engineHandle, FWPM_SUBLAYER0* subLayer, IntPtr sd);
    [DllImport(Lib)] public static extern uint FwpmSubLayerDeleteByKey0(IntPtr engineHandle, Guid* key);
    [DllImport(Lib)] public static extern uint FwpmFilterAdd0(IntPtr engineHandle, FWPM_FILTER0* filter, IntPtr sd, out ulong id);
    [DllImport(Lib)] public static extern uint FwpmFilterDeleteById0(IntPtr engineHandle, ulong id);
    [DllImport(Lib)] public static extern uint FwpmFilterCreateEnumHandle0(IntPtr engineHandle, IntPtr enumTemplate, out IntPtr enumHandle);
    [DllImport(Lib)] public static extern uint FwpmFilterEnum0(IntPtr engineHandle, IntPtr enumHandle, uint numEntriesRequested, out IntPtr entries, out uint numEntriesReturned);
    [DllImport(Lib)] public static extern uint FwpmFilterDestroyEnumHandle0(IntPtr engineHandle, IntPtr enumHandle);
    [DllImport(Lib)] public static extern void FwpmFreeMemory0(ref IntPtr p);
    [DllImport(Lib, CharSet = CharSet.Unicode)] public static extern uint FwpmGetAppIdFromFileName0(string fileName, out IntPtr appId);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] public static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
    [DllImport("advapi32.dll")] public static extern IntPtr FreeSid(IntPtr sid);
    [DllImport("advapi32.dll")] public static extern int GetLengthSid(IntPtr sid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern uint GetLongPathNameW(string shortPath, [Out] char[] longPath, int size);
}
