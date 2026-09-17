using System.Reflection;

namespace UnityLocalCI.App;

/// <summary>
/// O icone do aplicativo, embutido no assembly.
///
/// Embutido, e nao lido do executavel por ExtractAssociatedIcon: rodando por
/// 'dotnet run' o processo e o dotnet.exe, e o icone extraido seria o dele.
/// </summary>
public static class AppIcon
{
    private const string ResourceName = "UnityLocalCI.App.unitylocalci.ico";

    private static Icon? _cached;

    public static Icon Load()
    {
        if (_cached is not null) return _cached;

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);

        // Se o recurso sumir num refactor, um icone generico e melhor que a
        // janela nao abrir.
        _cached = stream is null ? SystemIcons.Application : new Icon(stream);
        return _cached;
    }

    /// <summary>Versao no tamanho que a bandeja pede, para nao sair borrada.</summary>
    public static Icon LoadForTray()
    {
        var size = SystemInformation.SmallIconSize;

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        return stream is null ? SystemIcons.Application : new Icon(stream, size);
    }

    private static Bitmap? _mark;

    /// <summary>
    /// A marca desenhada no topo da coluna de navegacao. Vem do quadro de 32
    /// pixels e nao do de 16: reduzir um desenho grande sai limpo, ampliar um
    /// pequeno sai serrilhado.
    /// </summary>
    public static Image LoadMark()
    {
        if (_mark is not null) return _mark;

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);

        // SystemIcons.Application e compartilhado pelo processo inteiro: usar,
        // sim; descartar, nunca.
        var icon = stream is null ? SystemIcons.Application : new Icon(stream, 32, 32);
        _mark = icon.ToBitmap();
        if (stream is not null) icon.Dispose();

        return _mark;
    }
}
