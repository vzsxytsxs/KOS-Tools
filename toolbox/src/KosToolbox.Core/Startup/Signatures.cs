using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using KosToolbox.Core.Setup;

namespace KosToolbox.Core.Startup;

/// <summary>
/// Who signed a startup file, for display only (the Startup page's Signer column): its own Authenticode signature, else the
/// Windows catalog that lists its hash (how most Windows files are signed). Never used to decide whether to run something.
/// </summary>
public static class Signatures
{
    static readonly ConcurrentDictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string? Signer(string path) => Cache.GetOrAdd(Environment.ExpandEnvironmentVariables(path), p =>
    {
        try { return File.Exists(p) ? Installer.Signer(p) ?? CatalogSigner(p, "SHA256") ?? CatalogSigner(p, null) : null; }
        catch { return null; }
    });

    /// <summary>Windows programs that run another file (a script, a DLL, a command): their signer says nothing about what they run.</summary>
    public static bool IsHost(string path) => Path.GetFileName(path).ToLowerInvariant() is
        "wscript.exe" or "cscript.exe" or "cmd.exe" or "powershell.exe" or "pwsh.exe" or "rundll32.exe" or "mshta.exe" or "conhost.exe" or "regsvr32.exe" or "msiexec.exe";

    static readonly HashSet<string> MicrosoftNames = new(StringComparer.OrdinalIgnoreCase) { "Microsoft Corporation", "Microsoft Windows", "Microsoft Windows Publisher" };

    /// <summary>Signed by Microsoft itself, by exact name (not the hardware compatibility signature Microsoft gives other companies' drivers).</summary>
    public static bool IsMicrosoft(string? signer) => signer is not null && MicrosoftNames.Contains(signer);

    /// <summary>The catalog's signer when a catalog lists this file's hash and WinVerifyTrust accepts the file as its member.</summary>
    static string? CatalogSigner(string path, string? algorithm)
    {
        if (!CryptCATAdminAcquireContext2(out var admin, IntPtr.Zero, algorithm, IntPtr.Zero, 0)) return null;
        var info = IntPtr.Zero;
        try
        {
            byte[] hash;
            using (var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                uint size = 0;
                CryptCATAdminCalcHashFromFileHandle2(admin, file, ref size, null, 0);
                if (size is 0 or > 64) return null;
                hash = new byte[size];
                if (!CryptCATAdminCalcHashFromFileHandle2(admin, file, ref size, hash, 0)) return null;
            }
            info = CryptCATAdminEnumCatalogFromHash(admin, hash, (uint)hash.Length, 0, IntPtr.Zero);
            if (info == IntPtr.Zero) return null;
            var cat = new CatalogInfo { cbStruct = (uint)Marshal.SizeOf<CatalogInfo>() };
            if (!CryptCATCatalogInfoFromContext(info, ref cat, 0)) return null;
            if (!VerifyMember(admin, cat.wszCatalogFile, path, hash)) return null;
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(cat.wszCatalogFile); // a signed PKCS #7 file gives its signer's certificate
#pragma warning restore SYSLIB0057
            return cert.GetNameInfo(X509NameType.SimpleName, false);
        }
        catch { return null; }
        finally
        {
            if (info != IntPtr.Zero) CryptCATAdminReleaseCatalogContext(admin, info, 0);
            CryptCATAdminReleaseContext(admin, 0);
        }
    }

    static bool VerifyMember(IntPtr admin, string catalog, string path, byte[] hash)
    {
        var catalogPath = Marshal.StringToHGlobalUni(catalog);
        var tag = Marshal.StringToHGlobalUni(Convert.ToHexString(hash));
        var member = Marshal.StringToHGlobalUni(path);
        var hashPtr = Marshal.AllocHGlobal(hash.Length);
        var infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustCatalogInfo>());
        try
        {
            Marshal.Copy(hash, 0, hashPtr, hash.Length);
            Marshal.StructureToPtr(new WintrustCatalogInfo
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustCatalogInfo>(), pcwszCatalogFilePath = catalogPath, pcwszMemberTag = tag, pcwszMemberFilePath = member,
                pbCalculatedFileHash = hashPtr, cbCalculatedFileHash = (uint)hash.Length, hCatAdmin = admin,
            }, infoPtr, false);
            var data = new Installer.WintrustData
            {
                cbStruct = (uint)Marshal.SizeOf<Installer.WintrustData>(),
                dwUIChoice = 2,          // WTD_UI_NONE
                fdwRevocationChecks = 0, // no revocation check over the network
                dwUnionChoice = 2,       // WTD_CHOICE_CATALOG
                pFile = infoPtr,
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return Installer.WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(infoPtr);
            Marshal.FreeHGlobal(hashPtr);
            Marshal.FreeHGlobal(member);
            Marshal.FreeHGlobal(tag);
            Marshal.FreeHGlobal(catalogPath);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CatalogInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string wszCatalogFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WintrustCatalogInfo
    {
        public uint cbStruct, dwCatalogVersion;
        public IntPtr pcwszCatalogFilePath, pcwszMemberTag, pcwszMemberFilePath, hMemberFile, pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public IntPtr pcCatalogContext, hCatAdmin;
    }

    [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptCATAdminAcquireContext2(out IntPtr admin, IntPtr subsystem, string? hashAlgorithm, IntPtr strongHashPolicy, uint flags);
    [DllImport("wintrust.dll", SetLastError = true)]
    static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr admin, Microsoft.Win32.SafeHandles.SafeFileHandle file, ref uint size, byte[]? hash, uint flags);
    [DllImport("wintrust.dll", SetLastError = true)]
    static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr admin, byte[] hash, uint size, uint flags, IntPtr previous);
    [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptCATCatalogInfoFromContext(IntPtr catInfo, ref CatalogInfo info, uint flags);
    [DllImport("wintrust.dll")] static extern bool CryptCATAdminReleaseCatalogContext(IntPtr admin, IntPtr catInfo, uint flags);
    [DllImport("wintrust.dll")] static extern bool CryptCATAdminReleaseContext(IntPtr admin, uint flags);
}
