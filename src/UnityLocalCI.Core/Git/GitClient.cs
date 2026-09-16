using System.Text;
using Microsoft.Extensions.Logging;
using UnityLocalCI.Core.Abstractions;

namespace UnityLocalCI.Core.Git;

public sealed class GitClient : IGitClient
{
    private const string RedactedHeader = "http.extraHeader=Authorization: Basic <REDACTED>";

    /// <summary>Separador de campos do 'git log --format'. Nao aparece em nome de autor nem em assunto de commit.</summary>
    private const char FieldSeparator = (char)0x1F;

    private readonly IProcessRunner _runner;
    private readonly ILogger<GitClient> _logger;

    public GitClient(IProcessRunner runner, ILogger<GitClient> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public async Task<SyncOutcome> EnsureWorkspaceAsync(GitContext context, CancellationToken ct)
    {
        if (Directory.Exists(Path.Combine(context.WorkspacePath, ".git")))
            return new SyncOutcome(Cloned: false);

        _logger.LogInformation(
            "Workspace {Workspace} ainda nao existe. Clonando; esta primeira build sera lenta porque nao ha cache da Library.",
            context.WorkspacePath);

        var parent = Path.GetDirectoryName(context.WorkspacePath.TrimEnd(Path.DirectorySeparatorChar));
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        await RunAsync(context, workingDirectory: parent ?? ".", ct,
            "clone", "--branch", context.Branch, context.RepositoryUrl, context.WorkspacePath);

        return new SyncOutcome(Cloned: true);
    }

    public Task FetchAsync(GitContext context, CancellationToken ct)
        => RunAsync(context, context.WorkspacePath, ct, "fetch", "origin", context.Branch, "--prune");

    public async Task<string> GetRemoteHeadShaAsync(GitContext context, CancellationToken ct)
    {
        var result = await RunAsync(context, context.WorkspacePath, ct, "rev-parse", $"origin/{context.Branch}");
        return result.StandardOutput.Trim();
    }

    public async Task<CommitInfo> GetCommitInfoAsync(GitContext context, string sha, CancellationToken ct)
    {
        var result = await RunAsync(context, context.WorkspacePath, ct,
            "log", "-1", "--format=%H%x1f%an%x1f%s", sha);

        var parts = result.StandardOutput.Trim().Split(FieldSeparator);
        return parts.Length >= 3
            ? new CommitInfo(parts[0], parts[1], parts[2])
            : new CommitInfo(sha, "(desconhecido)", "(sem mensagem)");
    }

    public async Task CheckoutAsync(GitContext context, string sha, CancellationToken ct)
    {
        await RunAsync(context, context.WorkspacePath, ct, "reset", "--hard", sha);

        var cleanArgs = GitCleanArguments.Build();
        await RunAsync(context, context.WorkspacePath, ct, cleanArgs.ToArray());

        if (UsesLfs(context.WorkspacePath))
            await RunAsync(context, context.WorkspacePath, ct, "lfs", "pull");
    }

    private static bool UsesLfs(string workspacePath)
    {
        var attributes = Path.Combine(workspacePath, ".gitattributes");
        return File.Exists(attributes)
            && File.ReadAllText(attributes).Contains("filter=lfs", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ProcessResult> RunAsync(
        GitContext context, string workingDirectory, CancellationToken ct, params string[] args)
    {
        var (real, display) = WithAuthentication(context, args);

        var request = new ProcessRequest
        {
            FileName = "git",
            Arguments = real,
            DisplayArguments = display,
            WorkingDirectory = workingDirectory,
            Environment = new Dictionary<string, string>
            {
                // Sem prompt interativo: numa conta de servico um prompt de
                // credencial trava a build ate o timeout sem dizer por que.
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GCM_INTERACTIVE"] = "never",
            },
        };

        var result = await _runner.RunAsync(request, onOutput: null, ct).ConfigureAwait(false);

        if (!result.Success)
        {
            throw new GitCommandException(
                $"git {string.Join(' ', args)} falhou com exit code {result.ExitCode}.",
                result.ExitCode,
                Redact(result.StandardError, context.PersonalAccessToken));
        }

        return result;
    }

    /// <summary>
    /// O PAT vai por '-c http.extraHeader' na invocacao, nunca na URL do remote
    /// nem gravado em .git/config, para que ele nao sobreviva ao processo.
    /// </summary>
    private static (string[] Real, string[] Display) WithAuthentication(GitContext context, string[] args)
    {
        if (string.IsNullOrEmpty(context.PersonalAccessToken))
            return (args, args);

        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($":{context.PersonalAccessToken}"));
        var real = new[] { "-c", $"http.extraHeader=Authorization: Basic {basic}" }.Concat(args).ToArray();
        var display = new[] { "-c", RedactedHeader }.Concat(args).ToArray();
        return (real, display);
    }

    private static string Redact(string text, string? secret)
        => string.IsNullOrEmpty(secret) ? text : text.Replace(secret, "<REDACTED>", StringComparison.Ordinal);
}

public sealed class GitCommandException : Exception
{
    public GitCommandException(string message, int exitCode, string stderr) : base(message)
    {
        ExitCode = exitCode;
        StandardError = stderr;
    }

    public int ExitCode { get; }
    public string StandardError { get; }
}
