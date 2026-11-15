using MediaPager.App.Core.Services;
using Xunit;

namespace MediaPager.App.Api.Tests;

public sealed class PluginProjectSelectorTests
{
    [Fact]
    public void SelectsTheOnlyNonTestProjectWhenTheRepositoryAlsoContainsTests()
    {
        using var projects = new TemporaryProjects();
        var plugin = projects.Write("MediaPager.Plugins.Sample.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        var tests = projects.Write("Harness.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>
            </Project>
            """);

        var selection = PluginProjectSelector.Select([tests, plugin], assembly: null);

        Assert.Equal([plugin], selection.Candidates);
        Assert.Equal(plugin, selection.Project);
    }

    [Fact]
    public void RecognizesTestSdkProjectsEvenWithoutTestNaming()
    {
        using var projects = new TemporaryProjects();
        var plugin = projects.Write("MediaPager.Plugins.Sample.csproj", "<Project />");
        var tests = projects.Write("ValidationHarness.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" /></ItemGroup>
            </Project>
            """);

        var selection = PluginProjectSelector.Select([plugin, tests], assembly: null);

        Assert.Equal([plugin], selection.Candidates);
        Assert.Equal(plugin, selection.Project);
    }

    [Fact]
    public void RequiresExplicitAssemblyWhenMultipleNonTestProjectsRemain()
    {
        using var projects = new TemporaryProjects();
        var first = projects.Write("MediaPager.Plugins.First.csproj", "<Project />");
        var second = projects.Write("MediaPager.Plugins.Second.csproj", "<Project />");
        var tests = projects.Write("MediaPager.Plugins.Sample.Tests.csproj", "<Project />");

        var automatic = PluginProjectSelector.Select([first, tests, second], assembly: null);
        var explicitSelection = PluginProjectSelector.Select([first, tests, second], "MediaPager.Plugins.Second");

        Assert.Equal(2, automatic.Candidates.Count);
        Assert.Null(automatic.Project);
        Assert.Equal(second, explicitSelection.Project);
    }

    [Fact]
    public void DoesNotAllowAnExplicitAssemblyToSelectATestProject()
    {
        using var projects = new TemporaryProjects();
        var tests = projects.Write("MediaPager.Plugins.Sample.Tests.csproj", "<Project />");

        var selection = PluginProjectSelector.Select([tests], "MediaPager.Plugins.Sample.Tests");

        Assert.Empty(selection.Candidates);
        Assert.Null(selection.Project);
    }

    private sealed class TemporaryProjects : IDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory("mediapager-project-selection-").FullName;

        public string Write(string fileName, string content)
        {
            var path = Path.Combine(directory, fileName);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
