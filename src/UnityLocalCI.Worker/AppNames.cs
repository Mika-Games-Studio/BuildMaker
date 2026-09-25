namespace UnityLocalCI.App;

/// <summary>
/// Os nomes do programa, separados por quem os le.
///
/// A distincao importa: <see cref="Display"/> e o que aparece para quem usa, e
/// pode mudar quando a marca mudar. Os outros sao identidade — nome do servico
/// do Windows, valor no registro, nome da credencial no cofre — e mudar
/// qualquer um deles quebra instalacoes que ja existem. Por isso eles nao estao
/// aqui: ficam onde sao usados, com o comentario de sempre.
/// </summary>
internal static class AppNames
{
    /// <summary>O nome que aparece na janela, na bandeja e nos avisos.</summary>
    public const string Display = "BuildMaker";

    /// <summary>O que o programa e, em tres palavras, debaixo da assinatura.</summary>
    public const string Descriptor = "Unity Local CI";

    /// <summary>A metade em peso cheio da assinatura.</summary>
    public const string MarkStrong = "Build";

    /// <summary>A metade em peso normal.</summary>
    public const string MarkSoft = "Maker";

    /// <summary>Quem assina o programa, no pe da coluna de navegacao.</summary>
    public const string Copyright = "© Mika Games Studio";
}
