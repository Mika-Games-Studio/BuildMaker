namespace UnityLocalCI.Core.Pipeline;

/// <summary>
/// Log da build, escrito em tempo real — em memoria, nao em disco.
/// Ver <see cref="BuildLogBuffer"/> para o porque.
/// </summary>
public interface IBuildLogWriter
{
    void Open(long buildId);
    void Write(string line);
}

/// <summary>
/// O carimbo de hora no comeco de cada linha do log de build.
///
/// Fica aqui, e nao escondido no writer, porque quem le o arquivo precisa saber
/// que os oito primeiros caracteres nao sao do Unity — e porque a janela usa
/// exatamente esta medida para separar a coluna de hora da mensagem.
/// </summary>
public static class BuildLogStamp
{
    /// <summary>Formato do carimbo: hora local, sem data.</summary>
    public const string Format = "HH:mm:ss";

    /// <summary>Quantos caracteres o carimbo ocupa, contando o separador.</summary>
    public const int Length = 8 + 2;

    public static string Prefix(DateTimeOffset at) => at.ToLocalTime().ToString(Format) + "  ";

    /// <summary>
    /// Separa o carimbo do resto. Linha sem carimbo devolve hora vazia — pode
    /// acontecer com log de uma versao anterior, e um log antigo nao pode virar
    /// uma tela quebrada.
    /// </summary>
    public static (string Hora, string Resto) Split(string linha)
    {
        if (linha.Length < Length) return ("", linha);

        var hora = linha[..8];

        // "12:34:56" e so isso: dois digitos, dois pontos, e assim por diante.
        if (hora[2] != ':' || hora[5] != ':') return ("", linha);
        for (var i = 0; i < 8; i++)
            if (i != 2 && i != 5 && !char.IsAsciiDigit(hora[i])) return ("", linha);

        return (hora, linha[Length..]);
    }
}

public sealed class BuildLogWriter : IBuildLogWriter
{
    private readonly BuildLogBuffer _buffer;
    private long _buildId;

    public BuildLogWriter(BuildLogBuffer buffer) => _buffer = buffer;

    public void Open(long buildId)
    {
        _buildId = buildId;
        _buffer.Comecar(buildId);
    }

    /// <summary>
    /// Registra a linha com a hora na frente.
    ///
    /// Vale para tudo, inclusive para a saida crua do Unity. Uma build WebGL
    /// leva quinze minutos e despeja dezessete mil linhas; sem a hora em cada
    /// uma, o log nao responde a unica pergunta que se faz a um log de build
    /// demorada — onde foram os quinze minutos.
    /// </summary>
    public void Write(string line)
        => _buffer.Escrever(_buildId, BuildLogStamp.Prefix(DateTimeOffset.Now) + line);
}
