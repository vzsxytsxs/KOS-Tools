namespace KosToolbox.Core.Drivers;

/// <summary>
/// Video driver on top of the driver store: what is installed, the set to remove for a vendor (video and,
/// optionally, its HDMI audio) and the video INFs of an extracted folder. Installing and removing go through pnputil.
/// </summary>
public static class DisplayDrivers
{
    /// <summary>Official download pages (the toolbox does not download installers by itself).</summary>
    public static readonly IReadOnlyList<(string Vendor, string Url)> DownloadPages = new[]
    {
        ("NVIDIA", "https://www.nvidia.com/en-us/drivers/"),
        ("AMD", "https://www.amd.com/en/support/download/drivers.html"),
        ("Intel", "https://www.intel.com/content/www/us/en/support/detect.html"),
    };

    public static List<DriverPackage> Installed(IEnumerable<DriverPackage> all) =>
        all.Where(p => p.Class.Equals("Display", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>"NVIDIA Corporation" and "NVIDIA" are the same vendor: compares the first word.</summary>
    public static string Vendor(string provider) => provider.Trim().Split(' ', 2)[0].ToUpperInvariant();

    /// <summary>Video packages of the vendor and, with <paramref name="includeAudio"/>, the audio ones (MEDIA class) of the same vendor.</summary>
    public static List<DriverPackage> RemovalSet(IEnumerable<DriverPackage> all, string provider, bool includeAudio)
    {
        var vendor = Vendor(provider);
        return all.Where(p => Vendor(p.Provider) == vendor
                              && (p.Class.Equals("Display", StringComparison.OrdinalIgnoreCase)
                                  || includeAudio && p.Class.Equals("MEDIA", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>Display-class INFs in a folder extracted from the installer (up to 4 levels; does not follow junctions).</summary>
    public static List<string> FindDisplayInfs(string folder)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        return Directory.EnumerateFiles(folder, "*.inf", options).Where(IsDisplayInf).OrderBy(f => f.Length).ToList();
    }

    internal static bool IsDisplayInf(string path)
    {
        try { return DriverStore.ParseInf(File.ReadLines(path).Take(400)).Class?.Equals("Display", StringComparison.OrdinalIgnoreCase) == true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
