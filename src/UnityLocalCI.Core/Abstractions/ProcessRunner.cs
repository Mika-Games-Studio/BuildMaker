using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace UnityLocalCI.Core.Abstractions;

public sealed class ProcessRunner : IProcessRunner
{
    private readonly ILogger<ProcessRunner> _logger;

    public ProcessRunner(ILogger<ProcessRunner> logger) => _logger = logger;

    public async Task<ProcessResult> RunAsync(
        ProcessRequest request,
        Action<OutputStream, string>? onOutput,
        CancellationToken ct)
    {
        var info = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in request.Arguments)
            info.ArgumentList.Add(argument);

        if (request.Environment is not null)
            foreach (var (key, value) in request.Environment)
                info.Environment[key] = value;

        _logger.LogDebug("Executando: {CommandLine}", request.SafeCommandLine);

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) { stdoutDone.TrySetResult(); return; }
            stdout.AppendLine(e.Data);
            onOutput?.Invoke(OutputStream.StandardOutput, e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) { stderrDone.TrySetResult(); return; }
            stderr.AppendLine(e.Data);
            onOutput?.Invoke(OutputStream.StandardError, e.Data);
        };

        using var job = OperatingSystem.IsWindows() ? new WindowsJobObject() : null;

        process.Start();
        // A associacao acontece logo apos o start; filhos criados a partir daqui
        // herdam o job e morrem junto, mesmo que reparenteiem.
        //
        // O teste do sistema esta repetido aqui de proposito: 'job' so e criado no
        // Windows, mas o analisador de plataforma nao consegue seguir essa
        // garantia por dentro de um local. Sem o if ele acusa CA1416, que neste
        // projeto e erro de compilacao.
        if (OperatingSystem.IsWindows()) job?.Assign(process.Handle);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        if (request.Timeout is { } timeout)
            timeoutCts.CancelAfter(timeout);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested;
            KillTree(process, job, timedOut, request);

            // Deixa o processo terminar de morrer para nao vazar handle.
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (InvalidOperationException) { /* ja saiu */ }

            if (!timedOut) ct.ThrowIfCancellationRequested();
        }

        // Espera os readers drenarem, com teto: se um filho herdou o pipe e ficou
        // vivo, o EOF nunca chega e nao vale travar a build por isso.
        await Task.WhenAny(
            Task.WhenAll(stdoutDone.Task, stderrDone.Task),
            Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false);

        var exitCode = timedOut ? -1 : process.ExitCode;
        return new ProcessResult(exitCode, timedOut, stdout.ToString(), stderr.ToString());
    }

    private void KillTree(Process process, WindowsJobObject? job, bool timedOut, ProcessRequest request)
    {
        var reason = timedOut ? "timeout" : "cancelamento";
        _logger.LogWarning("Encerrando arvore de processos por {Reason}: {CommandLine}", reason, request.SafeCommandLine);

        try
        {
            if (OperatingSystem.IsWindows()) job?.TerminateAll();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao encerrar o Job Object; caindo para Process.Kill em arvore.");
        }

        // No Linux nao ha job object, e o Process.Kill(entireProcessTree) logo
        // abaixo e o que sobra. Ele percorre a arvore pelo PPID, entao um neto
        // que ja tenha sido reparenteado para o init escapa — no Windows o job
        // pega esse caso. E a diferenca conhecida entre os dois lados.

        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* ja saiu */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao encerrar o processo {ProcessName}.", request.FileName);
        }
    }
}
