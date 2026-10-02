using Tamp;
using Tamp.NetCli.V10;
using Tamp.Components;
using Tamp.Components.NetCli.V10;
using Tamp.SonarScanner.V10;
using Tamp.Telegram;

// Restore / Compile / Test / Pack come from Tamp.Components (IDotNetTest + IDotNetPack); the build
// supplies the IHaz* values and keeps its own Info / Clean / Push / Ci. See ADR 0020.
class Build : TampBuild, IDotNetTest, IDotNetPack
{
    public static int Main(string[] args) => Execute<Build>(args);

    // TAM-227 — Telegram failure notify. Pulls TELEGRAM_BOT_TOKEN /
    // TELEGRAM_CHAT_ID / TELEGRAM_BUILD_LABEL from the environment;
    // returns null when missing, framework silently skips null reporters.
    [BuildReporter] readonly IBuildReporter? TelegramNotify =
        TelegramBuildReporter.FromEnvironment();

    // Settable property: [Parameter] binds into the setter; the getter satisfies IHazConfiguration.
    [Parameter("Build configuration")]
    public Configuration Configuration { get; set; } = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    // Settable property: [Solution] injects into the setter; the getter satisfies IHazSolution.
    [Solution] public Solution Solution { get; set; } = null!;

    [GitRepository] readonly GitRepository Git = null!;

    [Secret("NuGet API key", EnvironmentVariable = "NUGET_API_KEY")]
    readonly Secret NuGetApiKey = null!;

    // Satisfies IHazArtifacts. (The component Pack reads PACKAGE_VERSION itself, so no Version param here.)
    public AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

    // The component Test (IDotNetTest) writes coverage under artifacts/test-results; build/coverlet.runsettings
    // makes it OpenCover, which SonarCloud's sonar.cs.opencover.reportsPaths ingests.
    AbsolutePath TestResultsDir => ArtifactsDirectory / "test-results";

    // ----- SonarCloud (SonarQube Cloud) -----
    //
    // dotnet-sonarscanner is a DLL-based .NET tool; CI installs it globally
    // (`dotnet tool install --global dotnet-sonarscanner`) and resolves the apphost from PATH.
    // Optional so the fast CI lane (which never runs Sonar) doesn't require it to be installed.
    [FromPath("dotnet-sonarscanner", Optional = true)]
    readonly Tool SonarTool = null!;

    [Secret("SonarCloud token", EnvironmentVariable = "SONAR_TOKEN")]
    readonly Secret SonarToken = null!;

    [Parameter("Sonar host URL", EnvironmentVariable = "SONAR_HOST_URL")]
    readonly string SonarHostUrl = "https://sonarcloud.io";

    [Parameter("SonarCloud organization")]
    readonly string SonarOrganization = "tamp-build";

    [Parameter("SonarCloud project key")]
    readonly string SonarProjectKey = "tamp-build_tamp-conformance";

    // PR-decoration inputs (set by ci.yml on pull_request events; empty on branch runs → branch analysis).
    [Parameter("Pull-request number", EnvironmentVariable = "SONAR_PR_KEY")] readonly string SonarPrKey = "";
    [Parameter("Pull-request head branch", EnvironmentVariable = "SONAR_PR_BRANCH")] readonly string SonarPrBranch = "";
    [Parameter("Pull-request base branch", EnvironmentVariable = "SONAR_PR_BASE")] readonly string SonarPrBase = "";

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
        .Executes(() => ArtifactsDirectory.GlobFiles("*.nupkg")
            .Select(p => DotNet.NuGetPush(s => s
                .SetPackagePath(p)
                .SetSource("https://api.nuget.org/v3/index.json")
                .SetApiKey(NuGetApiKey)
                .SetSkipDuplicate(true))));

    // Component Pack depends on Compile, NOT Test (they are parallel-safe siblings), so Ci must name both.
    Target Ci => _ => _
        .DependsOn(nameof(Info), nameof(Clean), nameof(ITest.Test), nameof(IPack.Pack));

    // SonarCloud analysis is a two-phase scan: Begin before the build, End after tests, with the
    // component Compile + Test running between so the scanner collects MSBuild inputs and coverage.
    Target SonarBegin => _ => _
        .Description("Initialize the SonarCloud pre-build phase.")
        .Before(nameof(ICompile.Compile))
        .Requires(() => SonarToken != null)
        .Executes(() => SonarScanner.Begin(SonarTool, s =>
        {
            s.SetProjectKey(SonarProjectKey)
             .SetOrganization(SonarOrganization)
             .SetHostUrl(SonarHostUrl)
             .SetToken(SonarToken)
             // The build script is build tooling, not shipped product code.
             .SetProperty("sonar.exclusions", "build/**")
             .SetProperty("sonar.cs.opencover.reportsPaths", $"{TestResultsDir.Value}/**/coverage.opencover.xml");

            // On a PR run, analyze as a pull request so SonarCloud decorates the PR with the
            // new-code quality gate (instead of polluting the main branch). Branch runs omit these.
            if (!string.IsNullOrEmpty(SonarPrKey))
                s.SetProperty("sonar.pullrequest.key", SonarPrKey)
                 .SetProperty("sonar.pullrequest.branch", SonarPrBranch)
                 .SetProperty("sonar.pullrequest.base", SonarPrBase);
        }));

    Target SonarEnd => _ => _
        .After(nameof(ITest.Test))
        .DependsOn(nameof(SonarBegin))
        .Description("Finalize SonarCloud and submit results.")
        .Executes(() => SonarScanner.End(SonarTool, s => s.SetToken(SonarToken)));

    Target Sonar => _ => _
        .DependsOn(nameof(SonarBegin), nameof(ITest.Test), nameof(SonarEnd))
        .Description("Full SonarCloud analysis: begin, build + test coverage (all TFMs), end. Requires SONAR_TOKEN.");

    Target Default => _ => _.DependsOn(nameof(ICompile.Compile));
}
