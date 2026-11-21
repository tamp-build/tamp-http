using Tamp;
using Tamp.NetCli.V10;
using Tamp.Components;
using Tamp.Components.NetCli.V10;

class Build : TampBuild, IDotNetTest, IDotNetPack
{
    public static int Main(string[] args) => Execute<Build>(args);

    [Parameter("Build configuration")]
    public Configuration Configuration { get; set; } = IsLocalBuild ? Configuration.Debug : Configuration.Release;


    [Solution] public Solution Solution { get; set; } = null!;
    [GitRepository] readonly GitRepository Git = null!;

    [Secret("NuGet API key", EnvironmentVariable = "NUGET_API_KEY")]
    readonly Secret NuGetApiKey = null!;

    [NuGetPackage("dotnet-sonarscanner", Version = "10.4.1")]
    readonly Tool SonarTool = null!;

    [Secret("SonarQube token", EnvironmentVariable = "SONAR_TOKEN")]
    readonly Secret SonarToken = null!;

    [Parameter("Sonar host URL", EnvironmentVariable = "SONAR_HOST_URL")]
    readonly string SonarHostUrl = "https://sonarcloud.io";

    [Parameter("Sonar project key")]
    readonly string SonarProjectKey = "tamp-build_tamp-http";

    // SonarCloud requires an organization; the self-hosted instance this repo
    // used to point at does not have the concept. Omitting it makes the scanner
    // fail pre-processing with "Authentication with the server has failed", which
    // reads like a bad token and is not one.
    [Parameter("SonarCloud organization")]
    readonly string SonarOrganization = "tamp-build";

    [Parameter("Pull-request number", EnvironmentVariable = "SONAR_PR_KEY")] readonly string SonarPrKey = "";
    [Parameter("Pull-request head branch", EnvironmentVariable = "SONAR_PR_BRANCH")] readonly string SonarPrBranch = "";
    [Parameter("Pull-request base branch", EnvironmentVariable = "SONAR_PR_BASE")] readonly string SonarPrBase = "";

    AbsolutePath Artifacts => RootDirectory / "artifacts";

    public AbsolutePath ArtifactsDirectory => Artifacts;

    Target Info => _ => _.Executes(() =>
    {
        Console.WriteLine($"  Branch:        {Git.Branch ?? "<detached>"}");
        Console.WriteLine($"  Commit:        {Git.Commit[..7]}");
        Console.WriteLine($"  Configuration: {Configuration}");
    });

    Target Clean => _ => _
        .Description("Delete bin/obj and the artifacts directory.")
        .Executes(() => CleanArtifacts());

    Target Push => _ => _
        .DependsOn(nameof(IPack.Pack))
        .Requires(() => NuGetApiKey != null)
        .Executes(() => Artifacts.GlobFiles("*.nupkg")
            .Select(p => DotNet.NuGetPush(s => s
                .SetPackagePath(p)
                .SetSource("https://api.nuget.org/v3/index.json")
                .SetApiKey(NuGetApiKey)
                .SetSkipDuplicate(true))));

    Target Ci => _ => _
        .DependsOn(nameof(Info), nameof(Clean), nameof(ITest.Test), nameof(IPack.Pack));

    Target Default => _ => _.DependsOn(nameof(ICompile.Compile));

    Target SonarBegin => _ => _
        .Before(nameof(ICompile.Compile))
        .Requires(() => SonarToken != null)
        .Executes(() => Tamp.SonarScanner.V10.SonarScanner.Begin(SonarTool, s =>
        {
            s.SetProjectKey(SonarProjectKey);
            s.SetOrganization(SonarOrganization);
            s.SetHostUrl(SonarHostUrl);
            s.SetToken(SonarToken);
            s.SetProperty("sonar.cs.vstest.reportsPaths", $"{(Artifacts / "test-results").Value}/**/*.trx");
            s.SetProperty("sonar.cs.opencover.reportsPaths", $"{(Artifacts / "test-results").Value}/**/coverage.opencover.xml");
            s.SetProperty("sonar.exclusions", "**/bin/**,**/obj/**,artifacts/**,build/**,docs/**,samples/**");
            s.SetProperty("sonar.coverage.exclusions", "tests/**,build/**");

            // On a PR run, analyse as a pull request so SonarCloud decorates it with
            // the new-code gate. Empty on a nightly main-branch baseline.
            if (!string.IsNullOrEmpty(SonarPrKey))
            {
                s.SetProperty("sonar.pullrequest.key", SonarPrKey);
                s.SetProperty("sonar.pullrequest.branch", SonarPrBranch);
                s.SetProperty("sonar.pullrequest.base", SonarPrBase);
            }
        }));

    Target SonarEnd => _ => _
        .DependsOn(nameof(ITest.Test))
        .Requires(() => SonarToken != null)
        .Executes(() => Tamp.SonarScanner.V10.SonarScanner.End(SonarTool, s => s.SetToken(SonarToken)));

    Target Sonar => _ => _
        .DependsOn(nameof(SonarBegin), nameof(SonarEnd));
}
