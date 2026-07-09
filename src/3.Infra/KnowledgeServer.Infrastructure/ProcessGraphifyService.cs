using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using KnowledgeServer.Application;
using Microsoft.Extensions.Options;

namespace KnowledgeServer.Infrastructure;

public sealed class ProcessGraphifyService(
    IWorkspaceStore workspaceStore,
    IOptions<WorkspaceOptions> options) : IGraphifyService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<GraphifyResult> GenerateAsync(
        GraphifyRequest request,
        CancellationToken cancellationToken)
    {
        var workspace = await workspaceStore.GetOrCreateWorkspaceAsync(request.WorkspaceId, cancellationToken);
        var inputRoot = WorkspaceLayout.InputsRoot(workspace.RootPath);
        var graphRoot = WorkspaceLayout.GraphsRoot(workspace.RootPath);
        Directory.CreateDirectory(inputRoot);
        Directory.CreateDirectory(graphRoot);

        var artifacts = new List<string>();
        var processSucceeded = false;
        GraphifyExecution? execution = null;
        if (!request.ForceFallback)
        {
            execution = await ExecuteGraphifyWithRetryAsync(inputRoot, graphRoot, cancellationToken);
            processSucceeded = execution.ExitCode == 0;
            artifacts.Add(await WriteProcessLogAsync(graphRoot, execution, cancellationToken));
            await NormalizeGraphifyArtifactsAsync(inputRoot, graphRoot, artifacts, cancellationToken);
        }

        var normalizedGraphifyOutputRoot = Path.Combine(graphRoot, "graphify-out");
        var hasGraphifyOutput = Directory.Exists(normalizedGraphifyOutputRoot)
            && Directory.EnumerateFiles(normalizedGraphifyOutputRoot, "graph.html", SearchOption.AllDirectories).Any();

        var manifest = new
        {
            request.WorkspaceId,
            inputRoot,
            outputRoot = graphRoot,
            generator = hasGraphifyOutput ? "graphify-process" : "graphify-unavailable",
            generatedAt = DateTimeOffset.UtcNow,
            nodeCount = (int?)null,
            edgeCount = (int?)null,
            processSucceeded,
            hasGraphifyOutput,
            artifacts
        };
        artifacts.Add(await WriteArtifactAsync(graphRoot, "manifest.json", manifest, cancellationToken));

        return new GraphifyResult(
            workspace.Id,
            inputRoot,
            graphRoot,
            manifest.generator,
            manifest.generatedAt,
            0,
            0,
            artifacts);
    }

    private async Task<GraphifyExecution> ExecuteGraphifyWithRetryAsync(
        string inputRoot,
        string graphRoot,
        CancellationToken cancellationToken)
    {
        var command = options.Value.GraphifyCommand;
        if (string.IsNullOrWhiteSpace(command))
        {
            return new GraphifyExecution(
                command,
                string.Empty,
                inputRoot,
                graphRoot,
                DateTimeOffset.UtcNow,
                null,
                null,
                null,
                null,
                "GraphifyCommand não configurado.",
                []);
        }

        var arguments = options.Value.GraphifyArguments
            .Replace("{InputRoot}", inputRoot, StringComparison.Ordinal)
            .Replace("{RepositoryRoot}", WorkspaceLayout.RepositoriesRoot(Path.GetDirectoryName(inputRoot) ?? inputRoot), StringComparison.Ordinal)
            .Replace("{GraphRoot}", graphRoot, StringComparison.Ordinal);

        var firstAttempt = await RunGraphifyAsync(command, arguments, inputRoot, graphRoot, cancellationToken);
        if (!ShouldRetryCodeOnly(firstAttempt))
        {
            return firstAttempt with { Attempts = [firstAttempt.ToAttempt()] };
        }

        var retryArguments = $"{arguments} --code-only";
        var retryAttempt = await RunGraphifyAsync(command, retryArguments, inputRoot, graphRoot, cancellationToken);
        return retryAttempt with
        {
            Attempts = [
                firstAttempt.ToAttempt(),
                retryAttempt.ToAttempt("Retry automático com --code-only após falha por falta de chave de LLM.")
            ]
        };
    }

    private static bool ShouldRetryCodeOnly(GraphifyExecution execution)
    {
        return execution.ExitCode is not 0
            && !string.IsNullOrWhiteSpace(execution.Stderr)
            && execution.Stderr.Contains("no LLM API key found", StringComparison.OrdinalIgnoreCase)
            && !execution.Arguments.Contains("--code-only", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<GraphifyExecution> RunGraphifyAsync(
        string command,
        string arguments,
        string inputRoot,
        string graphRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                WorkingDirectory = graphRoot,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var startedAt = DateTimeOffset.UtcNow;
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            return new GraphifyExecution(
                command,
                arguments,
                inputRoot,
                graphRoot,
                startedAt,
                DateTimeOffset.UtcNow,
                process.ExitCode,
                await stdoutTask,
                await stderrTask,
                null,
                []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new GraphifyExecution(
                command,
                arguments,
                inputRoot,
                graphRoot,
                DateTimeOffset.UtcNow,
                null,
                null,
                null,
                null,
                ex.Message,
                []);
        }
    }

    private static async Task<string> WriteProcessLogAsync(
        string graphRoot,
        GraphifyExecution execution,
        CancellationToken cancellationToken)
    {
        var processLogPath = Path.Combine(graphRoot, "graphify-process.json");
        var log = new
        {
            execution.Command,
            execution.Arguments,
            execution.InputRoot,
            execution.GraphRoot,
            startedAt = execution.StartedAt,
            finishedAt = execution.FinishedAt,
            exitCode = execution.ExitCode,
            stdout = execution.Stdout,
            stderr = execution.Stderr,
            error = execution.Error,
            attempts = execution.Attempts.Select(attempt => new
            {
                attempt.Command,
                attempt.Arguments,
                startedAt = attempt.StartedAt,
                finishedAt = attempt.FinishedAt,
                exitCode = attempt.ExitCode,
                stdout = attempt.Stdout,
                stderr = attempt.Stderr,
                error = attempt.Error,
                attempt.Note
            })
        };

        await File.WriteAllTextAsync(
            processLogPath,
            JsonSerializer.Serialize(log, JsonOptions),
            Encoding.UTF8,
            cancellationToken);

        return processLogPath;
    }

    private static async Task<GraphDocument> BuildFallbackGraphAsync(
        string workspaceRoot,
        string inputRoot,
        CancellationToken cancellationToken)
    {
        var nodes = new List<GraphNode>
        {
            new(WorkspaceLayout.InputsRootName, WorkspaceLayout.InputsRootName, "directory")
        };
        var edges = new List<GraphEdge>();

        if (!Directory.Exists(inputRoot))
        {
            return new GraphDocument(nodes, edges);
        }

        foreach (var directory in Directory.EnumerateDirectories(inputRoot, "*", SearchOption.AllDirectories)
                     .Where(path => !IsIgnoredPath(inputRoot, path))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var id = NormalizeId(Path.GetRelativePath(workspaceRoot, directory));
            var label = Path.GetFileName(directory);
            nodes.Add(new GraphNode(id, label, "directory"));
            edges.Add(new GraphEdge(ParentId(workspaceRoot, inputRoot, directory), id, "contains"));
        }

        foreach (var file in Directory.EnumerateFiles(inputRoot, "*", SearchOption.AllDirectories)
                     .Where(path => !IsIgnoredPath(inputRoot, path))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var id = NormalizeId(Path.GetRelativePath(workspaceRoot, file));
            var info = new FileInfo(file);
            nodes.Add(new GraphNode(id, info.Name, "file", info.Length, Path.GetExtension(file)));
            edges.Add(new GraphEdge(ParentId(workspaceRoot, inputRoot, file), id, "contains"));

            if (Path.GetExtension(file).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                var content = await TryReadTextAsync(file, cancellationToken);
                if (content is not null)
                {
                    foreach (var projectRef in ExtractProjectReferences(file, content))
                    {
                        edges.Add(new GraphEdge(id, NormalizeId(Path.GetRelativePath(workspaceRoot, projectRef)), "project-reference"));
                    }
                }
            }
        }

        return new GraphDocument(nodes, edges);
    }

    private static IEnumerable<string> ExtractProjectReferences(string projectFile, string content)
    {
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                     content,
                     "<ProjectReference\\s+Include=\"(?<path>[^\"]+)\""))
        {
            var relative = match.Groups["path"].Value;
            yield return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectFile) ?? ".", relative));
        }
    }

    private static string ParentId(string workspaceRoot, string inputRoot, string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent) || Path.GetFullPath(parent) == Path.GetFullPath(inputRoot))
        {
            return WorkspaceLayout.InputsRootName;
        }

        return NormalizeId(Path.GetRelativePath(workspaceRoot, parent));
    }

    private static async Task<string> WriteArtifactAsync<T>(
        string graphRoot,
        string fileName,
        T value,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(graphRoot, fileName);
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(value, JsonOptions),
            Encoding.UTF8,
            cancellationToken);
        return path;
    }

    private static async Task<string> WriteHtmlAsync(
        string graphRoot,
        GraphDocument graph,
        CancellationToken cancellationToken)
    {
        var nodes = string.Join(
            Environment.NewLine,
            graph.Nodes.Select(node => $"""
                    <li><span class="kind">{WebUtility.HtmlEncode(node.Kind)}</span> {WebUtility.HtmlEncode(node.Label)} <code>{WebUtility.HtmlEncode(node.Id)}</code></li>
                """));
        var edges = string.Join(
            Environment.NewLine,
            graph.Edges.Select(edge => $"""
                    <li><code>{WebUtility.HtmlEncode(edge.Source)}</code> <span>{WebUtility.HtmlEncode(edge.Kind)}</span> <code>{WebUtility.HtmlEncode(edge.Target)}</code></li>
                """));

        var html = $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Workspace Graph</title>
              <style>
                body { font-family: ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; margin: 32px; color: #172026; background: #f7f8fa; }
                main { max-width: 1120px; margin: 0 auto; }
                h1 { font-size: 28px; margin: 0 0 8px; }
                h2 { font-size: 18px; margin-top: 28px; }
                .meta { color: #53606b; margin-bottom: 24px; }
                .grid { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: 24px; }
                section { background: #fff; border: 1px solid #d9dee3; border-radius: 8px; padding: 18px; }
                ul { list-style: none; padding: 0; margin: 0; display: grid; gap: 8px; }
                li { overflow-wrap: anywhere; line-height: 1.45; }
                code { font-size: 12px; color: #47515a; }
                .kind { display: inline-block; min-width: 72px; color: #005f73; font-weight: 650; }
                @media (max-width: 760px) { body { margin: 18px; } .grid { grid-template-columns: 1fr; } }
              </style>
            </head>
            <body>
              <main>
                <h1>Workspace Graph</h1>
                <p class="meta">{{graph.Nodes.Count}} nodes, {{graph.Edges.Count}} edges. Generated by fallback-file-graph.</p>
                <div class="grid">
                  <section>
                    <h2>Nodes</h2>
                    <ul>
            {{nodes}}
                    </ul>
                  </section>
                  <section>
                    <h2>Edges</h2>
                    <ul>
            {{edges}}
                    </ul>
                  </section>
                </div>
              </main>
            </body>
            </html>
            """;

        var path = Path.Combine(graphRoot, "index.html");
        await File.WriteAllTextAsync(path, html, Encoding.UTF8, cancellationToken);
        return path;
    }

    private static bool IsIgnoredPath(string inputRoot, string path)
    {
        var relative = Path.GetRelativePath(inputRoot, path);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment => segment is ".git" or "bin" or "obj" or "node_modules");
    }

    private static Task NormalizeGraphifyArtifactsAsync(
        string inputRoot,
        string graphRoot,
        ICollection<string> artifacts,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var preferredOutput = Path.Combine(graphRoot, "graphify-out");
        var misplacedOutput = Path.Combine(inputRoot, "graphify-out");

        if (Directory.Exists(misplacedOutput))
        {
            MoveDirectoryContents(misplacedOutput, preferredOutput);
            if (!artifacts.Contains(preferredOutput))
            {
                artifacts.Add(preferredOutput);
            }
        }

        if (Directory.Exists(preferredOutput) && !artifacts.Contains(preferredOutput))
        {
            artifacts.Add(preferredOutput);
        }

        return Task.CompletedTask;
    }

    private static void MoveDirectoryContents(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            var destination = Path.Combine(targetDirectory, Path.GetFileName(directory));
            MoveDirectoryContents(directory, destination);
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            var destination = Path.Combine(targetDirectory, Path.GetFileName(file));
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            File.Move(file, destination);
        }

        if (!Directory.EnumerateFileSystemEntries(sourceDirectory).Any())
        {
            Directory.Delete(sourceDirectory, recursive: false);
        }
    }

    private static string NormalizeId(string path)
    {
        return path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static async Task<string?> TryReadTextAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record GraphifyExecution(
        string Command,
        string Arguments,
        string InputRoot,
        string GraphRoot,
        DateTimeOffset StartedAt,
        DateTimeOffset? FinishedAt,
        int? ExitCode,
        string? Stdout,
        string? Stderr,
        string? Error,
        IReadOnlyCollection<GraphifyAttempt> Attempts)
    {
        public GraphifyAttempt ToAttempt(string? note = null)
            => new(Command, Arguments, StartedAt, FinishedAt, ExitCode, Stdout, Stderr, Error, note);
    }

    private sealed record GraphifyAttempt(
        string Command,
        string Arguments,
        DateTimeOffset StartedAt,
        DateTimeOffset? FinishedAt,
        int? ExitCode,
        string? Stdout,
        string? Stderr,
        string? Error,
        string? Note);

    private sealed record GraphDocument(
        IReadOnlyCollection<GraphNode> Nodes,
        IReadOnlyCollection<GraphEdge> Edges);

    private sealed record GraphNode(
        string Id,
        string Label,
        string Kind,
        long? Size = null,
        string? Extension = null);

    private sealed record GraphEdge(
        string Source,
        string Target,
        string Kind);
}
