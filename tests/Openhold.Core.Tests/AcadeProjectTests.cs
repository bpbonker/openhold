using Openhold.Core.Files;

namespace Openhold.Core.Tests;

public sealed class AcadeProjectTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openhold-acade", Guid.NewGuid().ToString("N"));

    public AcadeProjectTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string SampleProject = """
        *[1]Pump Station Controls
        *[2]Line 2
        +[1]%SL_DIR%jic125/
        +[5]1
        ===Cover sheet
        =====SUB=SCHEMATIC
        ps01.dwg
        ===Motor control
        ps02.dwg
        panel/ps03.dwg
        """;

    [Fact]
    public void Parses_description_and_drawings_in_order()
    {
        var project = AcadeProject.Parse(Path.Combine(_dir, "pump.wdp"), SampleProject.Split('\n'));

        Assert.Equal(["Pump Station Controls", "Line 2"], project.Description);
        Assert.Equal(3, project.Drawings.Count);
        Assert.Equal(Path.Combine(_dir, "ps01.dwg"), project.Drawings[0].Path);
        Assert.Equal("Cover sheet", project.Drawings[0].Description);
        Assert.Equal("SCHEMATIC", project.Drawings[0].Subfolder);
        Assert.Equal("Motor control", project.Drawings[1].Description);
        Assert.Null(project.Drawings[1].Subfolder);
        Assert.Equal(Path.Combine(_dir, "panel", "ps03.dwg"), project.Drawings[2].Path);
    }

    [Fact]
    public void Project_set_includes_companions_and_existing_drawings()
    {
        var wdp = Path.Combine(_dir, "pump.wdp");
        File.WriteAllText(wdp, "\uFEFF" + SampleProject);
        foreach (var name in new[] { "pump.wdt", "pump_wdtitle.wdl", "pump.aepx", "ps01.dwg", "ps02.dwg", "unrelated.dwg" })
            File.WriteAllText(Path.Combine(_dir, name), "x");

        var project = AcadeProject.Load(wdp);
        var set = project.ProjectSetFiles().Select(Path.GetFileName).ToList();

        Assert.Equal(["pump.wdp", "pump.wdt", "pump_wdtitle.wdl", "pump.aepx", "ps01.dwg", "ps02.dwg"], set);
        Assert.Equal([Path.Combine(_dir, "panel", "ps03.dwg")], project.MissingDrawings());
    }

    [Fact]
    public void Scratch_files_are_ignored_and_cad_names_need_uniqueness()
    {
        Assert.True(FileKinds.IsIgnored(@"C:\Work\demo01.dwl"));
        Assert.True(FileKinds.IsIgnored(@"C:\Work\~$Datasheet.docx"));
        Assert.True(FileKinds.IsIgnored(@"C:\Work\OldVersions\part.ipt"));
        Assert.False(FileKinds.IsIgnored(@"C:\Work\demo01.dwg"));
        Assert.True(FileKinds.RequiresUniqueName("DEMO01.DWG"));
        Assert.False(FileKinds.RequiresUniqueName("Datasheet.pdf"));
        Assert.True(FileKinds.IsAcadeProject("wddemo.WDP"));
    }

    [Theory]
    [InlineData("Pump Station.project", false)]
    [InlineData("Pump Station.projectarchive", false)]
    [InlineData("Utilities.library", false)]
    [InlineData("Pump Station.project.~u", true)]
    [InlineData("Pump Station.opt", true)]
    [InlineData("Pump Station.Device.Application.compileinfo", true)]
    [InlineData("Pump Station.Device.Application.bootinfo", true)]
    [InlineData("Pump Station.Device.Application.precompilecache", true)]
    public void Codesys_build_output_and_user_files_are_never_versioned(string fileName, bool ignored)
    {
        Assert.Equal(ignored, FileKinds.IsIgnored(Path.Combine(@"C:\Openhold\Workspace\Controls", fileName)));
        Assert.False(FileKinds.RequiresUniqueName(fileName));
    }
}
