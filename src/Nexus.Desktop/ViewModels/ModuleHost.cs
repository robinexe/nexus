using Nexus.Broker;
using Nexus.Core;
using Nexus.HardwareDiscovery;

namespace Nexus.Desktop.ViewModels;

/// <summary>
/// O que a janela oferece às páginas dos módulos. É a ÚNICA via de escrita delas: tudo passa pelo aplicador (broker),
/// com confirmação, diário, reversão e (para mudanças de sistema) aviso do Windows + ponto de restauração.
/// </summary>
public interface IModuleHost
{
    /// <summary>Leitura do PC (null até a primeira leitura terminar).</summary>
    SystemFacts? Facts { get; }

    /// <summary>SID de quem usa a interface (para ações no perfil do usuário).</summary>
    string UserSid { get; }

    /// <summary>
    /// Perfil de hardware para as recomendações (classe da placa, RAM, notebook, bateria, SSD e a preferência "menos atraso");
    /// null até a primeira leitura terminar. Lógica pura em <see cref="Nexus.Assistant.HardwareProfiler"/>.
    /// </summary>
    Nexus.Assistant.HardwareProfile? HardwareProfile => null;

    /// <summary>Simula no aplicador: mostra o que mudaria, sem escrever.</summary>
    Task<BrokerCallResult> PlanAsync(IReadOnlyList<ActionRequest> requests, BrokerLevel level);

    /// <summary>
    /// Pede confirmação (texto <paramref name="confirmation"/>) e aplica pelo aplicador. Retorna null se o usuário cancelar.
    /// A execução fica disponível para reverter na página Aplicar e em <see cref="RevertAsync"/>.
    /// </summary>
    Task<BrokerCallResult?> ApplyAsync(string title, string confirmation, IReadOnlyList<ActionRequest> requests, BrokerLevel level);

    /// <summary>
    /// Igual a <see cref="ApplyAsync(string, string, IReadOnlyList{ActionRequest}, BrokerLevel)"/>, avisando os marcos reais
    /// (confirmado, aplicador iniciando/aviso do Windows, aplicando, relendo) na thread da interface — para a tela de progresso.
    /// </summary>
    Task<BrokerCallResult?> ApplyAsync(string title, string confirmation, IReadOnlyList<ActionRequest> requests, BrokerLevel level, IProgress<ApplyStage>? stages) =>
        ApplyAsync(title, confirmation, requests, level);

    /// <summary>
    /// Aplica pelo aplicador SEM outra caixa de confirmação: quem chama já confirmou com o usuário em palavras simples. Usado pelo
    /// modo automático do Assistente ("Otimizar meu PC com segurança", uma confirmação no começo, só risco baixo desfazível) e pelo
    /// "um clique" da interface simples (QuickApplier: uma frase de confirmação antes). Mesmo caminho de <see cref="ApplyAsync(string, string, IReadOnlyList{ActionRequest}, BrokerLevel)"/>:
    /// diário, releitura, aviso do Windows e ponto de restauração para mudanças de sistema.
    /// </summary>
    Task<BrokerCallResult?> ApplyConfirmedAsync(string title, IReadOnlyList<ActionRequest> requests, BrokerLevel level, IProgress<ApplyStage>? stages);

    /// <summary>Reverte uma execução (com confirmação).</summary>
    Task<BrokerCallResult?> RevertAsync(Guid runId, BrokerLevel level);

    /// <summary>
    /// Reverte pelo aplicador SEM perguntar de novo. Só para o desfazer automático que o usuário já aceitou ao confirmar a
    /// mudança (ex.: troca do modo do monitor: "Volta sozinho em 15 s", porque a tela pode ter ficado preta e ele não vê o
    /// botão). Mesmo caminho de <see cref="RevertAsync"/> (diário, releitura, tela de progresso), só sem a caixa de confirmação.
    /// </summary>
    Task<BrokerCallResult?> RevertWithoutConfirmationAsync(Guid runId, BrokerLevel level);

    /// <summary>Mostra uma mensagem na barra de status.</summary>
    void Notify(string message);

    /// <summary>Pergunta sim/não ao usuário (padrão: não).</summary>
    bool Confirm(string title, string message);

    /// <summary>Navega para outra página (ex.: do Automático para Limpeza).</summary>
    void Navigate(PageViewModel page);

    /// <summary>Acha a página de um tipo (ex.: o Automático abrindo a Limpeza). Null se não existir.</summary>
    PageViewModel? FindPage(Type pageType);
}

/// <summary>Base das páginas de módulo: recebe o host e é avisada quando a leitura do PC termina.</summary>
public abstract class ModulePage : PageViewModel
{
    private bool _busy;
    private string _message = "";

    public IModuleHost? Host { get; private set; }
    public bool Busy { get => _busy; protected set => Set(ref _busy, value); }
    public string Message { get => _message; protected set => Set(ref _message, value); }

    internal void Attach(IModuleHost host) => Host = host;

    /// <summary>Chamado (na thread da interface) depois de cada leitura do PC. Leituras pesadas: use Task.Run.</summary>
    public virtual Task OnFactsReadyAsync(SystemFacts facts) => Task.CompletedTask;

    /// <summary>Chamado quando o usuário abre a página.</summary>
    public virtual Task OnShownAsync() => Task.CompletedTask;

    /// <summary>Executa uma tarefa da página marcando Busy e convertendo exceções em mensagem (a interface nunca cai).</summary>
    protected async Task RunAsync(Func<Task> work)
    {
        if (Busy) return;
        Busy = true;
        try { await work(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Message = Loc.F("Shell.NaoFoiPossivelConcluir", ex.Message); }
        finally { Busy = false; }
    }
}
