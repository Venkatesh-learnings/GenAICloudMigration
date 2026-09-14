using System.Text;
using System.Text.RegularExpressions;

namespace Orchestrator.Agents;

public record ProjectFacts(
    string Name,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> PublicTypes,
    IReadOnlyList<string> HttpRoutes,
    IReadOnlyList<string> EntitySets);

public record CodebaseFacts(
    string Root,
    IReadOnlyList<ProjectFacts> Projects,
    IReadOnlyList<string> RelevantFiles)
{
    public bool IsEmpty => Projects.Count == 0;
}

/// <summary>
/// Reads the actual repository to answer "what is here and what would this change touch?"
/// — projects and how they depend on each other, the public types in each, the HTTP routes
/// the API exposes, and the EF entity sets behind it. Deliberately regex-over-source rather
/// than a Roslyn workspace: it needs to be dependency-free and fast enough to run inside a
/// pipeline stage, and the questions it answers (which module owns this surface?) don't
/// need a full semantic model.
/// </summary>
public partial class CodebaseScanner(string root)
{
    private static readonly string[] IgnoredDirectories = ["bin", "obj", ".git", "artifacts", "node_modules"];

    public CodebaseFacts Scan(string requirementText, int maxRelevantFiles = 12)
    {
        if (!Directory.Exists(root))
        {
            return new CodebaseFacts(root, [], []);
        }

        var projectFiles = SafeEnumerate(root, "*.csproj");
        var projects = projectFiles.Select(ScanProject).OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        var relevant = FindRelevantFiles(requirementText, maxRelevantFiles);

        return new CodebaseFacts(root, projects, relevant);
    }

    private ProjectFacts ScanProject(string projectFile)
    {
        var projectDir = Path.GetDirectoryName(projectFile)!;
        var name = Path.GetFileNameWithoutExtension(projectFile);

        var projectXml = ReadTextOrEmpty(projectFile);
        var references = ProjectReferenceRegex().Matches(projectXml)
            .Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value.Replace('\\', '/')))
            .Distinct()
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        var sources = SafeEnumerate(projectDir, "*.cs");

        var publicTypes = new List<string>();
        var routes = new List<string>();
        var entitySets = new List<string>();

        foreach (var file in sources)
        {
            var text = ReadTextOrEmpty(file);

            publicTypes.AddRange(PublicTypeRegex().Matches(text).Select(m => $"{m.Groups[2].Value} ({m.Groups[1].Value})"));
            routes.AddRange(HttpMethodRegex().Matches(text)
                .Select(m => $"{m.Groups[1].Value.ToUpperInvariant()} {(m.Groups[2].Success ? m.Groups[2].Value : "(controller route)")} [{Path.GetFileNameWithoutExtension(file)}]"));
            entitySets.AddRange(DbSetRegex().Matches(text).Select(m => $"{m.Groups[2].Value}: {m.Groups[1].Value}"));
        }

        return new ProjectFacts(
            name,
            references,
            publicTypes.Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList(),
            routes.Distinct().OrderBy(r => r, StringComparer.Ordinal).ToList(),
            entitySets.Distinct().OrderBy(e => e, StringComparer.Ordinal).ToList());
    }

    /// <summary>Files whose content mentions the requirement's distinctive words — the likely blast radius.</summary>
    private List<string> FindRelevantFiles(string requirementText, int max)
    {
        var keywords = ExtractKeywords(requirementText);
        if (keywords.Count == 0)
        {
            return [];
        }

        return SafeEnumerate(root, "*.cs")
            .Select(file => (File: file, Text: ReadTextOrEmpty(file)))
            .Select(x => (x.File, Hits: keywords.Count(k => x.Text.Contains(k, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Hits > 0)
            .OrderByDescending(x => x.Hits)
            .ThenBy(x => x.File, StringComparer.Ordinal)
            .Take(max)
            .Select(x => $"{Path.GetRelativePath(root, x.File)} ({x.Hits} keyword hit(s))")
            .ToList();
    }

    private static readonly string[] StopWords =
    [
        "the", "and", "for", "that", "this", "with", "from", "into", "every", "given", "without",
        "existing", "must", "should", "would", "make", "more", "when", "where", "which", "their",
        "them", "than", "then", "have", "has", "one", "call", "each", "add", "new", "all", "any"
    ];

    private static List<string> ExtractKeywords(string text) =>
        WordRegex().Matches(text)
            .Select(m => m.Value)
            .Where(w => w.Length >= 5 && !StopWords.Contains(w, StringComparer.OrdinalIgnoreCase))
            .Select(w => w.ToLowerInvariant())
            .Distinct()
            .Take(12)
            .ToList();

    private static IEnumerable<string> SafeEnumerate(string directory, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
                .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(segment => IgnoredDirectories.Contains(segment)))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string ReadTextOrEmpty(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    public static string Render(CodebaseFacts facts)
    {
        if (facts.IsEmpty)
        {
            return "No existing projects found — treat this as greenfield.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Scanned {facts.Projects.Count} project(s) under {facts.Root}:");
        sb.AppendLine();

        foreach (var project in facts.Projects)
        {
            sb.AppendLine($"- {project.Name}");
            if (project.ProjectReferences.Count > 0)
            {
                sb.AppendLine($"    references: {string.Join(", ", project.ProjectReferences)}");
            }
            if (project.HttpRoutes.Count > 0)
            {
                sb.AppendLine($"    http routes: {string.Join("; ", project.HttpRoutes)}");
            }
            if (project.EntitySets.Count > 0)
            {
                sb.AppendLine($"    entity sets: {string.Join("; ", project.EntitySets)}");
            }
            if (project.PublicTypes.Count > 0)
            {
                sb.AppendLine($"    public types: {string.Join(", ", project.PublicTypes.Take(12))}{(project.PublicTypes.Count > 12 ? ", ..." : "")}");
            }
        }

        if (facts.RelevantFiles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Files most related to this requirement:");
            foreach (var file in facts.RelevantFiles)
            {
                sb.AppendLine($"- {file}");
            }
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"<ProjectReference\s+Include=""([^""]+)""")]
    private static partial Regex ProjectReferenceRegex();

    [GeneratedRegex(@"public\s+(?:sealed\s+|abstract\s+|static\s+|partial\s+)*(class|record|interface|enum)\s+(\w+)")]
    private static partial Regex PublicTypeRegex();

    [GeneratedRegex("""\[Http(Get|Post|Put|Delete|Patch)(?:\("([^"]*)"\))?\]""")]
    private static partial Regex HttpMethodRegex();

    [GeneratedRegex(@"DbSet<(\w+)>\s+(\w+)")]
    private static partial Regex DbSetRegex();

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9_]*")]
    private static partial Regex WordRegex();
}
