namespace UnityLocalCI.App;

/// <summary>
/// Acesso a disco com prazo, para uso na thread da janela.
///
/// Toda leitura da tela de configuracao pode cair num caminho de rede: a pasta
/// de destino costuma ser um compartilhamento, e um servidor desligado nao
/// responde "nao existe" — ele simplesmente nao responde. Um Directory.Exists
/// nesse caso fica preso por dezenas de segundos, e a janela junto, parecendo
/// que o programa travou.
///
/// Aqui a pergunta vai para uma thread do pool e a resposta tem prazo. Quando
/// o prazo estoura, a tela segue sem a informacao — que e o comportamento
/// certo: descobrir a versao do editor ou pre-selecionar uma pasta e
/// conveniencia, nunca requisito.
/// </summary>
internal static class BoundedIo
{
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Executa e devolve o resultado, ou o valor padrao se demorar demais ou
    /// lancar. A tarefa abandonada termina sozinha quando o sistema desistir;
    /// o que nao pode e a janela esperar por ela.
    /// </summary>
    public static T? Run<T>(Func<T?> work, TimeSpan? limit = null)
    {
        try
        {
            var task = Task.Run(work);
            return task.Wait(limit ?? Default) ? task.Result : default;
        }
        catch (AggregateException)
        {
            return default;
        }
    }
}
