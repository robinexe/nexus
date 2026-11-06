namespace Nexus.Desktop.ViewModels;

/// <summary>Tipo de bloco de um aviso do NEXUS: parágrafo, item de lista ou a pergunta final ("Começar agora?").</summary>
public enum DialogBlockKind { Paragraph, Bullet, Question }

/// <summary>Um bloco do texto do aviso. <paramref name="Detail"/>: o "[administrador; persistente]" do fim de um item, em letra menor.</summary>
public sealed record DialogBlock(DialogBlockKind Kind, string Text, string? Detail = null);

/// <summary>Tom do aviso: decide ícone, cor e o estilo do botão principal.</summary>
public enum DialogTone { Info, Warning, Danger, Error }

/// <summary>
/// Texto dos avisos (o mesmo que ia para a caixa do Windows) quebrado em blocos para o aviso do NEXUS: parágrafos, listas com
/// "•" e a pergunta final. Puro e testado: nenhuma tela precisa mudar o texto que já monta.
/// </summary>
public static class DialogText
{
    private static readonly string[] BulletMarks = ["• ", "- ", "* ", "– ", "· "];

    /// <summary>A partir deste número de itens a lista fica compacta (letra e espaço menores) para caber na página.</summary>
    public const int CompactFrom = 7;

    public static IReadOnlyList<DialogBlock> Parse(string? message)
    {
        var blocks = new List<DialogBlock>();
        if (string.IsNullOrWhiteSpace(message)) return blocks;
        var paragraph = new List<string>();
        void Flush()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(new DialogBlock(DialogBlockKind.Paragraph, string.Join("\n", paragraph)));
            paragraph.Clear();
        }

        foreach (var raw in message.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0) { Flush(); continue; }
            if (BulletMarks.FirstOrDefault(m => trimmed.StartsWith(m, StringComparison.Ordinal)) is { } mark)
            {
                Flush();
                blocks.Add(Bullet(trimmed[mark.Length..].Trim()));
                continue;
            }
            // Linha recuada logo depois de um item: continuação dele (texto longo quebrado à mão).
            if (paragraph.Count == 0 && line.Length > trimmed.Length && blocks.Count > 0 && blocks[^1].Kind == DialogBlockKind.Bullet)
            {
                var last = blocks[^1];
                blocks[^1] = last with { Text = last.Text + " " + trimmed };
                continue;
            }
            paragraph.Add(trimmed);
        }
        Flush();

        // A última frase que pergunta ("Começar agora?") vira a pergunta, em destaque perto dos botões.
        if (blocks.Count > 1 && blocks[^1] is { Kind: DialogBlockKind.Paragraph } q && q.Text.EndsWith('?') && !q.Text.Contains('\n', StringComparison.Ordinal))
            blocks[^1] = q with { Kind = DialogBlockKind.Question };
        return blocks;
    }

    private static DialogBlock Bullet(string text)
    {
        // "Título → valor  [administrador; persistente]": o colchete final vira detalhe em letra menor.
        if (text.EndsWith(']') && text.LastIndexOf('[') is var open and > 0)
            return new DialogBlock(DialogBlockKind.Bullet, text[..open].TrimEnd(), text[(open + 1)..^1].Trim());
        return new DialogBlock(DialogBlockKind.Bullet, text);
    }

    /// <summary>Lista longa: o aviso usa a versão compacta.</summary>
    public static bool IsCompact(IReadOnlyList<DialogBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        return blocks.Count(b => b.Kind == DialogBlockKind.Bullet) >= CompactFrom;
    }

    /// <summary>
    /// Ação sem volta (apagar de vez, exclusão segura, esvaziar lixeira/quarentena): o botão principal fica vermelho e o foco
    /// começa em "Cancelar".
    /// </summary>
    public static DialogTone ToneFor(string? title, string? message)
    {
        var all = ((title ?? "") + "\n" + (message ?? "")).ToLowerInvariant();
        string[] danger = ["irreversível", "irreversivel", "não tem volta", "nao tem volta", "não dá para desfazer", "não pode ser desfeit",
                           "sem volta", "apagar de vez", "irreversible", "cannot be undone", "can't be undone", "no se puede deshacer", "no tiene vuelta"];
        return danger.Any(d => all.Contains(d, StringComparison.Ordinal)) ? DialogTone.Danger : DialogTone.Warning;
    }
}
