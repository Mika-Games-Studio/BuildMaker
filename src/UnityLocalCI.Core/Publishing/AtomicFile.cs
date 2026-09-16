using System.Text;

namespace UnityLocalCI.Core.Publishing;

/// <summary>
/// Escrita de arquivo texto sem estado intermediario visivel.
///
/// A pasta de destino e a interface do sistema, e alguem pode abrir o
/// _STATUS.txt no exato instante em que ele esta sendo reescrito. Gravamos num
/// arquivo temporario ao lado e trocamos por rename, que e a mesma tatica do
/// zip com sufixo .part.
/// </summary>
public static class AtomicFile
{
    /// <summary>UTF-8 com BOM: os arquivos tem acento e sao abertos por ferramentas variadas no Windows.</summary>
    private static readonly UTF8Encoding Encoding = new(encoderShouldEmitUTF8Identifier: true);

    public static async Task WriteAllTextAsync(string path, string content, CancellationToken ct)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        // Nome unico por escrita: dois escritores concorrentes no mesmo destino
        // nao podem disputar o mesmo temporario, senao um apagaria o arquivo que
        // o outro ainda esta gravando.
        var temporary = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";

        try
        {
            await File.WriteAllTextAsync(temporary, content, Encoding, ct).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { /* limpeza best effort */ }
        catch (UnauthorizedAccessException) { /* idem */ }
    }
}
