using System.Globalization;

namespace Nexus.Broker;

/// <summary>
/// Em que ponto está um pedido de aplicação feito pela interface (para a tela de progresso). O aplicador responde de uma vez
/// só no fim; estes marcos são os que a interface vê de verdade: a confirmação, a partida do aplicador (com o aviso do Windows
/// para administrador), o envio do pedido e a releitura do PC depois de escrever.
/// </summary>
public enum ApplyStage
{
    /// <summary>Esperando o usuário responder à confirmação.</summary>
    WaitingConfirmation,
    /// <summary>O usuário confirmou.</summary>
    Confirmed,
    /// <summary>Iniciando o aplicador da conta do usuário.</summary>
    StartingApplier,
    /// <summary>Esperando o usuário responder ao aviso do Windows (administrador).</summary>
    WaitingWindows,
    /// <summary>Conectado: o pedido foi enviado e o aplicador está trabalhando.</summary>
    Running,
    /// <summary>O aplicador respondeu; a interface relê o PC.</summary>
    Refreshing,
}

/// <summary>
/// Frases para pessoas a partir da resposta do aplicador (barra de status da janela). Nunca mostra nome de enum nem o
/// identificador da execução: "Última aplicação: 5 feitas, 1 não precisou."
/// </summary>
public static class ApplySummary
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>
    /// O Windows cria no máximo um ponto de restauração a cada 24 h (SystemRestorePointCreationFrequency). Quando o
    /// SystemRestorePointHandler (Nexus.SystemTools) recusa por isso, o PC já está protegido pelo ponto recente: não é erro.
    /// </summary>
    public static bool IsRecentRestorePointRefusal(string? detail) =>
        detail is not null &&
        (detail.Contains("não criou um ponto novo", StringComparison.OrdinalIgnoreCase) ||
         detail.Contains("um a cada 24 horas", StringComparison.OrdinalIgnoreCase));

    /// <summary>Uma linha para a barra de status depois de aplicar.</summary>
    public static string Describe(BrokerCallResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Response is not { } r)
            return result.Message.Replace("durante Execute", "durante a aplicação", StringComparison.Ordinal);
        if (!r.Ok)
            return string.IsNullOrWhiteSpace(r.Error?.Detail) ? "O aplicador recusou o pedido. Nada foi alterado." : $"O aplicador recusou o pedido: {r.Error!.Detail}";
        var parts = Counts(r.Results ?? []);
        var text = parts.Count == 0 ? "Última aplicação: nada a fazer." : "Última aplicação: " + string.Join(", ", parts) + ".";
        if (r.SessionActive == true) text += " Ajustes de sessão ativos até você encerrar a sessão (página Aplicar).";
        var extra = Extra(r.Message);
        return extra.Length == 0 ? text : text + " " + extra;
    }

    /// <summary>"5 feitas", "1 não precisou", "2 não aplicadas"... na ordem: bom, neutro, ruim.</summary>
    public static IReadOnlyList<string> Counts(IReadOnlyList<ActionResultDto> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        int done = 0, already = 0, notNeeded = 0, notApplicable = 0, skipped = 0, blocked = 0, failed = 0;
        foreach (var x in results)
        {
            switch (x.Outcome)
            {
                case "Verified": done++; break;
                case "AlreadyConfigured": already++; break;
                case "NotApplicable": notApplicable++; break;
                case "SkippedExternalChange": skipped++; break;
                case "Blocked": blocked++; break;
                default:
                    if (IsRecentRestorePointRefusal(x.Detail)) notNeeded++;
                    else failed++;
                    break;
            }
        }
        var list = new List<string>();
        if (done > 0) list.Add(Plural(done, "feita", "feitas"));
        if (already > 0) list.Add(already == 1 ? "1 já estava assim" : string.Create(Pt, $"{already} já estavam assim"));
        if (notNeeded > 0) list.Add(notNeeded == 1 ? "1 não precisou" : string.Create(Pt, $"{notNeeded} não precisaram"));
        if (notApplicable > 0) list.Add(notApplicable == 1 ? "1 não se aplica a este PC" : string.Create(Pt, $"{notApplicable} não se aplicam a este PC"));
        if (skipped > 0) list.Add(Plural(skipped, "pulada (mudou por fora)", "puladas (mudaram por fora)"));
        if (blocked > 0) list.Add(Plural(blocked, "bloqueada", "bloqueadas"));
        if (failed > 0) list.Add(Plural(failed, "não aplicada (motivo na página Aplicar)", "não aplicadas (motivo na página Aplicar)"));
        return list;
    }

    /// <summary>Linha da barra de status depois de desfazer.</summary>
    public static string DescribeRevert(BrokerCallResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Response is not { } r) return result.Message.Replace("durante RevertRun", "ao desfazer", StringComparison.Ordinal);
        if (!r.Ok) return string.IsNullOrWhiteSpace(r.Error?.Detail) ? "O aplicador recusou desfazer." : $"O aplicador recusou desfazer: {r.Error!.Detail}";
        return r.FullyRestored == true ? "Desfeito: tudo voltou ao que era." : "Desfeito em parte: alguns itens não voltaram (detalhes na página Histórico).";
    }

    private static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : string.Create(Pt, $"{n} {many}");

    /// <summary>O que o aplicador acrescentou além das frases padrão (ex.: ponto de restauração criado).</summary>
    internal static string Extra(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "";
        return message.Replace("Execução encerrada.", "", StringComparison.Ordinal)
                      .Replace("Ajustes de sessão aplicados; o broker mantém a trava até EndSession.", "", StringComparison.Ordinal).Trim();
    }
}
