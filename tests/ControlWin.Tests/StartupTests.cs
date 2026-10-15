using System.Xml.Linq;
using ControlWin.Setup;

namespace ControlWin.Tests;

public class StartupTests
{
    [Fact]
    public void The_task_xml_is_valid_and_starts_the_exe_at_logon_with_highest_privileges()
    {
        string xml = Startup.BuildTaskXml(@"C:\Program Files\ControlWin & Co\ControlWin.exe", @"PC\Ned <o>");

        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal(@"C:\Program Files\ControlWin & Co\ControlWin.exe", (string?)doc.Descendants(ns + "Command").Single());
        Assert.Equal("HighestAvailable", (string?)doc.Descendants(ns + "RunLevel").Single());
        Assert.NotNull(doc.Descendants(ns + "LogonTrigger").SingleOrDefault());
        Assert.Equal(@"PC\Ned <o>", (string?)doc.Descendants(ns + "Principal").Single().Element(ns + "UserId"));
    }

    [Fact]
    public void The_task_is_never_stopped_by_time_or_battery()
    {
        var doc = XDocument.Parse(Startup.BuildTaskXml(@"C:\a.exe", "PC\\u"));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var settings = doc.Descendants(ns + "Settings").Single();

        Assert.Equal("PT0S", (string?)settings.Element(ns + "ExecutionTimeLimit"));
        Assert.Equal("false", (string?)settings.Element(ns + "StopIfGoingOnBatteries"));
        Assert.Equal("false", (string?)settings.Element(ns + "DisallowStartIfOnBatteries"));
    }
}
