﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.MSBuild;
using Nuke.Common.Utilities.Collections;
using Serilog;
using static AppVersionTask;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static RestoreTask;
using Project = Nuke.Common.ProjectModel.Project;

#pragma warning disable IDE1006 // Naming Styles
class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main() => Execute<Build>(x => x.PublishApp);

    [Parameter("Configuration to build - Default is 'Release'")]
    readonly Configuration Configuration = Configuration.Release;

    [Parameter("The target platform")]
    readonly PlatformTarget[]? PlatformTargets;

    [Parameter("Runs unit tests")]
    readonly bool RunTests;

    [Solution]
    readonly Solution? WindowsSolution;

    [Solution]
    readonly Solution? MacSolution;

    [Solution]
    readonly Solution? LinuxSolution;

    Target PreliminaryCheck => _ => _
        .Before(Clean)
        .Executes(() =>
        {
            if (PlatformTargets == null || PlatformTargets.Length == 0)
            {
                Assert.Fail("Parameter `--platform-targets` is missing. Please check `build.sh --help`.");
                return;
            }

            if (PlatformTargets.Contains(PlatformTarget.Windows) && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 0, 0))
            {
                Assert.Fail("To build Windows app, you need to run on Windows 10 or later.");
                return;
            }

            if (PlatformTargets.Contains(PlatformTarget.MacOS) && !OperatingSystem.IsMacOS())
            {
                Assert.Fail("To build macOS app, you need to run on macOS Ventura 13.1 or later.");
                return;
            }

            if (PlatformTargets.Contains(PlatformTarget.Linux) && !OperatingSystem.IsLinux())
            {
                Assert.Fail("To build Linux app, you need to run on Linux.");
                return;
            }

            Log.Information("Preliminary checks are successful.");
        });

    Target Clean => _ => _
        .DependsOn(PreliminaryCheck)
        .Executes(() =>
        {
            if (!Debugger.IsAttached)
            {
                RootDirectory.GlobDirectories("bin", "obj", "packages", "publish").ForEach(path => path.CreateOrCleanDirectory());
            }
        });

    Target Restore => _ => _
        .DependsOn(Clean)
        .Executes(async () =>
        {
            if (!Debugger.IsAttached)
            {
                await RestoreDependenciesAsync(RootDirectory);
            }
        });

    Target SetVersion => _ => _
        .DependentFor(GenerateSdkNuGet)
        .After(Restore)
        .OnlyWhenDynamic(() => Configuration == Configuration.Release)
        .Executes(() =>
        {
            SetAppVersion(RootDirectory);
        });

    Target BuildGenerators => _ => _
        .DependentFor(GenerateSdkNuGet)
        .After(Restore)
        .After(SetVersion)
        .Executes(() =>
        {
            Log.Information($"Building generators ...");
            RootDirectory
                .GlobFiles("**/generators/*.csproj")
                .ForEach(f =>
                    DotNetBuild(s => s
                        .SetProjectFile(f)
                        .SetConfiguration(Configuration)
                        .SetSelfContained(true)
                        .SetPublishTrimmed(false)
                        .SetVerbosity(DotNetVerbosity.quiet)));
        });

    Target BuildPlugins => _ => _
        .DependentFor(GenerateSdkNuGet)
        .After(Restore)
        .After(SetVersion)
        .After(BuildGenerators)
        .Executes(() =>
        {
            Log.Information($"Building plugins ...");
            Project project = WindowsSolution!.GetAllProjects("DevToys.Tools").Single();
            DotNetBuild(s => s
                .SetProjectFile(project)
                .SetConfiguration(Configuration)
                .SetSelfContained(false)
                .SetPublishTrimmed(false)
                .SetVerbosity(DotNetVerbosity.quiet));
        });

