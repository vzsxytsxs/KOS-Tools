using KosToolbox.Core.Firewall;
using Xunit;

namespace KosToolbox.Core.Tests;

public class HostsFileTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("kos-hosts-").FullName;
    string Hosts => Path.Combine(dir, "hosts");
    string Backups => Path.Combine(dir, "bak");
    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void ParsesEachLineKind()
    {
        var l = HostsFile.Parse("# top\r\n\r\n127.0.0.1 a.test b.test # note\r\nbogus\r\n  # indented\r\n");
        Assert.Equal(5, l.Count);
        Assert.Equal([HostsLineKind.Comment, HostsLineKind.Blank, HostsLineKind.Entry, HostsLineKind.Invalid, HostsLineKind.Comment],
            l.Select(x => x.Kind));
        Assert.Equal(3, l[2].Number);
        Assert.Equal("127.0.0.1", l[2].Address);
        Assert.Equal(["a.test", "b.test"], l[2].Hosts);
        Assert.Equal(" note", l[2].Comment);
        Assert.NotNull(l[3].Problem);
    }

    [Fact]
    public void ParseOfEmptyTextHasNoLines() => Assert.Empty(HostsFile.Parse(""));

    [Theory]
    [InlineData("127.0.0.1 localhost")]
    [InlineData("0.0.0.0\tads.example.com")]
    [InlineData("::1 localhost")]
    [InlineData("fe80::1%12 router_1.lan")]
    [InlineData("10.0.0.1 a-b.c-d.example")]
    public void AcceptsGoodEntries(string line) => Assert.Empty(HostsFile.Validate(line));

    [Theory]
    [InlineData("1 localhost")]
    [InlineData("1.2.3 localhost")]
    [InlineData("1.2.3.4.5 localhost")]
    [InlineData("256.1.1.1 localhost")]
    [InlineData("1.2.3.x localhost")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.3.4 -bad.com")]
    [InlineData("1.2.3.4 bad-.com")]
    [InlineData("1.2.3.4 a..com")]
    [InlineData("1.2.3.4 .com")]
    [InlineData("1.2.3.4 bad$name")]
    [InlineData("::1:::2 localhost")]
    [InlineData("fe80::1% localhost")]
    [InlineData("1.2.3.4 ok.com bad_host!")]
    public void RejectsBadEntries(string line) => Assert.Single(HostsFile.Validate(line));

    [Fact]
    public void EnforcesNameLengths()
    {
        Assert.Empty(HostsFile.Validate("1.2.3.4 " + new string('a', 63) + ".com"));
        Assert.Single(HostsFile.Validate("1.2.3.4 " + new string('a', 64) + ".com"));
        string longName = string.Join(".", Enumerable.Repeat(new string('a', 50), 5)); // 254 characters
        Assert.Single(HostsFile.Validate("1.2.3.4 " + longName));
        Assert.Empty(HostsFile.Validate("1.2.3.4 " + longName[..253]));
    }

    [Fact]
    public void RejectsNonAsciiAndNulAnywhere()
    {
        var p = HostsFile.Validate("# ok\r\n# café\r\n1.2.3.4 bücher.de\r\n1.2.3.4 a\0.com");
        Assert.Equal([2, 3, 4], p.Select(x => x.Line));
    }

    [Fact]
    public void ReportsLineNumbersAndEnglishMessage()
    {
        var p = Assert.Single(HostsFile.Validate("# x\r\n\r\nnope 1.2.3.4"));
        Assert.Equal(3, p.Line);
        Assert.Contains("address", p.Message);
    }

    [Fact]
    public void RoundTripKeepsCommentsAndBlankLines()
    {
        const string text = "# header\r\n\r\n127.0.0.1 localhost # me\r\n\r\n# end\r\n";
        HostsFile.Save(text, Hosts, Backups);
        Assert.Equal(text, HostsFile.Read(Hosts));
        Assert.Equal(text, string.Concat(HostsFile.Parse(text).Select(l => l.Raw + "\r\n")));
    }

    [Fact]
    public void WritesCrlfWithoutBom()
    {
        HostsFile.Save("# a\n127.0.0.1 x.test\n", Hosts, Backups);
        var bytes = File.ReadAllBytes(Hosts);
        Assert.Equal((byte)'#', bytes[0]);
        Assert.Equal("# a\r\n127.0.0.1 x.test\r\n", System.Text.Encoding.ASCII.GetString(bytes));
        Assert.False(File.Exists(Hosts + ".kos.tmp"));
    }

    [Fact]
    public void ReadDropsBomAndMissingFileIsEmpty()
    {
        Assert.Equal("", HostsFile.Read(Hosts));
        File.WriteAllBytes(Hosts, [0xEF, 0xBB, 0xBF, (byte)'#', (byte)'x']);
        Assert.Equal("#x", HostsFile.Read(Hosts));
    }

    [Fact]
    public void InvalidTextIsRefusedAndFileUntouched()
    {
        File.WriteAllText(Hosts, "# old");
        var ex = Assert.Throws<InvalidOperationException>(() => HostsFile.Save("1.2.3 bad", Hosts, Backups));
        Assert.Contains("Line 1", ex.Message);
        Assert.Equal("# old", File.ReadAllText(Hosts));
        Assert.False(Directory.Exists(Backups));
    }

    [Fact]
    public void FirstSaveHasNoBackupThenEachSaveMakesOne()
    {
        Assert.Null(HostsFile.Save("# one", Hosts, Backups));
        var backup = HostsFile.Save("# two", Hosts, Backups);
        Assert.NotNull(backup);
        Assert.Matches(@"^hosts-\d{8}-\d{6}\.bak$", Path.GetFileName(backup));
        Assert.Equal("# one", File.ReadAllText(backup));
    }

    [Fact]
    public void KeepsOnlyTheNewestTenBackups()
    {
        Directory.CreateDirectory(Backups);
        for (int i = 1; i <= 12; i++) File.WriteAllText(Path.Combine(Backups, $"hosts-2020010{i % 10}-0000{i:00}.bak"), "old");
        File.WriteAllText(Path.Combine(Backups, "other.txt"), "keep");
        File.WriteAllText(Hosts, "# cur");
        var made = HostsFile.Save("# new", Hosts, Backups)!;
        var left = Directory.GetFiles(Backups, "hosts-*.bak");
        Assert.Equal(10, left.Length);
        Assert.Contains(made, left);
        Assert.True(File.Exists(Path.Combine(Backups, "other.txt")));
    }

    [Theory]
    [InlineData("hosts_1.tmp")]
    [InlineData("hosts_gdid.tmp")]
    public void RefusesWhileASwapFileExists(string tmp)
    {
        File.WriteAllText(Hosts, "# old");
        File.WriteAllText(Path.Combine(dir, tmp), "");
        var ex = Assert.Throws<InvalidOperationException>(() => HostsFile.Save("# new", Hosts, Backups));
        Assert.Contains(tmp, ex.Message);
        Assert.Equal("# old", File.ReadAllText(Hosts));
    }

    [Fact]
    public void ReadOnlyAttributeIsRestored()
    {
        File.WriteAllText(Hosts, "# old");
        File.SetAttributes(Hosts, FileAttributes.ReadOnly);
        HostsFile.Save("# new", Hosts, Backups);
        Assert.True(File.GetAttributes(Hosts).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal("# new", HostsFile.Read(Hosts));
    }

    [Fact]
    public void DefaultPathIsInDriversEtc() =>
        Assert.EndsWith(Path.Combine("drivers", "etc", "hosts"), HostsFile.DefaultPath);
}
