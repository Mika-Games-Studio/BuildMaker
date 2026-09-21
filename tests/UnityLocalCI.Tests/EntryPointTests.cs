using System.Reflection;
using UnityLocalCI.App;
using Xunit;

namespace UnityLocalCI.Tests;

/// <summary>
/// O ponto de entrada do executavel.
///
/// Este teste existe por causa de um travamento real: as caixas de selecao de
/// pasta e de arquivo do Windows sao objetos COM do shell, que so podem ser
/// chamados de uma thread STA. O programa rodava em MTA — porque o Main gerado
/// a partir de instrucoes de nivel superior usava 'await' e virava async, e um
/// Main async nao pode ser STA — e escolher uma pasta travava o aplicativo,
/// levando o shell junto.
///
/// Nada no compilador avisa quando isso volta a acontecer: basta alguem trocar
/// o Main por instrucoes de nivel superior de novo. Daí o teste.
/// </summary>
public class EntryPointTests
{
    private static MethodInfo EntryPoint =>
        typeof(MainForm).Assembly.EntryPoint
        ?? throw new InvalidOperationException("o executavel perdeu o ponto de entrada");

    [Fact]
    public void O_ponto_de_entrada_e_STA()
    {
        Assert.NotNull(EntryPoint.GetCustomAttribute<STAThreadAttribute>());
        Assert.Null(EntryPoint.GetCustomAttribute<MTAThreadAttribute>());
    }

    /// <summary>
    /// Um Main async nao pode carregar STAThread: o atributo ate compila, mas a
    /// continuacao depois do primeiro 'await' cai numa thread do pool, que e
    /// MTA. Entao o ponto de entrada tem que ser sincrono.
    /// </summary>
    [Fact]
    public void O_ponto_de_entrada_nao_e_async()
    {
        Assert.Null(EntryPoint.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>());
        Assert.Equal(typeof(int), EntryPoint.ReturnType);
    }
}
