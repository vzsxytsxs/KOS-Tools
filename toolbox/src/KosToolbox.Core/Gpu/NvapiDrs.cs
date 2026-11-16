using System.Runtime.InteropServices;
using System.Text;

namespace KosToolbox.Core.Gpu;

/// <summary>
/// DRS (Driver Settings) of NVAPI: the driver's profile database that the NVIDIA panel and NVIDIA Profile Inspector
/// edit. Public structures from nvapi.h (NVDRS_PROFILE_V1, NVDRS_SETTING_V1, NVDRS_SETTING_VALUES_V1).
/// </summary>
internal static partial class Nvapi
{
    const uint DrsCreateSession = 0x0694D52E, DrsDestroySession = 0xDAD9CFF8, DrsLoadSettings = 0x375DBD6B, DrsSaveSettings = 0xFCBC7E14;
    const uint DrsGetCurrentGlobalProfile = 0x617BFF9F, DrsGetNumProfiles = 0x1DAE4FBC, DrsEnumProfiles = 0xBC371EE0, DrsGetProfileInfo = 0x61CD6FD6;
    const uint DrsGetSetting = 0x73BF8338, DrsSetSetting = 0x577DD202, DrsRestoreProfileDefaultSetting = 0x53F0381E, DrsRestoreAllDefaults = 0x5927B094;
    const uint DrsEnumAvailableSettingIds = 0xF020614A, DrsGetSettingNameFromId = 0xD61CBE6E, DrsEnumAvailableSettingValues = 0x2EC39F90;
    const uint DrsEnumSettings = 0xAE3039DA;
    const uint DrsFindApplicationByName = 0xEEE566B2, DrsCreateProfile = 0xCC176068, DrsCreateApplication = 0x4347A9DE;
    const uint DrsEnumApplications = 0x7FA2173A, DrsDeleteProfile = 0x17093206;
    // NVDRS_APPLICATION_V4: version, isPredefined, appName, userFriendlyName, launcher, fileInFolder, flags, commandLine.
    const int AppSize = 8 + 5 * UnicodeBytes + 4, AppNameAt = 8, AppFriendlyAt = 8 + UnicodeBytes; // 20492
    public const int ExecutableNotFound = -166;

