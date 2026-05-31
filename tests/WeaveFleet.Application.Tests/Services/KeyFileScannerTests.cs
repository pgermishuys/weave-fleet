using Shouldly;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Application.Tests.Services;

public sealed class KeyFileScannerTests
{
    private static KeyFileScanner CreateScanner() =>
        new(KeyFileConfig.Load());

    [Fact]
    public void build_result_returns_empty_when_no_files()
    {
        var scanner = CreateScanner();
        var result = scanner.BuildResult([]);

        result.FilesByToolId.ShouldBeEmpty();
    }

    [Fact]
    public void build_result_finds_dotnet_solution_for_rider_and_visual_studio()
    {
        var scanner = CreateScanner();
        var result = scanner.BuildResult(["WeaveFleet.slnx"]);

        result.FilesByToolId.ContainsKey("rider").ShouldBeTrue();
        result.FilesByToolId.ContainsKey("visual-studio").ShouldBeTrue();
        result.FilesByToolId["rider"].ShouldContain("WeaveFleet.slnx");
        result.FilesByToolId["visual-studio"].ShouldContain("WeaveFleet.slnx");
    }

    [Fact]
    public void build_result_solution_trumps_project_files()
    {
        var scanner = CreateScanner();
        var result = scanner.BuildResult(["WeaveFleet.slnx", "src/MyLib/MyLib.csproj"]);

        result.FilesByToolId["rider"].ShouldNotContain("src/MyLib/MyLib.csproj");
        result.FilesByToolId["visual-studio"].ShouldNotContain("src/MyLib/MyLib.csproj");
        result.FilesByToolId["rider"].ShouldContain("WeaveFleet.slnx");
    }

    [Fact]
    public void build_result_shows_project_files_when_no_solution()
    {
        var scanner = CreateScanner();
        var result = scanner.BuildResult(["src/MyLib/MyLib.csproj", "src/MyApp/MyApp.fsproj"]);

        result.FilesByToolId.ContainsKey("rider").ShouldBeTrue();
        result.FilesByToolId["rider"].ShouldContain("src/MyLib/MyLib.csproj");
        result.FilesByToolId["rider"].ShouldContain("src/MyApp/MyApp.fsproj");
    }

    [Fact]
    public void build_result_solution_also_trumps_vcxproj()
    {
        var scanner = CreateScanner();
        var result = scanner.BuildResult(["WeaveFleet.sln", "native/Native.vcxproj"]);

        result.FilesByToolId["visual-studio"].ShouldNotContain("native/Native.vcxproj");
        result.FilesByToolId["visual-studio"].ShouldContain("WeaveFleet.sln");
    }

    [Fact]
    public void build_result_vcxproj_shown_when_no_solution()
    {
        var scanner = CreateScanner();
        var result = scanner.BuildResult(["native/Native.vcxproj"]);

        result.FilesByToolId.ContainsKey("visual-studio").ShouldBeTrue();
        result.FilesByToolId["visual-studio"].ShouldContain("native/Native.vcxproj");
    }

    [Fact]
    public void build_result_sorts_root_files_before_nested()
    {
        var scanner = CreateScanner();
        var result = scanner.BuildResult([
            "src/Services/ServiceA/ServiceA.slnx",
            "WeaveFleet.slnx",
            "src/ServiceB.slnx",
        ]);

        var files = result.FilesByToolId["rider"];
        files[0].ShouldBe("WeaveFleet.slnx");
        files[1].ShouldBe("src/ServiceB.slnx");
        files[2].ShouldBe("src/Services/ServiceA/ServiceA.slnx");
    }

    [Fact]
    public void build_result_sorts_alphabetically_within_same_depth()
    {
        var scanner = CreateScanner();
        var result = scanner.BuildResult([
            "src/Zebra.slnx",
            "src/Alpha.slnx",
        ]);

        var files = result.FilesByToolId["rider"];
        files[0].ShouldBe("src/Alpha.slnx");
        files[1].ShouldBe("src/Zebra.slnx");
    }

    [Fact]
    public void build_result_xcode_workspace_trumps_xcodeproj()
    {
        var scanner = CreateScanner();
        var result = scanner.BuildResult(["MyApp.xcworkspace", "MyApp.xcodeproj"]);

        result.FilesByToolId.ContainsKey("xcode").ShouldBeTrue();
        result.FilesByToolId["xcode"].ShouldContain("MyApp.xcworkspace");
        result.FilesByToolId["xcode"].ShouldNotContain("MyApp.xcodeproj");
    }

    [Fact]
    public void build_result_xcodeproj_shown_when_no_workspace()
    {
        var scanner = CreateScanner();
        var result = scanner.BuildResult(["MyApp.xcodeproj"]);

        result.FilesByToolId.ContainsKey("xcode").ShouldBeTrue();
        result.FilesByToolId["xcode"].ShouldContain("MyApp.xcodeproj");
    }

    [Fact]
    public void build_result_gradle_maps_to_intellij_and_android_studio()
    {
        var scanner = CreateScanner();
        var result = scanner.BuildResult(["build.gradle"]);

        result.FilesByToolId.ContainsKey("intellij").ShouldBeTrue();
        result.FilesByToolId.ContainsKey("android-studio").ShouldBeTrue();
    }

    [Fact]
    public void build_result_pom_xml_maps_to_intellij_and_android_studio()
    {
        var scanner = CreateScanner();
        var result = scanner.BuildResult(["pom.xml"]);

        result.FilesByToolId.ContainsKey("intellij").ShouldBeTrue();
        result.FilesByToolId.ContainsKey("android-studio").ShouldBeTrue();
    }

    [Fact]
    public void build_result_cmake_maps_to_clion()
    {
        var scanner = CreateScanner();
        var result = scanner.BuildResult(["CMakeLists.txt"]);

        result.FilesByToolId.ContainsKey("clion").ShouldBeTrue();
        result.FilesByToolId["clion"].ShouldContain("CMakeLists.txt");
    }

    [Fact]
    public void build_result_ignores_unrecognised_files()
    {
        var scanner = CreateScanner();
        var result = scanner.BuildResult(["README.md", "src/main.ts", "Dockerfile"]);

        result.FilesByToolId.ShouldBeEmpty();
    }
}
