using System.ComponentModel;
using System.Drawing.Design;

namespace UnityLocalCI.App;

/// <summary>
/// Caixa de selecao de pasta no lugar de um campo de texto.
///
/// Caminho digitado a mao e uma das poucas coisas nesta configuracao que so
/// falha depois — na hora do clone, do build ou da copia, quando ja e tarde. A
/// caixa do Windows so devolve pasta que existe.
/// </summary>
public sealed class FolderPathEditor : UITypeEditor
{
    public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext? context)
        => UITypeEditorEditStyle.Modal;

    public override object? EditValue(ITypeDescriptorContext? context, IServiceProvider provider, object? value)
    {
        using var dialog = new FolderBrowserDialog
        {
            UseDescriptionForTitle = true,
            Description = context?.PropertyDescriptor?.DisplayName ?? "Escolha a pasta",
            ShowNewFolderButton = true,
        };

        // Uma pasta que ainda nao existe seria recusada pela caixa; neste caso
        // ela abre onde estiver mais perto do que ja foi digitado.
        if (value is string atual && atual.Length > 0)
        {
            var existente = PrimeiraPastaExistente(atual);
            if (existente is not null) dialog.SelectedPath = existente;
        }

        return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : value;
    }

    internal static string? PrimeiraPastaExistente(string caminho)
    {
        try
        {
            var atual = Path.GetFullPath(caminho);

            while (!string.IsNullOrEmpty(atual))
            {
                if (Directory.Exists(atual)) return atual;
                atual = Path.GetDirectoryName(atual);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }

        return null;
    }
}

/// <summary>
/// Caixa de selecao de arquivo. Aceita nome que ainda nao existe: o arquivo de
/// gatilho e o de status geral sao criados depois, pelo proprio servico.
/// </summary>
public sealed class FilePathEditor : UITypeEditor
{
    public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext? context)
        => UITypeEditorEditStyle.Modal;

    public override object? EditValue(ITypeDescriptorContext? context, IServiceProvider provider, object? value)
    {
        using var dialog = new OpenFileDialog
        {
            Title = context?.PropertyDescriptor?.DisplayName ?? "Escolha o arquivo",
            CheckFileExists = false,
            CheckPathExists = false,
            Filter = "Arquivo de texto (*.txt)|*.txt|Todos os arquivos (*.*)|*.*",
        };

        if (value is string atual && atual.Length > 0)
        {
            dialog.FileName = Path.GetFileName(atual);

            var pasta = FolderPathEditor.PrimeiraPastaExistente(atual);
            if (pasta is not null) dialog.InitialDirectory = pasta;
        }

        return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : value;
    }
}
