using Nexus.Broker;
using Nexus.GameProfiles.Cards;

namespace Nexus.Desktop.ViewModels.Simple;

/// <summary>
/// Ponte entre um cartão de jogo (<see cref="GameCardDefinition"/>, registrado por reflexão em <see cref="GameCardRegistry"/>) e a
/// tela Jogos: transforma cada <see cref="GameCardOption"/> num <see cref="GameOptionPlan"/> (os pedidos ao aplicador, as partes
/// que o diário reconhece e a leitura do estado real pelo leitor do jogo, <see cref="GameCardStateRegistry"/>). As ações de
/// arquivo de jogo rodam como usuário (perfil pelo SID). Nada aqui escreve: a tela aplica pelo <c>IModuleHost</c>.
/// </summary>
public static class GameCardPlans
{
    /// <summary>Identidade estável do botão: id do jogo + id da opção.</summary>
    public static string PlanId(GameCardDefinition card, GameCardOption option)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(option);
        return card.Id + "|" + option.Id;
    }

    /// <summary>Todas as ações do clique: a principal (com o valor escolhido) e as extras (valor fixo do catálogo).</summary>
    public static IReadOnlyList<ActionRequest> Requests(GameCardOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        var list = new List<ActionRequest> { new(option.ActionKey, option.Value) };
        if (option.ExtraActionKeys is { } extras) list.AddRange(extras.Select(k => new ActionRequest(k, null)));
        return list;
    }

    public static GameOptionPlan For(GameCardDefinition card, GameCardOption option, Func<GameCardStateContext> context)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(option);
        ArgumentNullException.ThrowIfNull(context);
        var requests = Requests(option);
        return new GameOptionPlan(
            PlanId(card, option),
            [.. requests.Select(r => (r, BrokerLevel.User))],
            [.. requests.Select(r => new OptionPart(r.ActionId))],
            _ => Translate(GameCardStateRegistry.Read(card.Id, option.Id, context())));
    }

    /// <summary>Todos os planos de um cartão, na ordem das opções.</summary>
    public static IReadOnlyList<GameOptionPlan> ForCard(GameCardDefinition card, Func<GameCardStateContext> context)
    {
        ArgumentNullException.ThrowIfNull(card);
        return [.. card.Options.Select(o => For(card, o, context))];
    }

    public static OptionState Translate(GameCardOptionState state) => state switch
    {
        GameCardOptionState.On => OptionState.On,
        GameCardOptionState.Off => OptionState.Off,
        _ => OptionState.Unknown,
    };

    /// <summary>Contexto real desta conta: a pasta do perfil (C:\Users\nome) e a da Steam, quando houver (só leitura; nos testes, pastas falsas).</summary>
    public static GameCardStateContext CurrentUserContext()
    {
        string? profile = null;
        try
        {
            var p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(p)) profile = p;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or ArgumentException) { }
        return new GameCardStateContext(profile, SteamDir());
    }

    private static string? SteamDir()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam") ??
                            Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            return key?.GetValue("InstallPath") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { return null; }
    }
}
