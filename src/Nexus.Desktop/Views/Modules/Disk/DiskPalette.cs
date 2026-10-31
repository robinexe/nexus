using Nexus.DiskTools.Analysis;

namespace Nexus.Desktop.Views.Modules.Disk;

/// <summary>
/// Cores e ícones por tipo de arquivo, compartilhados pelo treemap, pela legenda "Por tipo" e pelos cartões dos maiores arquivos
/// (a mesma cor nos três lugares: o usuário liga o bloco do mapa à linha da legenda sem ler).
/// </summary>
public static class DiskPalette
{
    /// <summary>Cor do tipo em "#RRGGBB" (âmbar = vídeo, verde = jogos, tons distintos para o resto).</summary>
    public static string Hex(FileCategory? category) => category switch
    {
        FileCategory.Video => "#FFB547",
        FileCategory.Game => "#5EE6A0",
        FileCategory.DiskImage => "#B48CFF",
        FileCategory.Installer => "#FF966B",
        FileCategory.Archive => "#E8D36B",
        FileCategory.Image => "#6BB7FF",
        FileCategory.Audio => "#D086F0",
        FileCategory.Document => "#A9B4CC",
        FileCategory.Development => "#8FD89A",
        FileCategory.System => "#7782A0",
        _ => "#9AA4BC",
    };

    /// <summary>Ícone de linha (chave de Themes/Icons.xaml) do tipo.</summary>
    public static string IconKey(FileCategory? category) => category switch
    {
        FileCategory.Video => "Icon.Monitor",
        FileCategory.Game => "Icon.Games",
        FileCategory.DiskImage => "Icon.Disk",
        FileCategory.Installer => "Icon.Programs",
        FileCategory.Archive => "Icon.Quarantine",
        FileCategory.Image => "Icon.Sparkle",
        FileCategory.Audio => "Icon.Measure",
        FileCategory.Document => "Icon.Page",
        FileCategory.Development => "Icon.Code",
        FileCategory.System => "Icon.Windows",
        _ => "Icon.Page",
    };
}
