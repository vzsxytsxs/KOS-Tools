using System.Xml.Linq;
using KosToolbox.Core.Startup;
using Xunit;

namespace KosToolbox.Core.Tests;

public class DelayedStartupTests
{
    [Fact]
    public void TaskXmlIsValidAndCarriesTheDelay()
    {
        var xml = DelayedStartup.Xml(@"PC\user", @"C:\Program Files\App & Co\app.exe", "--minimized \"x\"", 60, "App <beta>");
        var doc = XDocument.Parse(xml); // escaped: &, quotes and <> don't break it
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal(@"C:\Program Files\App & Co\app.exe", doc.Descendants(ns + "Command").Single().Value);
        Assert.Equal("--minimized \"x\"", doc.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal(60, DelayedStartup.ParseDelay(xml));
        Assert.DoesNotContain("<Arguments>", DelayedStartup.Xml("u", @"C:\a.exe", "", 30, "a"));
    }

    [Fact]
    public void ReadBackCatchesAnyChange()
    {
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        var mine = DelayedStartup.Xml(sid, @"C:\a b\app.exe", "-x", 60, "a");
        Assert.True(DelayedStartup.Matches(mine, sid, @"C:\a b\app.exe", "-x", 60));
        Assert.False(DelayedStartup.Matches(mine.Replace("LeastPrivilege", "HighestAvailable"), sid, @"C:\a b\app.exe", "-x", 60), "case 1");
        Assert.False(DelayedStartup.Matches(mine.Replace(sid, "S-1-5-18"), sid, @"C:\a b\app.exe", "-x", 60), "case 2");
        Assert.False(DelayedStartup.Matches(mine.Replace("app.exe", "evil.exe"), sid, @"C:\a b\app.exe", "-x", 60), "case 3");
        Assert.False(DelayedStartup.Matches(mine, sid, @"C:\a b\app.exe", "-x", 30), "case 4");
        var all = DelayedStartup.Xml(null, @"C:\a.exe", "", 30, "a");
        Assert.Contains("<GroupId>S-1-5-32-545</GroupId>", all);
        Assert.DoesNotContain("<UserId>", all);
        Assert.True(DelayedStartup.Matches(all, null, @"C:\a.exe", "", 30));
        Assert.False(DelayedStartup.Matches(all, sid, @"C:\a.exe", "", 30), "case 5");
    }

    [Fact]
    public void DelaysInEveryFormat()
    {
        Assert.Equal(120, DelayedStartup.ParseDelay("<Delay>PT2M</Delay>"));
        Assert.Equal(90, DelayedStartup.ParseDelay("<Delay>PT1M30S</Delay>"));
        Assert.Equal(30, DelayedStartup.ParseDelay("<Task><Delay>PT30S</Delay></Task>"));
        Assert.Null(DelayedStartup.ParseDelay("<Task />"));
    }
}

public class DelayedStartupLiveTests
{
    /// <summary>schtasks accepts the definition and reads the delay back (creates and deletes a task of this account; set KOS_TEST_SCHTASKS=1).</summary>
    [Fact]
    public void SchtasksAcceptsTheDefinition()
    {
        if (Environment.GetEnvironmentVariable("KOS_TEST_SCHTASKS") != "1") return;
        var schtasks = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
        var name = DelayedStartup.Prefix + "test";
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var xml = DelayedStartup.Xml(sid, cmd, "/c exit", 90, "test & <x>");
        var tmp = Path.Combine(Path.GetTempPath(), $"kos-delay-test-{Guid.NewGuid():N}.xml");
        File.WriteAllText(tmp, xml, System.Text.Encoding.Unicode);
        try
        {
            var create = KosToolbox.Core.Execution.ProcessRunner.Run(schtasks, $"/Create /TN \"{name}\" /XML \"{tmp}\" /F");
            Assert.True(create.ExitCode == 0, create.Output);
            var query = KosToolbox.Core.Execution.ProcessRunner.Run(schtasks, $"/Query /TN \"{name}\" /XML");
            Assert.Equal(90, DelayedStartup.ParseDelay(query.Output));
            Assert.True(DelayedStartup.Matches(query.Output, sid, cmd, "/c exit", 90), query.Output);
        }
        finally
        {
            File.Delete(tmp);
            KosToolbox.Core.Execution.ProcessRunner.Run(schtasks, $"/Delete /TN \"{name}\" /F");
        }
    }
}

public class StartupHookTests
{
    [Fact]
    public void ReadsWinsockPathsAndWmiOutput()
    {
        var packed = new byte[400];
        System.Text.Encoding.ASCII.GetBytes(@"%SystemRoot%\system32\mswsock.dll").CopyTo(packed, 0);
        Assert.Equal(@"%SystemRoot%\system32\mswsock.dll", SystemHookProvider.PackedPath(packed));
        Assert.Null(SystemHookProvider.PackedPath(new byte[10]));
        var wmi = WmiSubscriptionProvider.Parse("noise\r\n[{\"Name\":\"x\",\"Class\":\"CommandLineEventConsumer\",\"Command\":\"cmd /c y\"}]");
        Assert.Equal(("x", "CommandLineEventConsumer", "cmd /c y"), wmi.Single());
        Assert.Empty(WmiSubscriptionProvider.Parse("[]"));
    }

    /// <summary>This PC: the hooks read, KOS's IFEO blocks are labelled, and Windows files get a Microsoft signer (catalog or own).</summary>
    [Fact]
    public void ReadsThisPc()
    {
        var hooks = new SystemHookProvider().Collect(true).ToList();
        Assert.Contains(hooks, e => e.Source == KosToolbox.Core.Text.Txt.SourceKnownDlls);
        Assert.True(Signatures.IsMicrosoft(Signatures.Signer(Path.Combine(Environment.SystemDirectory, "kernel32.dll"))), Signatures.Signer(Path.Combine(Environment.SystemDirectory, "kernel32.dll")) ?? "no signer");
        Assert.True(Signatures.IsMicrosoft(Signatures.Signer(Path.Combine(Environment.SystemDirectory, "notepad.exe")) ?? Signatures.Signer(Path.Combine(Environment.SystemDirectory, "cmd.exe"))));
        if (Environment.GetEnvironmentVariable("KOS_TEST_REPORT") is not null)
        {
            foreach (var g in hooks.GroupBy(e => e.Source)) Console.WriteLine($"{g.Key}: {g.Count()} · {string.Join(", ", g.Take(4).Select(e => e.Name))}");
            foreach (var e in hooks.Where(e => e.Source.StartsWith(KosToolbox.Core.Text.Txt.SourceIfeo))) Console.WriteLine($"IFEO {e.Name}: {e.StatusDetail}");
            foreach (var e in new WmiSubscriptionProvider().Collect(true)) Console.WriteLine($"WMI {e.Name}: {e.Command}");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var unsigned = hooks.Where(e => e.ImagePath is not null).Select(e => (e.Name, Signer: Signatures.Signer(e.ImagePath!))).Where(x => !Signatures.IsMicrosoft(x.Signer)).ToList();
            Console.WriteLine($"signers in {sw.ElapsedMilliseconds} ms; not Microsoft: {string.Join(", ", unsigned.Select(x => $"{x.Name}={x.Signer ?? "none"}"))}");
        }
    }
}
