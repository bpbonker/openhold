using Openhold.Client.Sdk.Workspace;

namespace Openhold.Core.Tests;

public sealed class AcadeProjectRepairerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openhold-wdp", Guid.NewGuid().ToString("N"));

    public AcadeProjectRepairerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Renamed_drawing_line_is_rewritten_and_everything_else_kept()
    {
        var wdp = Path.Combine(_dir, "pump.wdp");
        File.WriteAllText(wdp, "﻿*[1]Pump station\r\n+[5]1\r\n===Cover\r\nps01.dwg\r\n===Motors\r\npanel/ps02.dwg\r\n");
        var repairer = new AcadeProjectRepairer();

        var changed = repairer.Repair(wdp, Path.Combine(_dir, "panel", "ps02.dwg"), Path.Combine(_dir, "panel", "PS-MOTORS.dwg"));

        Assert.True(changed);
        var bytes = File.ReadAllBytes(wdp);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]); // encoding kept
        Assert.Equal("*[1]Pump station\r\n+[5]1\r\n===Cover\r\nps01.dwg\r\n===Motors\r\npanel/PS-MOTORS.dwg\r\n",
            System.Text.Encoding.UTF8.GetString(bytes[3..]));
    }

    [Fact]
    public void A_project_saved_in_the_ansi_code_page_keeps_every_byte()
    {
        var wdp = Path.Combine(_dir, "pump.wdp");
        // "Pump 90°" in Windows-1252: 0xB0 on its own isn't valid UTF-8.
        byte[] description = [.. "*[1]Pump 90"u8, 0xB0, .. "\r\nps01.dwg\r\n"u8];
        File.WriteAllBytes(wdp, description);

        new AcadeProjectRepairer().Repair(wdp, Path.Combine(_dir, "ps01.dwg"), Path.Combine(_dir, "ps01-cover.dwg"));

        Assert.Equal([.. "*[1]Pump 90"u8, 0xB0, .. "\r\nps01-cover.dwg\r\n"u8], File.ReadAllBytes(wdp));
    }

    [Fact]
    public void Drawing_moved_to_another_folder_gets_a_relative_path()
    {
        var wdp = Path.Combine(_dir, "pump.wdp");
        File.WriteAllText(wdp, "*[1]Pump\nps01.dwg\n");

        new AcadeProjectRepairer().Repair(wdp, Path.Combine(_dir, "ps01.dwg"), Path.Combine(_dir, "Sheets", "ps01.dwg"));

        Assert.Equal("*[1]Pump\nSheets\\ps01.dwg\n", File.ReadAllText(wdp));
    }

    [Fact]
    public void Project_without_the_drawing_is_left_alone()
    {
        var wdp = Path.Combine(_dir, "pump.wdp");
        File.WriteAllText(wdp, "*[1]Pump\nps01.dwg\n");
        var before = File.ReadAllBytes(wdp);

        var changed = new AcadeProjectRepairer().Repair(wdp, Path.Combine(_dir, "other.dwg"), Path.Combine(_dir, "renamed.dwg"));

        Assert.False(changed);
        Assert.Equal(before, File.ReadAllBytes(wdp));
    }
}