#pragma warning disable IDE0051 // Remove unused private members
    Target UnitTests => _ => _
        .DependentFor(GenerateSdkNuGet)
        .After(Restore)
        .After(SetVersion)
        .After(BuildGenerators)
        .After(BuildPlugins)
        .OnlyWhenDynamic(() => RunTests)
        .Executes(() =>
        {
            RootDirectory
                .GlobFiles("**/*Tests.csproj")
                .ForEach(f =>
                    DotNetTest(s => s
                    .SetProjectFile(f)
                    .SetConfiguration(Configuration)
                    .SetVerbosity(DotNetVerbosity.quiet)));
        });
#pragma warning restore IDE0051 // Remove unused private members

    Target GenerateSdkNuGet => _ => _
        .Description("Generate the DevToys SDK NuGet package")
        .DependsOn(SetVersion)
        .DependsOn(Restore)
        .DependsOn(BuildGenerators)
        .Executes(() =>
        {
            Log.Information($"Building NuGet packages ...");
            Project project = WindowsSolution!.GetAllProjects("DevToys.Api").Single();
            DotNetPack(s => s
                .SetProject(project)
                .SetConfiguration(Configuration)
                .SetPublishTrimmed(false)
                .SetVerbosity(DotNetVerbosity.quiet)
                .SetProcessArgumentConfigurator(_ => _
                    .Add($"/bl:\"{RootDirectory / "publish" / "Sdk"}.binlog\""))
                .SetOutputDirectory(RootDirectory / "publish" / "Sdk"));
        });

    Target PublishApp => _ => _
        .DependsOn(GenerateSdkNuGet)
        .Executes(() =>
        {
            if (PlatformTargets!.Contains(PlatformTarget.Windows))
            {
                PublishWindowsApp();
            }

            if (PlatformTargets!.Contains(PlatformTarget.MacOS))
            {
                PublishMacApp();
            }

            if (PlatformTargets!.Contains(PlatformTarget.Linux))
            {
                PublishLinuxApp();
            }

            if (PlatformTargets!.Contains(PlatformTarget.CLI))
            {
                PublishCliApp();
            }
        });

    void PublishWindowsApp()
    {
        // DevToys WPF
        foreach (DotnetParameters dotnetParameters in GetDotnetParametersForWindowsApp())
        {
            Log.Information($"Publishing {dotnetParameters.ProjectOrSolutionPath + " - " + dotnetParameters.TargetFramework + " - " + dotnetParameters.RuntimeIdentifier + (dotnetParameters.Portable ? "-Portable" : string.Empty)} ...");
            DotNetPublish(s => s
                .SetProject(dotnetParameters.ProjectOrSolutionPath)
                .SetConfiguration(Configuration)
                .SetFramework(dotnetParameters.TargetFramework)
                .SetRuntime(dotnetParameters.RuntimeIdentifier)
                .SetPlatform(dotnetParameters.Platform)
                .SetSelfContained(dotnetParameters.Portable)
                .SetPublishSingleFile(false)
                .SetPublishReadyToRun(false)
                .SetPublishTrimmed(false)
                .SetVerbosity(DotNetVerbosity.quiet)
                .SetProcessArgumentConfigurator(_ => _
                    .Add("/p:RuntimeIdentifierOverride=" + dotnetParameters.RuntimeIdentifier)
                    .Add("/p:Unpackaged=" + dotnetParameters.Portable)
                    .Add($"/bl:\"{RootDirectory / "publish" / dotnetParameters.OutputPath}.binlog\""))
                .SetOutput(RootDirectory / "publish" / dotnetParameters.OutputPath));
        }
    }

    void PublishMacApp()
    {
        // DevToys MAUI Blazor Mac Catalyst
        foreach (DotnetParameters dotnetParameters in GetDotnetParametersForMacOSApp())
        {
            Log.Information($"Publishing {dotnetParameters.ProjectOrSolutionPath + " - " + dotnetParameters.TargetFramework + " - " + dotnetParameters.RuntimeIdentifier} ...");
            DotNetBuild(s => s
                .SetProjectFile(dotnetParameters.ProjectOrSolutionPath)
                .SetConfiguration(Configuration)
                .SetSelfContained(true)
                .SetPublishSingleFile(false) // Not supported by MacCatalyst as it would require UseAppHost to be true, which isn't supported on Mac
                .SetPublishReadyToRun(false)
                .SetPublishTrimmed(true) // Should be true, even though the CSPROJ disable AOT and Trimming.
                .SetVerbosity(DotNetVerbosity.quiet)
                .SetNoRestore(true) /* workaround for https://github.com/xamarin/xamarin-macios/issues/15664#issuecomment-1233123515 */
                .SetProcessArgumentConfigurator(_ => _
                    .Add("/p:RuntimeIdentifierOverride=" + dotnetParameters.RuntimeIdentifier)
                    .Add("/p:CreatePackage=True") /* Will create an installable .pkg */
                    .Add($"/bl:\"{RootDirectory / "publish" / dotnetParameters.OutputPath}.binlog\""))
                .SetOutputDirectory(RootDirectory / "publish" / dotnetParameters.OutputPath));
        }
    }

    void PublishLinuxApp()
    {
        // DevToys Linux
        foreach (DotnetParameters dotnetParameters in GetDotnetParametersForLinuxApp())
        {
            Log.Information($"Publishing {dotnetParameters.ProjectOrSolutionPath + " - " + dotnetParameters.TargetFramework + " - " + dotnetParameters.RuntimeIdentifier + (dotnetParameters.Portable ? "-Portable" : string.Empty)} ...");
            DotNetPublish(s => s
                .SetProject(dotnetParameters.ProjectOrSolutionPath)
                .SetConfiguration(Configuration)
                .SetFramework(dotnetParameters.TargetFramework)
                .SetRuntime(dotnetParameters.RuntimeIdentifier)
                .SetPlatform(dotnetParameters.Platform)
                .SetSelfContained(dotnetParameters.Portable)
                .SetPublishSingleFile(false)
                .SetPublishReadyToRun(false)
                .SetPublishTrimmed(false)
                .SetVerbosity(DotNetVerbosity.quiet)
                .SetProcessArgumentConfigurator(_ => _
                    .Add("/p:RuntimeIdentifierOverride=" + dotnetParameters.RuntimeIdentifier)
                    .Add("/p:Unpackaged=" + dotnetParameters.Portable)
                    .Add($"/bl:\"{RootDirectory / "publish" / dotnetParameters.OutputPath}.binlog\""))
                .SetOutput(RootDirectory / "publish" / dotnetParameters.OutputPath));
        }
    }

    void PublishCliApp()
    {
        // DevToys CLI
        foreach (DotnetParameters dotnetParameters in GetDotnetParametersForCliApp())
        {
            Log.Information($"Publishing {dotnetParameters.ProjectOrSolutionPath + " - " + dotnetParameters.TargetFramework + " - " + dotnetParameters.RuntimeIdentifier} ...");
            DotNetPublish(s => s
                .SetProject(dotnetParameters.ProjectOrSolutionPath)
                .SetConfiguration(Configuration)
                .SetFramework(dotnetParameters.TargetFramework)
                .SetRuntime(dotnetParameters.RuntimeIdentifier)
                .SetSelfContained(dotnetParameters.Portable)
                .SetPublishSingleFile(dotnetParameters.Portable)
                .SetPublishReadyToRun(false)
                .SetPublishTrimmed(false)
                .SetVerbosity(DotNetVerbosity.quiet)
                .SetProcessArgumentConfigurator(_ => _
                    .Add($"/bl:\"{RootDirectory / "publish" / dotnetParameters.OutputPath}.binlog\""))
                .SetOutput(RootDirectory / "publish" / dotnetParameters.OutputPath));
        }
    }

    IEnumerable<DotnetParameters> GetDotnetParametersForCliApp()
    {
        string publishProject = "DevToys.CLI";
        Project project;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            project = MacSolution!.GetProject(publishProject);
            foreach (string targetFramework in project.GetTargetFrameworks())
            {
                yield return new DotnetParameters(project.Path, "osx-x64", targetFramework, portable: false);
                yield return new DotnetParameters(project.Path, "osx-x64", targetFramework, portable: true);

                yield return new DotnetParameters(project.Path, "osx-arm64", targetFramework, portable: false);
                yield return new DotnetParameters(project.Path, "osx-arm64", targetFramework, portable: true);
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            project = WindowsSolution!.GetAllProjects(publishProject).Single();
            foreach (string targetFramework in project.GetTargetFrameworks())
            {
                yield return new DotnetParameters(project.Path, "win-x64", targetFramework, portable: false);
                yield return new DotnetParameters(project.Path, "win-x64", targetFramework, portable: true);

                yield return new DotnetParameters(project.Path, "win-arm64", targetFramework, portable: false);
                yield return new DotnetParameters(project.Path, "win-arm64", targetFramework, portable: true);

                yield return new DotnetParameters(project.Path, "win-x86", targetFramework, portable: false);
                yield return new DotnetParameters(project.Path, "win-x86", targetFramework, portable: true);
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            project = LinuxSolution!.GetAllProjects(publishProject).Single();
            foreach (string targetFramework in project.GetTargetFrameworks())
            {
                yield return new DotnetParameters(project.Path, "linux-arm", targetFramework, portable: false, platform: "arm");
                yield return new DotnetParameters(project.Path, "linux-x64", targetFramework, portable: false, platform: "x64");

                yield return new DotnetParameters(project.Path, "linux-arm", targetFramework, portable: true, platform: "arm");
                yield return new DotnetParameters(project.Path, "linux-x64", targetFramework, portable: true, platform: "x64");
            }
        }
    }

    IEnumerable<DotnetParameters> GetDotnetParametersForWindowsApp()
    {
        string publishProject = "DevToys.Windows";
        Project project;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            project = WindowsSolution!.GetAllProjects(publishProject).Single();
            foreach (string targetFramework in project.GetTargetFrameworks())
            {
                yield return new DotnetParameters(project.Path, "win-arm64", targetFramework, portable: false, platform: "arm64");
                yield return new DotnetParameters(project.Path, "win-x64", targetFramework, portable: false, platform: "x64");
                yield return new DotnetParameters(project.Path, "win-x86", targetFramework, portable: false, platform: "x86");

                yield return new DotnetParameters(project.Path, "win-arm64", targetFramework, portable: true, platform: "arm64");
                yield return new DotnetParameters(project.Path, "win-x64", targetFramework, portable: true, platform: "x64");
                yield return new DotnetParameters(project.Path, "win-x86", targetFramework, portable: true, platform: "x86");
            }
        }
        else
        {
            throw new NotSupportedException();
        }
    }

    IEnumerable<DotnetParameters> GetDotnetParametersForLinuxApp()
    {
        string publishProject = "DevToys.Linux";
        Project project;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            project = LinuxSolution!.GetAllProjects(publishProject).Single();
            foreach (string targetFramework in project.GetTargetFrameworks())
            {
                yield return new DotnetParameters(project.Path, "linux-arm", targetFramework, portable: false, platform: "arm");
                yield return new DotnetParameters(project.Path, "linux-x64", targetFramework, portable: false, platform: "x64");

                yield return new DotnetParameters(project.Path, "linux-arm", targetFramework, portable: true, platform: "arm");
                yield return new DotnetParameters(project.Path, "linux-x64", targetFramework, portable: true, platform: "x64");
            }
        }
        else
        {
            throw new NotSupportedException();
        }
    }

    IEnumerable<DotnetParameters> GetDotnetParametersForMacOSApp()
    {
        string publishProject = "DevToys.MacOS";
        Project project;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            project = MacSolution!.GetProject(publishProject);
            foreach (string targetFramework in project.GetTargetFrameworks())
            {
                yield return new DotnetParameters(project.Path, "maccatalyst-arm64", targetFramework, portable: true);
                yield return new DotnetParameters(project.Path, "maccatalyst-x64", targetFramework, portable: true);
            }
        }
        else
        {
            throw new NotSupportedException();
        }
    }
#pragma warning restore IDE1006 // Naming Styles
}
