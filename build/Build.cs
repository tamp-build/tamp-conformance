using Tamp;
using Tamp.NetCli.V10;
using Tamp.Components;
using Tamp.Components.NetCli.V10;
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

    Target Default => _ => _.DependsOn(nameof(ICompile.Compile));
}
