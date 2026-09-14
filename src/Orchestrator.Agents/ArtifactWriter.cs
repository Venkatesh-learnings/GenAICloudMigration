namespace Orchestrator.Agents;

/// <summary>Writes generated engineering output (code, docs, reports) to a per-run artifact directory.</summary>
public class ArtifactWriter(string artifactsRoot)
{
    public string WriteText(string runId, string stageId, string fileName, string content)
    {
        var dir = Path.Combine(artifactsRoot, runId, stageId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, content);
        return path;
    }
}
