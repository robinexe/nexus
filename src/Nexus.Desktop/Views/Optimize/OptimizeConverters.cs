using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Nexus.Desktop.Views.Optimize;

/// <summary>
/// Vazio (null, texto vazio, coleção vazia, zero) vira <see cref="Visibility.Collapsed"/>; o resto, Visible.
/// Com <see cref="Invert"/> = true faz o contrário (útil para estados vazios: "nada por aqui ainda").
/// </summary>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var empty = value switch
        {
            null => true,
            string s => string.IsNullOrWhiteSpace(s),
            int i => i == 0,
            bool b => !b,
            ICollection c => c.Count == 0,
            IEnumerable e => !e.GetEnumerator().MoveNext(),
            _ => false,
        };
        return empty != Invert ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// Resultado de uma ação no broker ou estado de uma entrada do diário (nomes de ExecutionOutcome, RevertOutcome e
/// JournalState) em português de jogador. Um sufixo " [inferido]" vira " (inferido)".
/// </summary>
public sealed class OutcomeTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var raw = value as string ?? value?.ToString() ?? "";
        var (name, inferred) = OutcomeText.Split(raw);
        var text = OutcomeText.Of(name);
        return inferred ? text + " (inferido)" : text;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Tom visual do resultado: "good", "bad", "change" (âmbar: algo mudou ou mudaria) ou "neutral".</summary>
public sealed class OutcomeToneConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var raw = value as string ?? value?.ToString() ?? "";
        return OutcomeText.Tone(OutcomeText.Split(raw).Name);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public static class OutcomeText
{
    private const string InferredSuffix = "[inferido]";

    public static (string Name, bool Inferred) Split(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.EndsWith(InferredSuffix, StringComparison.Ordinal))
            return (trimmed[..^InferredSuffix.Length].Trim(), true);
        return (trimmed, false);
    }

    public static string Of(string name) => name switch
    {
        // ExecutionOutcome
        "Verified" => Loc.T("Auto.AplicadaConferida"),
        "AlreadyConfigured" => Loc.T("Auto.JaEstavaAssim"),
        "SkippedExternalChange" => Loc.T("Auto.PuladaMudouFora"),
        "NotApplicable" => Loc.T("Pg.NaoAplica"),
        "Blocked" => Loc.T("Shell.Bloqueada"),
        "Failed" => Loc.T("Auto.Falhou"),
        "FailedAndCompensated" => Loc.T("Auto.FalhouFoiDesfeita"),
        "Interrupted" => Loc.T("Auto.Interrompida"),
        // RevertOutcome
        "Reverted" => Loc.T("Auto.Revertida"),
        "AlreadyOriginal" => Loc.T("Auto.JaEstavaOriginal"),
        "Conflict" => Loc.T("Auto.ConflitoNaoSobrescrevemos"),
        "NothingToRevert" => Loc.T("Auto.NadaReverter"),
        // JournalState
        "Planned" => Loc.T("Auto.Planejada"),
        "BackedUp" => Loc.T("Auto.OriginalGuardado"),
        "Applying" => Loc.T("Shell.Aplicando"),
        "Applied" => Loc.T("Auto.Aplicada"),
        "RevertConflict" => Loc.T("Auto.ConflitoReverter"),
        "Restoring" => Loc.T("Auto.Restaurando"),
        "Accepted" => Loc.T("Auto.MantidaVoce"),
        "" => "",
        _ => name,
    };

    public static string Tone(string name) => name switch
    {
        "Verified" or "Reverted" or "AlreadyConfigured" or "AlreadyOriginal" => "good",
        "Failed" or "Interrupted" or "Conflict" or "RevertConflict" => "bad",
        "Applied" or "Applying" or "Restoring" or "BackedUp" or "Planned" or "FailedAndCompensated" => "change",
        _ => "neutral",
    };
}
