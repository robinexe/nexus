using Nexus.Desktop.ViewModels;

namespace Nexus.Tests;

/// <summary>
/// Avisos do NEXUS (Views/Shell/NexusDialog) no lugar da caixa de mensagem do Windows: o texto que as telas já montam vira
/// blocos (parágrafo, lista, pergunta), listas longas ficam compactas e ações sem volta ganham o tom de perigo.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DialogTextTests
{
    [Fact]
    public void Confirmation_text_becomes_paragraphs_bullets_and_the_final_question()
    {
        var blocks = DialogText.Parse("O NEXUS vai:\n\n• Limpar arquivos temporários (vão para a quarentena e podem\n  voltar)\n• Ajustar o Windows para jogos\n\nSó entra o que é de risco baixo.\nO Windows vai pedir permissão.\n\nComeçar agora?");
        Assert.Equal(
            [DialogBlockKind.Paragraph, DialogBlockKind.Bullet, DialogBlockKind.Bullet, DialogBlockKind.Paragraph, DialogBlockKind.Question],
            blocks.Select(b => b.Kind));
        Assert.Equal("Limpar arquivos temporários (vão para a quarentena e podem voltar)", blocks[1].Text); // continuação recuada junta
        Assert.Equal("Só entra o que é de risco baixo.\nO Windows vai pedir permissão.", blocks[3].Text);
        Assert.Equal("Começar agora?", blocks[4].Text);
    }

    [Fact]
    public void Bracketed_flags_at_the_end_of_an_item_become_a_smaller_detail()
    {
        var b = Assert.Single(DialogText.Parse("• Desligar o MPO → 5  [administrador; persistente]"));
        Assert.Equal("Desligar o MPO → 5", b.Text);
        Assert.Equal("administrador; persistente", b.Detail);
    }

    [Fact]
    public void Windows_line_endings_empty_text_and_a_single_question_are_safe()
    {
        Assert.Empty(DialogText.Parse(null));
        Assert.Empty(DialogText.Parse("  \r\n "));
        Assert.Equal(2, DialogText.Parse("a\r\n\r\nb").Count);
        // Uma pergunta sozinha continua parágrafo (é o texto todo, não um rodapé).
        Assert.Equal(DialogBlockKind.Paragraph, Assert.Single(DialogText.Parse("Abrir esta página no navegador?")).Kind);
    }

    [Fact]
    public void Long_lists_are_compact_so_the_buttons_stay_on_screen()
    {
        var shortList = DialogText.Parse(string.Join("\n", Enumerable.Range(1, DialogText.CompactFrom - 1).Select(i => $"• item {i}")));
        var longList = DialogText.Parse(string.Join("\n", Enumerable.Range(1, 40).Select(i => $"• item {i}")));
        Assert.False(DialogText.IsCompact(shortList));
        Assert.True(DialogText.IsCompact(longList));
        Assert.Equal(40, longList.Count);
    }

    [Theory]
    [InlineData("Exclusão segura (irreversível)", "Apagar 3 arquivos?", DialogTone.Danger)]
    [InlineData("Esvaziar a lixeira", "Isso não tem volta. Continuar?", DialogTone.Danger)]
    [InlineData("Secure delete", "This cannot be undone.", DialogTone.Danger)]
    [InlineData("Otimizar meu PC com segurança", "Só entra o que pode ser desfeito em Histórico.", DialogTone.Warning)]
    public void Irreversible_actions_get_the_danger_tone(string title, string message, DialogTone expected) =>
        Assert.Equal(expected, DialogText.ToneFor(title, message));

    [Fact]
    public void Desktop_never_shows_the_windows_message_box()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Nexus.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var desktop = Path.Combine(dir!.FullName, "src", "Nexus.Desktop");
        var offenders = Directory.EnumerateFiles(desktop, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("MessageBox.Show", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(desktop, f)).ToList();
        Assert.True(offenders.Count == 0, "Use Views.Shell.NexusDialog (aviso do NEXUS): " + string.Join(", ", offenders));
    }
}
