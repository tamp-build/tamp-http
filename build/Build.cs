using Tamp;
using Tamp.NetCli.V10;

class Build : TampBuild
{
    public static int Main(string[] args) => Execute<Build>(args);

    [Parameter("Build configuration")]
    Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Parameter("Package version override", EnvironmentVariable = "PACKAGE_VERSION")]
#pragma warning disable CS0649
    readonly string? Version;
#pragma warning restore CS0649

    [Solution] readonly Solution Solution = null!;
    [GitRepository] readonly GitRepository Git = null!;

    [Secret("NuGet API key", EnvironmentVariable = "NUGET_API_KEY")]
    readonly Secret NuGetApiKey = null!;

    [NuGetPackage("dotnet-sonarscanner", Version = "10.4.1")]
    readonly Tool SonarTool = null!;

    [Secret("SonarQube token", EnvironmentVariable = "SONAR_TOKEN")]
    readonly Secret SonarToken = null!;

    [Parameter("Sonar host URL", EnvironmentVariable = "SONAR_HOST_URL")]
    readonly string SonarHostUrl = "https://sonar.brewingcoder.com";

    [Parameter("Sonar project key")]
    readonly string SonarProjectKey = "tamp-build_tamp-http";

    AbsolutePath Artifacts => RootDirectory / "artifacts";

    Target Info => _ => _.Executes(() =>
    {
        Console.WriteLine($"  Branch:        {Git.Branch ?? "<detached>"}");
        Console.WriteLine($"  Commit:        {Git.Commit[..7]}");
        Console.WriteLine($"  Configuration: {Configuration}");
    });

    Target Clean => _ => _
        .Executes(() =>
        {
            // Exclude the build script's own bin/obj — we're currently running from there.
            // Deleting them mid-run would self-evict the Tamp.NetCli.V10 dll the Restore
            // target needs. The Tamp.Core 1.0.8 GlobDirectories fix surfaced this trap.
            var buildDir = (RootDirectory / "build").Value;
            foreach (var d in RootDirectory.GlobDirectories("**/bin", "**/obj"))
            {
                if (d.Value.StartsWith(buildDir, StringComparison.Ordinal)) continue;
                d.Delete();
            }
            Artifacts.Delete();
        });

    Target Restore => _ => _.Executes(() => DotNet.Restore(s => s.SetProject(Solution.Path)));

    Target Compile => _ => _
        .DependsOn(nameof(Restore))
        .Executes(() => DotNet.Build(s => s
            .SetProject(Solution.Path)
            .SetConfiguration(Configuration)
            .SetNoRestore(true)));

    Target Test => _ => _
        .DependsOn(nameof(Compile))
        .Description("Unit tests — Tamp.Http has no integration tests (pure library, no external CLI/service to drive).")
        .Executes(() => DotNet.Test(s => s
            .SetProject(RootDirectory / "tests" / "Tamp.Http.Tests" / "Tamp.Http.Tests.csproj")
            .SetConfiguration(Configuration)
            .SetNoBuild(true)
            .AddLogger("trx;LogFileName=test-results.trx")
            .AddDataCollector("XPlat Code Coverage")
            .SetSettings((RootDirectory / "build" / "coverlet.runsettings").Value)
            .SetResultsDirectory(Artifacts / "test-results")));

    Target Pack => _ => _
        .DependsOn(nameof(Test))
        .Executes(() => DotNet.Pack(s =>
        {
            s.SetProject(RootDirectory / "src" / "Tamp.Http" / "Tamp.Http.csproj");
            s.SetConfiguration(Configuration);
            s.SetNoBuild(true);
            s.SetOutput(Artifacts);
            if (!string.IsNullOrEmpty(Version)) s.SetProperty("Version", Version);
        }));

    Target Push => _ => _
        .DependsOn(nameof(Pack))
        .Requires(() => NuGetApiKey != null)
        .Executes(() => Artifacts.GlobFiles("*.nupkg")
            .Select(p => DotNet.NuGetPush(s => s
                .SetPackagePath(p)
                .SetSource("https://api.nuget.org/v3/index.json")
                .SetApiKey(NuGetApiKey)
                .SetSkipDuplicate(true))));

    Target Ci => _ => _
        .DependsOn(nameof(Info), nameof(Clean), nameof(Pack));

    Target Default => _ => _.DependsOn(nameof(Compile));

    Target SonarBegin => _ => _
        .Before(nameof(Compile))
        .Requires(() => SonarToken != null)
        .Executes(() => Tamp.SonarScanner.V10.SonarScanner.Begin(SonarTool, s =>
        {
            s.SetProjectKey(SonarProjectKey);
            s.SetHostUrl(SonarHostUrl);
            s.SetToken(SonarToken);
            s.SetProperty("sonar.cs.vstest.reportsPaths", $"{(Artifacts / "test-results").Value}/**/*.trx");
            s.SetProperty("sonar.cs.opencover.reportsPaths", $"{(Artifacts / "test-results").Value}/**/coverage.opencover.xml");
            s.SetProperty("sonar.exclusions", "**/bin/**,**/obj/**,artifacts/**,build/**,docs/**,samples/**");
            s.SetProperty("sonar.coverage.exclusions", "tests/**,build/**");
        }));

    Target SonarEnd => _ => _
        .DependsOn(nameof(Test))
        .Requires(() => SonarToken != null)
        .Executes(() => Tamp.SonarScanner.V10.SonarScanner.End(SonarTool, s => s.SetToken(SonarToken)));

    Target Sonar => _ => _
        .DependsOn(nameof(SonarBegin), nameof(SonarEnd));
}
