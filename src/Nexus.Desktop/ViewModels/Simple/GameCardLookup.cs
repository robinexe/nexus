using System.IO;
using Nexus.GameProfiles.Cards;
using Nexus.Measurement;

namespace Nexus.Desktop.ViewModels.Simple;

/// <summary>
/// Acha o cartão de um jogo da biblioteca (<see cref="GameCardRegistry"/>) pelo executável: o próprio ou o que desenha por ele
/// (lançador/anticheat → jogo, <see cref="GameAliases"/>). Jogo sem cartão = null (cartão genérico na tela Jogos). Lógica pura, testada.
/// </summary>
public static class GameCardLookup
{
    public static GameCardDefinition? ForExe(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return null;
        var names = new List<string> { exe };
        names.AddRange(GameAliases.RenderCandidates(exe));
        return GameCardRegistry.All.FirstOrDefault(c => c.Executables.Any(e => names.Contains(e, StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>Processos (sem .exe) que, abertos, impedem as opções do arquivo do jogo: todos os executáveis do cartão.</summary>
    public static IReadOnlyList<string> ProcessNames(GameCardDefinition card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return [.. card.Executables.Select(Path.GetFileNameWithoutExtension).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
