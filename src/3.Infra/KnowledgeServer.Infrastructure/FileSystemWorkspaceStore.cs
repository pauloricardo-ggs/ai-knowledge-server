using System.Text;
using System.Text.Json;
using KnowledgeServer.Application;
using KnowledgeServer.Domain;
using Microsoft.Extensions.Options;

namespace KnowledgeServer.Infrastructure;

public sealed class FileSystemWorkspaceStore(IOptions<WorkspaceOptions> options) : IWorkspaceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly string[] SearchableExtensions =
    [
        ".md",
        ".txt",
        ".json",
        ".yml",
        ".yaml",
        ".cs",
        ".csproj",
        ".sln",
        ".slnx"
    ];

    public static IReadOnlyCollection<string> SupportedTextExtensions => SearchableExtensions;

    private readonly string rootPath = Path.GetFullPath(options.Value.RootPath);

    public async Task<IReadOnlyCollection<Workspace>> ListWorkspacesAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(rootPath);

        var directories = Directory
            .EnumerateDirectories(rootPath)
            .Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        var workspaces = new List<Workspace>();
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            workspaces.Add(await ReadOrCreateWorkspaceAsync(directory, cancellationToken));
        }

        return workspaces;
    }

    public async Task<Workspace> GetOrCreateWorkspaceAsync(string workspaceId, CancellationToken cancellationToken)
    {
        var safeId = NormalizeSegment(workspaceId);
        var workspacePath = Path.Combine(rootPath, safeId);
        Directory.CreateDirectory(workspacePath);

        EnsureWorkspaceLayout(workspacePath);

        return await ReadOrCreateWorkspaceAsync(workspacePath, cancellationToken);
    }

    public async Task<WorkspaceDocument> SaveDocumentAsync(
        string workspaceId,
        string category,
        string fileName,
        Stream content,
        CancellationToken cancellationToken)
    {
        var workspace = await GetOrCreateWorkspaceAsync(workspaceId, cancellationToken);
        var safeCategory = NormalizeSegment(string.IsNullOrWhiteSpace(category) ? "raw" : category);
        var safeFileName = NormalizeFileName(fileName);
        var targetDirectory = Path.Combine(WorkspaceLayout.DocumentsRoot(workspace.RootPath), safeCategory);
        Directory.CreateDirectory(targetDirectory);

        var targetPath = Path.Combine(targetDirectory, safeFileName);
        await using (var target = File.Create(targetPath))
        {
            await content.CopyToAsync(target, cancellationToken);
        }

        await EnqueueIndexingJobAsync(workspace.Id, "document-uploaded", targetPath, cancellationToken);

        return ToDocument(workspace.Id, workspace.RootPath, targetPath);
    }

    public async Task<IReadOnlyCollection<WorkspaceDocument>> ListDocumentsAsync(
        string workspaceId,
        CancellationToken cancellationToken)
    {
        var workspace = await GetOrCreateWorkspaceAsync(workspaceId, cancellationToken);
        var documentsRoot = WorkspaceLayout.DocumentsRoot(workspace.RootPath);
        if (!Directory.Exists(documentsRoot))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(documentsRoot, "*", SearchOption.AllDirectories)
            .Where(path => !IsIgnoredPath(workspace.RootPath, path))
            .Select(path => ToDocument(workspace.Id, workspace.RootPath, path))
            .OrderBy(document => document.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<WorkspaceRepository> RegisterRepositoryAsync(
        string workspaceId,
        string name,
        string relativePath,
        string? remoteUrl,
        string? branch,
        CancellationToken cancellationToken)
    {
        var workspace = await GetOrCreateWorkspaceAsync(workspaceId, cancellationToken);
        var safeName = NormalizeSegment(name);
        var safeRelativePath = NormalizeRepositoryPath(string.IsNullOrWhiteSpace(relativePath)
            ? Path.Combine(WorkspaceLayout.RepositoriesRootName, safeName)
            : relativePath);

        if (!safeRelativePath.StartsWith($"{WorkspaceLayout.InputsRootName}/{WorkspaceLayout.RepositoriesRootName}/", StringComparison.Ordinal)
            && safeRelativePath != $"{WorkspaceLayout.InputsRootName}/{WorkspaceLayout.RepositoriesRootName}")
        {
            safeRelativePath = safeRelativePath.StartsWith($"{WorkspaceLayout.RepositoriesRootName}/", StringComparison.Ordinal)
                || safeRelativePath == WorkspaceLayout.RepositoriesRootName
                ? $"{WorkspaceLayout.InputsRootName}/{safeRelativePath}"
                : $"{WorkspaceLayout.InputsRootName}/{WorkspaceLayout.RepositoriesRootName}/{safeRelativePath}";
        }

        var absoluteRepositoryPath = Path.GetFullPath(Path.Combine(workspace.RootPath, safeRelativePath));
        if (!absoluteRepositoryPath.StartsWith(workspace.RootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Repository path must stay inside the workspace.");
        }

        Directory.CreateDirectory(absoluteRepositoryPath);

        var repository = new WorkspaceRepository(
            workspace.Id,
            safeName,
            safeRelativePath,
            string.IsNullOrWhiteSpace(remoteUrl) ? null : remoteUrl,
            string.IsNullOrWhiteSpace(branch) ? null : branch,
            TryReadGitCommit(absoluteRepositoryPath),
            DateTimeOffset.UtcNow);

        var repositories = (await ListRepositoriesAsync(workspace.Id, cancellationToken))
            .Where(existing => !existing.Name.Equals(repository.Name, StringComparison.OrdinalIgnoreCase))
            .Append(repository)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await WriteRepositoriesAsync(workspace.RootPath, repositories, cancellationToken);

        await EnqueueIndexingJobAsync(
            workspace.Id,
            "repository-registered",
            absoluteRepositoryPath,
            cancellationToken);

        return repository;
    }

    public async Task<IReadOnlyCollection<WorkspaceRepository>> ListRepositoriesAsync(
        string workspaceId,
        CancellationToken cancellationToken)
    {
        var workspace = await GetOrCreateWorkspaceAsync(workspaceId, cancellationToken);
        var metadataPath = RepositoriesMetadataPath(workspace.RootPath);
        var registered = new List<WorkspaceRepository>();

        if (File.Exists(metadataPath))
        {
            var json = await File.ReadAllTextAsync(metadataPath, cancellationToken);
            registered.AddRange(JsonSerializer.Deserialize<WorkspaceRepository[]>(json, JsonOptions) ?? []);
        }

        var repositoriesRoot = WorkspaceLayout.RepositoriesRoot(workspace.RootPath);
        if (Directory.Exists(repositoriesRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(repositoriesRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = Path.GetRelativePath(workspace.RootPath, directory);
                if (registered.Any(item => item.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                registered.Add(new WorkspaceRepository(
                    workspace.Id,
                    Path.GetFileName(directory),
                    relativePath,
                    TryReadGitRemote(directory),
                    TryReadGitBranch(directory),
                    TryReadGitCommit(directory),
                    Directory.GetCreationTimeUtc(directory)));
            }
        }

        return registered
            .OrderBy(repository => repository.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyCollection<SearchResult>> SearchAsync(
        string workspaceId,
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var workspace = await GetOrCreateWorkspaceAsync(workspaceId, cancellationToken);
        var terms = Tokenize(query);
        if (terms.Length == 0)
        {
            return [];
        }

        var results = new List<SearchResult>();
        foreach (var file in Directory.EnumerateFiles(workspace.RootPath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsIgnoredPath(workspace.RootPath, file) || !IsSearchable(file))
            {
                continue;
            }

            var content = await TryReadTextAsync(file, cancellationToken);
            if (content is null)
            {
                continue;
            }

            var score = terms.Sum(term => CountOccurrences(content, term));
            if (score == 0)
            {
                continue;
            }

            results.Add(new SearchResult(
                workspace.Id,
                Path.GetRelativePath(workspace.RootPath, file),
                BuildSnippet(content, terms),
                score));
        }

        return results
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxResults, 1, 50))
            .ToArray();
    }

    public async Task<IndexingJob> EnqueueIndexingJobAsync(
        string workspaceId,
        string reason,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var workspace = await GetOrCreateWorkspaceAsync(workspaceId, cancellationToken);
        var job = new IndexingJob(
            Guid.NewGuid().ToString("n"),
            workspace.Id,
            reason,
            ToRelativeOrOriginal(workspace.RootPath, sourcePath),
            "queued",
            DateTimeOffset.UtcNow);

        var jobsDirectory = Path.Combine(workspace.RootPath, "cache", "jobs");
        Directory.CreateDirectory(jobsDirectory);

        var path = Path.Combine(jobsDirectory, $"{job.CreatedAt:yyyyMMddHHmmssfff}-{job.Id}.json");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(job, JsonOptions),
            Encoding.UTF8,
            cancellationToken);

        return job;
    }

    public async Task<IReadOnlyCollection<IndexingJob>> ListIndexingJobsAsync(
        string workspaceId,
        CancellationToken cancellationToken)
    {
        var workspace = await GetOrCreateWorkspaceAsync(workspaceId, cancellationToken);
        var jobsDirectory = Path.Combine(workspace.RootPath, "cache", "jobs");
        if (!Directory.Exists(jobsDirectory))
        {
            return [];
        }

        var jobs = new List<IndexingJob>();
        foreach (var file in Directory.EnumerateFiles(jobsDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var json = await File.ReadAllTextAsync(file, cancellationToken);
            var job = JsonSerializer.Deserialize<IndexingJob>(json, JsonOptions);
            if (job is not null)
            {
                jobs.Add(job);
            }
        }

        return jobs
            .OrderByDescending(job => job.CreatedAt)
            .ToArray();
    }

    public async Task<IReadOnlyCollection<IndexingJob>> ListQueuedIndexingJobsAsync(
        CancellationToken cancellationToken)
    {
        var workspaces = await ListWorkspacesAsync(cancellationToken);
        var jobs = new List<IndexingJob>();

        foreach (var workspace in workspaces)
        {
            var workspaceJobs = await ListIndexingJobsAsync(workspace.Id, cancellationToken);
            jobs.AddRange(workspaceJobs.Where(job => job.Status == "queued"));
        }

        return jobs
            .OrderBy(job => job.CreatedAt)
            .ToArray();
    }

    public async Task<IndexingJob?> UpdateIndexingJobStatusAsync(
        string workspaceId,
        string jobId,
        string status,
        string? error,
        CancellationToken cancellationToken)
    {
        return await UpdateIndexingJobAsync(
            workspaceId,
            jobId,
            job =>
            {
                var now = DateTimeOffset.UtcNow;
                return job with
                {
                    Status = status,
                    StartedAt = job.StartedAt ?? (status == "running" ? now : job.StartedAt),
                    CompletedAt = status is "completed" or "failed" ? now : job.CompletedAt,
                    Error = error
                };
            },
            cancellationToken);
    }

    public async Task<IndexingJob?> UpdateIndexingJobProgressAsync(
        string workspaceId,
        string jobId,
        IndexingProgress progress,
        CancellationToken cancellationToken)
    {
        return await UpdateIndexingJobAsync(
            workspaceId,
            jobId,
            job => job with
            {
                Progress = progress with
                {
                    UpdatedAt = progress.UpdatedAt ?? DateTimeOffset.UtcNow
                }
            },
            cancellationToken);
    }

    public async Task<IndexingJob?> AppendIndexingJobLogAsync(
        string workspaceId,
        string jobId,
        IndexingLogEntry logEntry,
        CancellationToken cancellationToken)
    {
        return await UpdateIndexingJobAsync(
            workspaceId,
            jobId,
            job =>
            {
                var logs = (job.Logs ?? [])
                    .Append(logEntry)
                    .TakeLast(300)
                    .ToArray();

                return job with
                {
                    Logs = logs
                };
            },
            cancellationToken);
    }

    public async Task<Workspace?> FindWorkspaceAsync(string workspaceId, CancellationToken cancellationToken)
    {
        var workspaces = await ListWorkspacesAsync(cancellationToken);
        return workspaces.FirstOrDefault(workspace => workspace.Id == workspaceId);
    }

    private async Task<Workspace> ReadOrCreateWorkspaceAsync(string workspacePath, CancellationToken cancellationToken)
    {
        EnsureWorkspaceLayout(workspacePath);

        var configPath = Path.Combine(workspacePath, "workspace.json");
        if (File.Exists(configPath))
        {
            var json = await File.ReadAllTextAsync(configPath, cancellationToken);
            var workspace = JsonSerializer.Deserialize<Workspace>(json, JsonOptions);
            if (workspace is not null)
            {
                return workspace with { RootPath = workspacePath };
            }
        }

        var created = new Workspace(
            Path.GetFileName(workspacePath),
            Path.GetFileName(workspacePath),
            workspacePath,
            DateTimeOffset.UtcNow);

        await File.WriteAllTextAsync(
            configPath,
            JsonSerializer.Serialize(created, JsonOptions),
            Encoding.UTF8,
            cancellationToken);

        return created;
    }

    private async Task<IndexingJob?> UpdateIndexingJobAsync(
        string workspaceId,
        string jobId,
        Func<IndexingJob, IndexingJob> update,
        CancellationToken cancellationToken)
    {
        var workspace = await GetOrCreateWorkspaceAsync(workspaceId, cancellationToken);
        var jobsDirectory = Path.Combine(workspace.RootPath, "cache", "jobs");
        if (!Directory.Exists(jobsDirectory))
        {
            return null;
        }

        foreach (var file in Directory.EnumerateFiles(jobsDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var json = await File.ReadAllTextAsync(file, cancellationToken);
            var job = JsonSerializer.Deserialize<IndexingJob>(json, JsonOptions);
            if (job?.Id != jobId)
            {
                continue;
            }

            var updated = update(job);

            await File.WriteAllTextAsync(
                file,
                JsonSerializer.Serialize(updated, JsonOptions),
                Encoding.UTF8,
                cancellationToken);

            return updated;
        }

        return null;
    }

    private static void EnsureWorkspaceLayout(string workspacePath)
    {
        MigrateLegacyInputDirectory(workspacePath, WorkspaceLayout.RepositoriesRootName, WorkspaceLayout.RepositoriesRoot(workspacePath));
        MigrateLegacyInputDirectory(workspacePath, WorkspaceLayout.DocumentsRootName, WorkspaceLayout.DocumentsRoot(workspacePath));
        MigrateLegacyMetadataFile(
            Path.Combine(WorkspaceLayout.RepositoriesRoot(workspacePath), "repositories.json"),
            RepositoriesMetadataPath(workspacePath));

        Directory.CreateDirectory(WorkspaceLayout.RepositoriesRoot(workspacePath));
        Directory.CreateDirectory(WorkspaceLayout.InputsRoot(workspacePath));
        Directory.CreateDirectory(WorkspaceLayout.RawDocumentsRoot(workspacePath));
        Directory.CreateDirectory(WorkspaceLayout.GraphsRoot(workspacePath));
        Directory.CreateDirectory(WorkspaceLayout.RoslynRoot(workspacePath));
        Directory.CreateDirectory(WorkspaceLayout.SummariesRoot(workspacePath));
        Directory.CreateDirectory(WorkspaceLayout.EmbeddingsRoot(workspacePath));
        Directory.CreateDirectory(WorkspaceLayout.JobsRoot(workspacePath));
        Directory.CreateDirectory(WorkspaceLayout.LogsRoot(workspacePath));
    }

    private static string RepositoriesMetadataPath(string workspaceRoot)
    {
        return Path.Combine(WorkspaceLayout.InputsRoot(workspaceRoot), "repositories.json");
    }

    private static async Task WriteRepositoriesAsync(
        string workspaceRoot,
        IReadOnlyCollection<WorkspaceRepository> repositories,
        CancellationToken cancellationToken)
    {
        var path = RepositoriesMetadataPath(workspaceRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(repositories, JsonOptions),
            Encoding.UTF8,
            cancellationToken);
    }

    private static WorkspaceDocument ToDocument(string workspaceId, string workspaceRoot, string path)
    {
        var info = new FileInfo(path);
        var relativePath = Path.GetRelativePath(workspaceRoot, path);
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var category = segments.Length >= 4
            && segments[0] == WorkspaceLayout.InputsRootName
            && segments[1] == WorkspaceLayout.DocumentsRootName
            ? segments[2]
            : "unknown";

        return new WorkspaceDocument(
            workspaceId,
            relativePath,
            info.Name,
            category,
            info.Length,
            info.LastWriteTimeUtc);
    }

    private static bool IsSearchable(string path)
    {
        var extension = Path.GetExtension(path);
        return SearchableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsIgnoredPath(string workspaceRoot, string path)
    {
        var relative = Path.GetRelativePath(workspaceRoot, path);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment => segment is ".git" or "bin" or "obj" or "node_modules")
            || segments.FirstOrDefault() is "cache" or "logs" or "embeddings";
    }

    private static void MigrateLegacyInputDirectory(string workspacePath, string legacyDirectoryName, string targetPath)
    {
        var legacyPath = Path.Combine(workspacePath, legacyDirectoryName);
        if (!Directory.Exists(legacyPath))
        {
            return;
        }

        if (!Directory.Exists(targetPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            Directory.Move(legacyPath, targetPath);
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(legacyPath))
        {
            var destination = Path.Combine(targetPath, Path.GetFileName(directory));
            if (!Directory.Exists(destination))
            {
                Directory.Move(directory, destination);
            }
        }

        foreach (var file in Directory.EnumerateFiles(legacyPath))
        {
            var destination = Path.Combine(targetPath, Path.GetFileName(file));
            if (!File.Exists(destination))
            {
                File.Move(file, destination);
            }
        }

        if (!Directory.EnumerateFileSystemEntries(legacyPath).Any())
        {
            Directory.Delete(legacyPath, recursive: false);
        }
    }

    private static void MigrateLegacyMetadataFile(string legacyPath, string targetPath)
    {
        if (!File.Exists(legacyPath) || File.Exists(targetPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.Move(legacyPath, targetPath);
    }

    private static string[] Tokenize(string value)
    {
        return value
            .Split([' ', '\t', '\r', '\n', '.', ',', ';', ':', '!', '?', '/', '\\', '-', '_', '(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries)
            .Select(term => term.Trim().ToLowerInvariant())
            .Where(term => term.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int CountOccurrences(string content, string term)
    {
        var count = 0;
        var index = 0;
        while ((index = content.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += term.Length;
        }

        return count;
    }

    private static string BuildSnippet(string content, string[] terms)
    {
        var firstIndex = terms
            .Select(term => content.IndexOf(term, StringComparison.OrdinalIgnoreCase))
            .Where(index => index >= 0)
            .DefaultIfEmpty(0)
            .Min();

        var start = Math.Max(0, firstIndex - 120);
        var length = Math.Min(360, content.Length - start);
        return content.Substring(start, length).ReplaceLineEndings(" ").Trim();
    }

    private static async Task<string?> TryReadTextAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 2_000_000)
            {
                return null;
            }

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

    private static string NormalizeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var normalized = new string(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray());
        normalized = normalized.Trim('.', ' ', '/', '\\');
        return string.IsNullOrWhiteSpace(normalized) ? "default" : normalized;
    }

    private static string NormalizeFileName(string value)
    {
        var fileName = Path.GetFileName(value);
        return NormalizeSegment(fileName);
    }

    private static string NormalizeRepositoryPath(string value)
    {
        var normalized = value.Replace('\\', '/').Trim('/', ' ');
        var segments = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != "." && segment != "..")
            .Select(NormalizeSegment);

        var joined = string.Join('/', segments);
        if (string.IsNullOrWhiteSpace(joined))
        {
            return $"{WorkspaceLayout.InputsRootName}/{WorkspaceLayout.RepositoriesRootName}";
        }

        return joined;
    }

    private static string? TryReadGitRemote(string repositoryPath)
    {
        var configPath = Path.Combine(repositoryPath, ".git", "config");
        if (!File.Exists(configPath))
        {
            return null;
        }

        var lines = File.ReadAllLines(configPath);
        return lines
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("url =", StringComparison.OrdinalIgnoreCase))
            ?.Split('=', 2)[1]
            .Trim();
    }

    private static string? TryReadGitBranch(string repositoryPath)
    {
        var headPath = Path.Combine(repositoryPath, ".git", "HEAD");
        if (!File.Exists(headPath))
        {
            return null;
        }

        var head = File.ReadAllText(headPath).Trim();
        const string prefix = "ref: refs/heads/";
        return head.StartsWith(prefix, StringComparison.Ordinal)
            ? head[prefix.Length..]
            : null;
    }

    private static string? TryReadGitCommit(string repositoryPath)
    {
        var gitRoot = Path.Combine(repositoryPath, ".git");
        var headPath = Path.Combine(gitRoot, "HEAD");
        if (!File.Exists(headPath))
        {
            return null;
        }

        var head = File.ReadAllText(headPath).Trim();
        const string prefix = "ref: ";
        if (!head.StartsWith(prefix, StringComparison.Ordinal))
        {
            return head.Length >= 7 ? head : null;
        }

        var refPath = Path.Combine(gitRoot, head[prefix.Length..].Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(refPath)
            ? File.ReadAllText(refPath).Trim()
            : null;
    }

    private static string ToRelativeOrOriginal(string workspaceRoot, string sourcePath)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        return fullPath.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(workspaceRoot, fullPath)
            : sourcePath;
    }
}
