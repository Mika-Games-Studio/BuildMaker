using System.Text;

namespace UnityLocalCI.Core.Pipeline;

/// <summary>
/// Log da build em arquivo, escrito em tempo real. Se a build travar e for
/// morta pelo timeout, o que ja saiu precisa estar gravado.
/// </summary>
public interface IBuildLogWriter : IDisposable
{
    string Path { get; }
    void Open(string path);
    void Write(string line);
}

public sealed class BuildLogWriter : IBuildLogWriter
{
    private readonly object _gate = new();
    private StreamWriter? _writer;

    public string Path { get; private set; } = "";

    public void Open(string path)
    {
        lock (_gate)
        {
            _writer?.Dispose();

            var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            Path = path;
            _writer = new StreamWriter(
                new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read),
                Encoding.UTF8)
            {
                AutoFlush = true,
            };
        }
    }

    public void Write(string line)
    {
        lock (_gate)
        {
            _writer?.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