    // NvAPI_UnicodeString = NvU16[2048]; NVDRS_BINARY_SETTING = NvU32 + 4096 bytes.
    const int UnicodeBytes = 4096, ValueUnionBytes = 4100;
    const int ProfileSize = 4 + UnicodeBytes + 16;                       // 4116
    const int SettingSize = 4 + UnicodeBytes + 20 + 2 * ValueUnionBytes;  // 12320
    const int SettingIdAt = 4100, SettingTypeAt = 4104, SettingLocationAt = 4108, CurrentPredefinedAt = 4112, PredefinedValidAt = 4116;
    const int PredefinedAt = 4120, CurrentAt = 8220;
    const int ValuesMax = 100, ValuesSize = 12 + ValueUnionBytes + ValuesMax * ValueUnionBytes;
    public const int SettingNotFound = -160, NotSupported = -104;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int OutPtr(out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int OnePtr(IntPtr a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PtrOutPtr(IntPtr a, out IntPtr b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PtrOutUInt(IntPtr a, out uint b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PtrUIntOutPtr(IntPtr a, uint index, out IntPtr b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PtrPtrBuf(IntPtr a, IntPtr b, IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PtrPtrUIntBuf(IntPtr a, IntPtr b, uint id, IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PtrPtrUInt(IntPtr a, IntPtr b, uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int BufRefUInt(IntPtr buffer, ref uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int UIntBuf(uint id, IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int UIntRefUIntBuf(uint id, ref uint count, IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PtrPtrUIntRefUIntBuf(IntPtr a, IntPtr b, uint start, ref uint count, IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PtrBufOutPtrBuf(IntPtr a, IntPtr name, out IntPtr profile, IntPtr app);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PtrBufOutPtr(IntPtr a, IntPtr buffer, out IntPtr b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PtrPtr(IntPtr a, IntPtr b);

    static void PutUnicode(byte[] data, int at, string s)
    {
        if (s.Length >= UnicodeBytes / 2) throw new ArgumentException("Name too long for the driver.", nameof(s));
        Encoding.Unicode.GetBytes(s).CopyTo(data, at);
    }

    static byte[] NewApplication()
    {
        var app = new byte[AppSize];
        Put(app, 0, AppSize | (4 << 16));
        return app;
    }

    /// <summary>The profile the driver uses for this program (file name or full path); null when none names it.</summary>
    public static (IntPtr Profile, string ProfileName, bool Predefined)? DrsFindApplication(IntPtr session, string program)
    {
        lock (Gate)
        {
            var name = new byte[UnicodeBytes];
            PutUnicode(name, 0, program);
            var app = NewApplication();
            var profile = IntPtr.Zero;
            var code = WithBuffer(name, n => WithBuffer(app, a => Function<PtrBufOutPtrBuf>(DrsFindApplicationByName)(session, n, out profile, a)));
            if (code == ExecutableNotFound) return null;
            Check(code, "NvAPI_DRS_FindApplicationByName");
            var (profileName, predefined) = ProfileInfo(session, profile);
            return (profile, profileName, predefined);
        }
    }

    /// <summary>A new user profile with this program in it (in the session; takes effect on Save).</summary>
    public static IntPtr DrsCreateProfileFor(IntPtr session, string profileName, string program)
    {
        lock (Gate)
        {
            var info = new byte[ProfileSize];
            Put(info, 0, ProfileSize | 0x10000);
            PutUnicode(info, 4, profileName);
            var profile = IntPtr.Zero;
            Check(WithBuffer(info, p => Function<PtrBufOutPtr>(DrsCreateProfile)(session, p, out profile)), "NvAPI_DRS_CreateProfile");
            var app = NewApplication();
            PutUnicode(app, AppNameAt, program);
            PutUnicode(app, AppFriendlyAt, program);
            var code = WithBuffer(app, a => Function<PtrPtrBuf>(DrsCreateApplication)(session, profile, a));
            if (code != 0) { Function<PtrPtr>(DrsDeleteProfile)(session, profile); Check(code, "NvAPI_DRS_CreateApplication"); }
            return profile;
        }
    }

    /// <summary>The programs (application names) of a profile.</summary>
    public static List<string> DrsApplications(IntPtr session, IntPtr profile)
    {
        lock (Gate)
        {
            var info = new byte[ProfileSize];
            Put(info, 0, ProfileSize | 0x10000);
            Check(WithBuffer(info, p => Function<PtrPtrBuf>(DrsGetProfileInfo)(session, profile, p)), "NvAPI_DRS_GetProfileInfo");
            var total = (uint)I(info, 4108);
            var list = new List<string>();
            const uint Batch = 32;
            for (uint start = 0; start < total;)
            {
                var count = Math.Min(Batch, total - start);
                var buffer = new byte[AppSize * count];
                for (var i = 0; i < count; i++) Put(buffer, i * AppSize, AppSize | (4 << 16));
                Check(WithBuffer(buffer, b => Function<PtrPtrUIntRefUIntBuf>(DrsEnumApplications)(session, profile, start, ref count, b)), "NvAPI_DRS_EnumApplications");
                if (count == 0) break;
                for (var i = 0; i < count; i++) list.Add(Unicode(buffer, i * AppSize + AppNameAt));
                start += count;
            }
            return list;
        }
    }

    /// <summary>Deletes a profile (in the session; takes effect on Save).</summary>
    public static void DrsDeleteProfileHandle(IntPtr session, IntPtr profile)
    {
        lock (Gate) Check(Function<PtrPtr>(DrsDeleteProfile)(session, profile), "NvAPI_DRS_DeleteProfile");
    }

    static string Unicode(byte[] data, int at)
    {
        var s = Encoding.Unicode.GetString(data, at, UnicodeBytes);
        var end = s.IndexOf('\0');
        return end >= 0 ? s[..end] : s;
    }

    public static IntPtr DrsOpen()
    {
        lock (Gate)
        {
            EnsureReady();
            Check(Function<OutPtr>(DrsCreateSession)(out var session), "NvAPI_DRS_CreateSession");
            var code = Function<OnePtr>(DrsLoadSettings)(session);
            if (code != 0) { Function<OnePtr>(DrsDestroySession)(session); Check(code, "NvAPI_DRS_LoadSettings"); }
            return session;
        }
    }

    public static void DrsClose(IntPtr session)
    {
        lock (Gate) { if (session != IntPtr.Zero) Function<OnePtr>(DrsDestroySession)(session); }
    }

    public static void DrsSave(IntPtr session)
    {
        lock (Gate) Check(Function<OnePtr>(DrsSaveSettings)(session), "NvAPI_DRS_SaveSettings");
    }

    public static void DrsRestoreAll(IntPtr session)
    {
        lock (Gate) Check(Function<OnePtr>(DrsRestoreAllDefaults)(session), "NvAPI_DRS_RestoreAllDefaults");
    }

    public static IntPtr DrsGlobalProfile(IntPtr session)
    {
        lock (Gate)
        {
            Check(Function<PtrOutPtr>(DrsGetCurrentGlobalProfile)(session, out var profile), "NvAPI_DRS_GetCurrentGlobalProfile");
            return profile;
        }
    }

    /// <summary>All profiles: handle, name and whether it is an NVIDIA (predefined) profile.</summary>
    public static List<(IntPtr Handle, string Name, bool Predefined)> DrsProfiles(IntPtr session)
    {
        lock (Gate)
        {
            Check(Function<PtrOutUInt>(DrsGetNumProfiles)(session, out var count), "NvAPI_DRS_GetNumProfiles");
            var list = new List<(IntPtr, string, bool)>((int)count);
            var enumerate = Function<PtrUIntOutPtr>(DrsEnumProfiles);
            for (uint i = 0; i < count; i++)
            {
                if (enumerate(session, i, out var profile) != 0) break;
                var (name, predefined) = ProfileInfo(session, profile);
                list.Add((profile, name, predefined));
            }
            return list;
        }
    }

    static (string Name, bool Predefined) ProfileInfo(IntPtr session, IntPtr profile)
    {
        var info = new byte[ProfileSize];
        Put(info, 0, ProfileSize | 0x10000);
        Check(WithBuffer(info, p => Function<PtrPtrBuf>(DrsGetProfileInfo)(session, profile, p)), "NvAPI_DRS_GetProfileInfo");
        return (Unicode(info, 4), I(info, 4104) != 0);
    }

    public static string DrsProfileName(IntPtr session, IntPtr profile)
    {
        lock (Gate) return ProfileInfo(session, profile).Name;
    }

    /// <summary>A value of the profile; null if the setting does not exist in it (nor inherited).</summary>
    public static DrsStored? DrsGet(IntPtr session, IntPtr profile, uint id)
    {
        lock (Gate)
        {
            var data = new byte[SettingSize];
            Put(data, 0, SettingSize | 0x10000);
            var code = WithBuffer(data, p => Function<PtrPtrUIntBuf>(DrsGetSetting)(session, profile, id, p));
            if (code == SettingNotFound) return null;
            Check(code, "NvAPI_DRS_GetSetting");
            return ReadSetting(data, 0);
        }
    }

    /// <summary>The settings stored in the profile (all at once, instead of one call per id).</summary>
    public static List<DrsStored> DrsEnum(IntPtr session, IntPtr profile)
    {
        lock (Gate)
        {
            var list = new List<DrsStored>();
            const int Batch = 32;
            var buffer = Marshal.AllocHGlobal(SettingSize * Batch);
            try
            {
                for (uint start = 0; ; start += Batch)
                {
                    var data = new byte[SettingSize * Batch];
                    for (var i = 0; i < Batch; i++) Put(data, i * SettingSize, SettingSize | 0x10000);
                    Marshal.Copy(data, 0, buffer, data.Length);
                    uint count = Batch;
                    var code = Function<PtrPtrUIntRefUIntBuf>(DrsEnumSettings)(session, profile, start, ref count, buffer);
                    if (code == -7 /* END_ENUMERATION */ || count == 0) break;
                    Check(code, "NvAPI_DRS_EnumSettings");
                    Marshal.Copy(buffer, data, 0, data.Length);
                    for (var i = 0; i < count; i++) list.Add(ReadSetting(data, i * SettingSize));
                    if (count < Batch) break;
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return list;
        }
    }

    static DrsStored ReadSetting(byte[] d, int at)
    {
        var type = I(d, at + SettingTypeAt);
        string? Text(int union) => type is 2 or 3 ? Unicode(d, at + union) : type == 1 ? Convert.ToHexString(d, at + union + 4, Math.Clamp(I(d, at + union), 0, 4096)) : null;
        return new DrsStored(
            BitConverter.ToUInt32(d, at + SettingIdAt), (DrsType)type, I(d, at + SettingLocationAt),
            I(d, at + CurrentPredefinedAt) != 0, I(d, at + PredefinedValidAt) != 0,
            BitConverter.ToUInt32(d, at + CurrentAt), Text(CurrentAt),
            BitConverter.ToUInt32(d, at + PredefinedAt), Text(PredefinedAt));
    }

    /// <summary>Writes to the session. The driver refuses without a fatal error what it lacks (SETTING_NOT_FOUND) and what is not valid in this profile (NOT_SUPPORTED, e.g. Ansel on the global one).</summary>
    public static DrsWrite DrsSet(IntPtr session, IntPtr profile, uint id, DrsType type, uint dword, string? text)
    {
        lock (Gate)
        {
            var data = new byte[SettingSize];
            Put(data, 0, SettingSize | 0x10000);
            Put(data, SettingIdAt, unchecked((int)id));
            Put(data, SettingTypeAt, (int)type);
            if (type == DrsType.Dword) Put(data, CurrentAt, unchecked((int)dword));
            else if (type is DrsType.String or DrsType.WString)
            {
                var bytes = Encoding.Unicode.GetBytes(text ?? "");
                Array.Copy(bytes, 0, data, CurrentAt, Math.Min(bytes.Length, UnicodeBytes - 2));
            }
            else throw new NotSupportedException("Binary driver settings can't be edited here.");
            var code = WithBuffer(data, p => Function<PtrPtrBuf>(DrsSetSetting)(session, profile, p));
            if (code == SettingNotFound) return DrsWrite.NotInDriver;
            if (code == NotSupported) return DrsWrite.NotOnThisProfile;
            Check(code, "NvAPI_DRS_SetSetting");
            return DrsWrite.Ok;
        }
    }

    public static void DrsRestoreDefault(IntPtr session, IntPtr profile, uint id)
    {
        lock (Gate)
        {
            var code = Function<PtrPtrUInt>(DrsRestoreProfileDefaultSetting)(session, profile, id);
            if (code != SettingNotFound) Check(code, "NvAPI_DRS_RestoreProfileDefaultSetting");
        }
    }

    /// <summary>Ids of all settings the driver knows (documented).</summary>
    public static List<uint> DrsAvailableIds()
    {
        lock (Gate)
        {
            EnsureReady();
            uint count = 8192;
            var buffer = Marshal.AllocHGlobal((int)count * 4);
            try
            {
                Check(Function<BufRefUInt>(DrsEnumAvailableSettingIds)(buffer, ref count), "NvAPI_DRS_EnumAvailableSettingIds");
                var ids = new int[count];
                Marshal.Copy(buffer, ids, 0, (int)count);
                return ids.Select(i => unchecked((uint)i)).ToList();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    public static string? DrsSettingName(uint id)
    {
        lock (Gate)
        {
            var data = new byte[UnicodeBytes];
            return WithBuffer(data, p => Function<UIntBuf>(DrsGetSettingNameFromId)(id, p)) == 0 ? Unicode(data, 0) : null;
        }
    }

    /// <summary>Values the driver documents for a DWORD setting (up to 100) and the default.</summary>
    public static (IReadOnlyList<uint> Values, uint Default)? DrsAvailableValues(uint id)
    {
        lock (Gate)
        {
            var data = new byte[ValuesSize];
            Put(data, 0, ValuesSize | 0x10000);
            uint max = ValuesMax;
            if (WithBuffer(data, p => Function<UIntRefUIntBuf>(DrsEnumAvailableSettingValues)(id, ref max, p)) != 0 || I(data, 8) != 0) return null;
            var count = Math.Min((int)max, Math.Min(I(data, 4), ValuesMax));
            var values = Enumerable.Range(0, count).Select(i => BitConverter.ToUInt32(data, 12 + ValueUnionBytes + i * ValueUnionBytes)).ToList();
            return (values, BitConverter.ToUInt32(data, 12));
        }
    }
}

public enum DrsType { Dword = 0, Binary = 1, String = 2, WString = 3 }

/// <summary>Result of a write: accepted, the driver lacks the setting, or it is not valid in this profile.</summary>
public enum DrsWrite { Ok, NotInDriver, NotOnThisProfile }

/// <summary>
/// Value of a setting read from the driver. Location: 0 = stored in this profile, 1 = inherited from the global one, 2 = from the base
/// profile, 3 = driver default. Predefined = the value NVIDIA puts in the profile.
/// </summary>
public sealed record DrsStored(uint Id, DrsType Type, int Location, bool IsCurrentPredefined, bool PredefinedValid,
    uint Dword, string? Text, uint PredefinedDword, string? PredefinedText);
