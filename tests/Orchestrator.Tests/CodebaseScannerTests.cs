using Orchestrator.Agents;

namespace Orchestrator.Tests;

/// <summary>
/// Points the scanner at this repository and asserts it reports facts that are actually true of
/// it — the point of the scanner is that analysis is grounded in the real code rather than
/// invented. Assertions are deliberately loose (a project exists, its route list is non-empty)
/// so they survive ordinary evolution of the codebase.
/// </summary>
public class CodebaseScannerTests
{
    /// <summary>Walks up from the test binary for the *.sln, exactly as Orchestrator.Cli does.</summary>
    private static string SolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate the solution root (no .sln found above the test binary).");
    }

    private static CodebaseFacts ScanRepository(string requirement = "shorten url analytics") =>
        new CodebaseScanner(SolutionRoot()).Scan(requirement);

    [Fact]
    public void Scan_FindsTheSolutionsProjects()
    {
        var facts = ScanRepository();

        Assert.False(facts.IsEmpty);
        Assert.Equal(SolutionRoot(), facts.Root);

        var names = facts.Projects.Select(p => p.Name).ToList();
        Assert.True(names.Count >= 4, $"Expected several projects, found: {string.Join(", ", names)}");
        Assert.Contains("UrlShortener.Api", names);
        Assert.Contains("Orchestrator.Core", names);
    }

    [Fact]
    public void Scan_ReportsHttpRoutes_ForTheApiProject()
    {
        var facts = ScanRepository();

        var api = Assert.Single(facts.Projects, p => p.Name == "UrlShortener.Api");

        Assert.NotEmpty(api.HttpRoutes);
        Assert.Contains(api.HttpRoutes, r => r.StartsWith("GET", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_ReportsEntitySets_ForTheInfrastructureProject()
    {
        var facts = ScanRepository();

        var infrastructure = Assert.Single(facts.Projects, p => p.Name == "UrlShortener.Infrastructure");

        Assert.NotEmpty(infrastructure.EntitySets);
        Assert.Contains(infrastructure.EntitySets, e => e.Contains("ShortUrl", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_ReportsProjectReferences_AndPublicTypes()
    {
        var facts = ScanRepository();

        var core = Assert.Single(facts.Projects, p => p.Name == "Orchestrator.Core");
        Assert.Contains(core.PublicTypes, t => t.Contains("WorkflowEngine", StringComparison.Ordinal));

        var agents = Assert.Single(facts.Projects, p => p.Name == "Orchestrator.Agents");
        Assert.Contains("Orchestrator.Core", agents.ProjectReferences);
    }

    [Fact]
    public void Scan_FindsFilesRelatedToTheRequirement()
    {
        var facts = ScanRepository("Add expiry handling to the ShortUrl redirect flow");

        Assert.NotEmpty(facts.RelevantFiles);
        // Paths are reported relative to the scan root, so nothing absolute leaks into a prompt.
        Assert.All(facts.RelevantFiles, file => Assert.False(Path.IsPathRooted(file)));
    }

    [Fact]
    public void Scan_IgnoresBuildOutputDirectories()
    {
        var facts = ScanRepository();

        Assert.DoesNotContain(facts.RelevantFiles, f => f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
        Assert.DoesNotContain(facts.RelevantFiles, f => f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    [Fact]
    public void Scan_OnNonexistentDirectory_ReturnsEmptyFacts()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"no-such-repo-{Guid.NewGuid():N}");

        var facts = new CodebaseScanner(missing).Scan("anything at all");

        Assert.True(facts.IsEmpty);
        Assert.Empty(facts.Projects);
        Assert.Empty(facts.RelevantFiles);
        Assert.Equal(missing, facts.Root);
    }

    [Fact]
    public void Render_OfEmptyFacts_SaysGreenfield()
    {
        var facts = new CodebaseFacts("/nowhere", [], []);

        Assert.Contains("greenfield", CodebaseScanner.Render(facts), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_OfRealFacts_MentionsProjectsAndRoutes()
    {
        var rendered = CodebaseScanner.Render(ScanRepository());

        Assert.Contains("UrlShortener.Api", rendered);
        Assert.Contains("http routes", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("greenfield", rendered, StringComparison.OrdinalIgnoreCase);
    }
}
