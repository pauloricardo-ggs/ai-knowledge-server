using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KnowledgeServer.Application;

namespace KnowledgeServer.Infrastructure;

public sealed partial class DeterministicCodeIntelligenceService(IWorkspaceStore workspaceStore) : ICodeIntelligenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<CodeIntelligenceResult> GenerateAsync(
        CodeIntelligenceRequest request,
        CancellationToken cancellationToken)
    {
        var workspace = await workspaceStore.GetOrCreateWorkspaceAsync(request.WorkspaceId, cancellationToken);
        var repositoryRoot = WorkspaceLayout.RepositoriesRoot(workspace.RootPath);
        var outputRoot = WorkspaceLayout.RoslynRoot(workspace.RootPath);
        Directory.CreateDirectory(repositoryRoot);
        Directory.CreateDirectory(outputRoot);

        var files = Directory
            .EnumerateFiles(repositoryRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsIgnoredPath(repositoryRoot, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var parsedFiles = new List<ParsedCSharpFile>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var content = await TryReadTextAsync(file, cancellationToken);
            if (content is null)
            {
                continue;
            }

            parsedFiles.Add(ParseFile(repositoryRoot, file, content));
        }

        var symbols = parsedFiles.SelectMany(file => file.Symbols).ToArray();
        var artifacts = new List<string>
        {
            await WriteArtifactAsync(outputRoot, "symbols.json", symbols, cancellationToken)
        };

        if (request.IncludeReferences)
        {
            artifacts.Add(await WriteArtifactAsync(
                outputRoot,
                "references.json",
                BuildReferences(parsedFiles, symbols),
                cancellationToken));
        }

        if (request.IncludeCallGraph)
        {
            artifacts.Add(await WriteArtifactAsync(
                outputRoot,
                "callgraph.json",
                BuildCallGraph(parsedFiles, symbols),
                cancellationToken));
        }

        if (request.IncludeEndpoints)
        {
            artifacts.Add(await WriteArtifactAsync(
                outputRoot,
                "endpoints.json",
                parsedFiles.SelectMany(file => file.Endpoints).ToArray(),
                cancellationToken));
        }

        if (request.IncludeRelatedTests)
        {
            artifacts.Add(await WriteArtifactAsync(
                outputRoot,
                "related-tests.json",
                BuildRelatedTests(symbols),
                cancellationToken));
        }

        var manifest = new
        {
            request.WorkspaceId,
            repositoryRoot,
            outputRoot,
            analyzer = "deterministic-csharp-syntax",
            generatedAt = DateTimeOffset.UtcNow,
            fileCount = parsedFiles.Count,
            symbolCount = symbols.Length,
            artifacts
        };
        artifacts.Add(await WriteArtifactAsync(outputRoot, "manifest.json", manifest, cancellationToken));

        return new CodeIntelligenceResult(
            workspace.Id,
            repositoryRoot,
            outputRoot,
            "deterministic-csharp-syntax",
            manifest.generatedAt,
            parsedFiles.Count,
            symbols.Length,
            artifacts);
    }

    private static ParsedCSharpFile ParseFile(string repositoryRoot, string path, string content)
    {
        var relativePath = Path.GetRelativePath(repositoryRoot, path);
        var lines = content.ReplaceLineEndings("\n").Split('\n');
        var namespaceName = ExtractNamespace(content);
        var symbols = new List<CodeSymbol>();
        var endpoints = new List<HttpEndpoint>();

        var currentType = "";
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var lineNumber = index + 1;
            var typeMatch = TypeDeclarationRegex().Match(line);
            if (typeMatch.Success)
            {
                currentType = typeMatch.Groups["name"].Value;
                symbols.Add(new CodeSymbol(
                    BuildFullName(namespaceName, currentType),
                    currentType,
                    typeMatch.Groups["kind"].Value,
                    relativePath,
                    lineNumber,
                    namespaceName,
                    null,
                    IsTestFile(relativePath)));
            }

            var methodMatch = MethodDeclarationRegex().Match(line);
            if (methodMatch.Success && !ControlKeywords.Contains(methodMatch.Groups["name"].Value))
            {
                var methodName = methodMatch.Groups["name"].Value;
                symbols.Add(new CodeSymbol(
                    BuildFullName(namespaceName, currentType, methodName),
                    methodName,
                    "method",
                    relativePath,
                    lineNumber,
                    namespaceName,
                    string.IsNullOrWhiteSpace(currentType) ? null : currentType,
                    IsTestFile(relativePath) || HasTestAttribute(lines, index)));
            }

            foreach (Match endpointMatch in MinimalApiEndpointRegex().Matches(line))
            {
                endpoints.Add(new HttpEndpoint(
                    endpointMatch.Groups["verb"].Value.ToUpperInvariant(),
                    endpointMatch.Groups["route"].Value,
                    relativePath,
                    lineNumber,
                    "minimal-api",
                    currentType.Length == 0 ? null : BuildFullName(namespaceName, currentType)));
            }

            var routeAttribute = RouteAttributeRegex().Match(line);
            if (routeAttribute.Success)
            {
                var verb = routeAttribute.Groups["verb"].Value;
                var route = routeAttribute.Groups["route"].Value;
                endpoints.Add(new HttpEndpoint(
                    string.IsNullOrWhiteSpace(verb) ? "ROUTE" : verb.ToUpperInvariant(),
                    route,
                    relativePath,
                    lineNumber,
                    "attribute",
                    currentType.Length == 0 ? null : BuildFullName(namespaceName, currentType)));
            }
        }

        return new ParsedCSharpFile(relativePath, content, lines, symbols, endpoints);
    }

    private static ReferenceEdge[] BuildReferences(IEnumerable<ParsedCSharpFile> files, IReadOnlyCollection<CodeSymbol> symbols)
    {
        var indexedSymbols = symbols
            .Where(symbol => symbol.Name.Length >= 3)
            .GroupBy(symbol => symbol.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var references = new List<ReferenceEdge>();
        foreach (var file in files)
        {
            foreach (var symbolGroup in indexedSymbols)
            {
                foreach (Match match in Regex.Matches(file.Content, $@"\b{Regex.Escape(symbolGroup.Key)}\b"))
                {
                    var line = CountLine(file.Content, match.Index);
                    foreach (var target in symbolGroup.Value.Where(symbol => symbol.FilePath != file.RelativePath || symbol.Line != line))
                    {
                        references.Add(new ReferenceEdge(
                            file.RelativePath,
                            line,
                            symbolGroup.Key,
                            target.FullName,
                            target.Kind));
                    }
                }
            }
        }

        return references
            .OrderBy(reference => reference.SourceFile, StringComparer.OrdinalIgnoreCase)
            .ThenBy(reference => reference.SourceLine)
            .ThenBy(reference => reference.TargetSymbol, StringComparer.Ordinal)
            .ToArray();
    }

    private static CallGraphEdge[] BuildCallGraph(IEnumerable<ParsedCSharpFile> files, IReadOnlyCollection<CodeSymbol> symbols)
    {
        var methodsByName = symbols
            .Where(symbol => symbol.Kind == "method")
            .GroupBy(symbol => symbol.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var edges = new List<CallGraphEdge>();
        foreach (var file in files)
        {
            for (var index = 0; index < file.Lines.Length; index++)
            {
                var method = file.Symbols
                    .Where(symbol => symbol.Kind == "method")
                    .LastOrDefault(symbol => symbol.Line <= index + 1);
                if (method is null)
                {
                    continue;
                }

                foreach (Match invocation in InvocationRegex().Matches(file.Lines[index]))
                {
                    var calledName = invocation.Groups["name"].Value;
                    if (!methodsByName.TryGetValue(calledName, out var targets))
                    {
                        continue;
                    }

                    foreach (var target in targets.Where(target => target.FullName != method.FullName))
                    {
                        edges.Add(new CallGraphEdge(method.FullName, target.FullName, file.RelativePath, index + 1));
                    }
                }
            }
        }

        return edges
            .DistinctBy(edge => $"{edge.Caller}|{edge.Callee}|{edge.FilePath}|{edge.Line}")
            .OrderBy(edge => edge.Caller, StringComparer.Ordinal)
            .ThenBy(edge => edge.Callee, StringComparer.Ordinal)
            .ToArray();
    }

    private static RelatedTests[] BuildRelatedTests(IReadOnlyCollection<CodeSymbol> symbols)
    {
        var tests = symbols.Where(symbol => symbol.IsTest).ToArray();
        var production = symbols.Where(symbol => !symbol.IsTest).ToArray();

        return production
            .Select(symbol => new RelatedTests(
                symbol.FullName,
                tests
                    .Where(test => test.Name.Contains(symbol.Name, StringComparison.OrdinalIgnoreCase)
                        || test.FullName.Contains(symbol.Name, StringComparison.OrdinalIgnoreCase)
                        || test.FilePath.Contains(symbol.Name, StringComparison.OrdinalIgnoreCase))
                    .Select(test => test.FullName)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray()))
            .Where(item => item.Tests.Count > 0)
            .OrderBy(item => item.Symbol, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<string> WriteArtifactAsync<T>(
        string outputRoot,
        string fileName,
        T value,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(outputRoot, fileName);
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(value, JsonOptions),
            Encoding.UTF8,
            cancellationToken);
        return path;
    }

    private static string ExtractNamespace(string content)
    {
        var fileScoped = FileScopedNamespaceRegex().Match(content);
        if (fileScoped.Success)
        {
            return fileScoped.Groups["namespace"].Value;
        }

        var blockScoped = BlockScopedNamespaceRegex().Match(content);
        return blockScoped.Success ? blockScoped.Groups["namespace"].Value : "";
    }

    private static bool HasTestAttribute(IReadOnlyList<string> lines, int methodLineIndex)
    {
        var start = Math.Max(0, methodLineIndex - 4);
        for (var index = start; index < methodLineIndex; index++)
        {
            if (TestAttributeRegex().IsMatch(lines[index]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIgnoredPath(string repositoryRoot, string path)
    {
        var relative = Path.GetRelativePath(repositoryRoot, path);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment => segment is ".git" or "bin" or "obj" or "node_modules");
    }

    private static bool IsTestFile(string relativePath)
    {
        return relativePath.Contains("test", StringComparison.OrdinalIgnoreCase)
            || relativePath.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildFullName(string namespaceName, string typeName, string? memberName = null)
    {
        var typeFullName = string.IsNullOrWhiteSpace(namespaceName) ? typeName : $"{namespaceName}.{typeName}";
        return string.IsNullOrWhiteSpace(memberName) ? typeFullName : $"{typeFullName}.{memberName}";
    }

    private static int CountLine(string content, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                line++;
            }
        }

        return line;
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

    private static readonly HashSet<string> ControlKeywords = new(StringComparer.Ordinal)
    {
        "if",
        "for",
        "foreach",
        "while",
        "switch",
        "catch",
        "using",
        "lock"
    };

    [GeneratedRegex(@"^\s*(?:namespace\s+)(?<namespace>[A-Za-z_][\w.]*)\s*;", RegexOptions.Compiled)]
    private static partial Regex FileScopedNamespaceRegex();

    [GeneratedRegex(@"^\s*(?:namespace\s+)(?<namespace>[A-Za-z_][\w.]*)\s*\{", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex BlockScopedNamespaceRegex();

    [GeneratedRegex(@"\b(?<kind>class|record|struct|interface|enum)\s+(?<name>[A-Za-z_]\w*)", RegexOptions.Compiled)]
    private static partial Regex TypeDeclarationRegex();

    [GeneratedRegex(@"^\s*(?:public|private|protected|internal|static|async|virtual|override|sealed|partial|\s)+\s*(?:[\w<>\[\],.?]+\s+)+(?<name>[A-Za-z_]\w*)\s*\(", RegexOptions.Compiled)]
    private static partial Regex MethodDeclarationRegex();

    [GeneratedRegex(@"\bMap(?<verb>Get|Post|Put|Patch|Delete)\s*\(\s*""(?<route>[^""]*)""", RegexOptions.Compiled)]
    private static partial Regex MinimalApiEndpointRegex();

    [GeneratedRegex(@"\[(?:(?<verb>HttpGet|HttpPost|HttpPut|HttpPatch|HttpDelete)|Route)(?:\s*\(\s*""(?<route>[^""]*)""\s*\))?\]", RegexOptions.Compiled)]
    private static partial Regex RouteAttributeRegex();

    [GeneratedRegex(@"\[(Fact|Theory|Test|TestMethod)\b", RegexOptions.Compiled)]
    private static partial Regex TestAttributeRegex();

    [GeneratedRegex(@"\b(?<name>[A-Za-z_]\w*)\s*\(", RegexOptions.Compiled)]
    private static partial Regex InvocationRegex();

    private sealed record ParsedCSharpFile(
        string RelativePath,
        string Content,
        string[] Lines,
        IReadOnlyCollection<CodeSymbol> Symbols,
        IReadOnlyCollection<HttpEndpoint> Endpoints);

    private sealed record CodeSymbol(
        string FullName,
        string Name,
        string Kind,
        string FilePath,
        int Line,
        string Namespace,
        string? ContainingType,
        bool IsTest);

    private sealed record ReferenceEdge(
        string SourceFile,
        int SourceLine,
        string Text,
        string TargetSymbol,
        string TargetKind);

    private sealed record CallGraphEdge(
        string Caller,
        string Callee,
        string FilePath,
        int Line);

    private sealed record HttpEndpoint(
        string Verb,
        string Route,
        string FilePath,
        int Line,
        string Source,
        string? Handler);

    private sealed record RelatedTests(
        string Symbol,
        IReadOnlyCollection<string> Tests);
}
