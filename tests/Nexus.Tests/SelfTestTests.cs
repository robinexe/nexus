using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Nexus.SelfTest;

namespace Nexus.Tests;

/// <summary>Mecanismo do autoteste (nexus selftest): tempo limite, agregação, relatórios, sanitização e proibição de escrita.</summary>
[Trait("Category", "Unit")]
public sealed class SelfTestTests
{
    private static readonly SelfTestEnvironment Env = new("Teste OS", "X64", false, false, "0.0.0-test", ".NET", 4);

    private static Task<SelfTestReport> Run(params SelfTestCheck[] checks) =>
        SelfTestRunner.RunAsync(checks, new SelfTestOptions { Timeout = TimeSpan.FromSeconds(5) }, isWindows: true, environment: Env);

    // ------------------------------------------------------------------ resultado de cada verificação

    [Fact]
    public async Task Ok_warning_failure_and_skip_are_classified()
    {
        var r = await Run(
            new SelfTestCheck("M", "ok", c => c.Count("itens", 3)),
            new SelfTestCheck("M", "aviso", c => { c.Count("itens", 0); c.Warn("precisa de administrador"); }),
            new SelfTestCheck("M", "falha", (Action<CheckContext>)(_ => throw new InvalidOperationException("quebrou"))),
            new SelfTestCheck("M", "ignorado", _ => CheckContext.Skip("use --with-benchmarks")));

        Assert.Equal([CheckStatus.Ok, CheckStatus.Warning, CheckStatus.Failure, CheckStatus.Skipped], r.Results.Select(x => x.Status));
        Assert.Equal("itens: 3", r.Results[0].Summary);
        Assert.Equal("precisa de administrador", r.Results[1].Reason);
        Assert.Contains("InvalidOperationException: quebrou", r.Results[2].Reason);
        Assert.Contains("SelfTestTests", r.Results[2].Error); // pilha
        Assert.Equal("use --with-benchmarks", r.Results[3].Reason);
        Assert.Equal((1, 1, 1, 1), (r.Ok, r.Warnings, r.Failures, r.Skipped));
    }

    [Fact]
    public async Task Timeout_is_a_failure_and_late_writes_are_ignored()
    {
        var release = new ManualResetEventSlim();
        var check = new SelfTestCheck("M", "presa", c =>
        {
            c.Count("antes", 1);
            release.Wait(TimeSpan.FromSeconds(10)); // leitura síncrona presa, sem olhar o cancelamento
            c.Count("depois", 2);
            c.Warn("tarde demais");
        }) { Timeout = TimeSpan.FromMilliseconds(300) };
        var sw = Stopwatch.StartNew();
        var r = await Run(check);
        sw.Stop();
        release.Set();

        var x = Assert.Single(r.Results);
        Assert.Equal(CheckStatus.Failure, x.Status);
        Assert.Contains("tempo limite", x.Reason);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"esperou {sw.Elapsed}");
        await Task.Delay(100);
        Assert.Equal("antes: 1", x.Summary);
        Assert.Empty(x.Warnings);
        Assert.Equal(1, r.ExitCode);
    }

    [Fact]
    public async Task Timeout_cancels_the_token_given_to_the_check()
    {
        var cancelled = new TaskCompletionSource<bool>();
        var check = new SelfTestCheck("M", "cancelável", async c =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), c.Cancel); }
            catch (OperationCanceledException) { cancelled.TrySetResult(true); throw; }
        }) { Timeout = TimeSpan.FromMilliseconds(200) };
        var r = await Run(check);
        Assert.Equal(CheckStatus.Failure, r.Results[0].Status);
        Assert.True(await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Windows_only_checks_are_ignored_elsewhere_and_only_filters_modules()
    {
        var ran = new ConcurrentBag<string>();
        SelfTestCheck[] checks =
        [
            new("Programas", "win", c => ran.Add("win")),
            new("Catálogo", "todos", c => ran.Add("todos")) { WindowsOnly = false },
            new("Segurança", "fora do filtro", c => ran.Add("filtro")) { WindowsOnly = false },
        ];
        var r = await SelfTestRunner.RunAsync(checks, new SelfTestOptions { Only = ["programas", "CATÁLOGO"] }, isWindows: false, environment: Env);

        Assert.Equal(2, r.Results.Count);
        Assert.Equal(CheckStatus.Skipped, r.Results[0].Status);
        Assert.Equal("não é Windows", r.Results[0].Reason);
        Assert.Equal(CheckStatus.Ok, r.Results[1].Status);
        Assert.Equal(["todos"], ran.ToArray());
        Assert.Equal(0, r.ExitCode); // IGNORADO não é falha
    }

    [Fact]
    public async Task Exit_code_is_zero_only_without_failures_and_warnings_do_not_count()
    {
        Assert.Equal(0, (await Run(new SelfTestCheck("M", "a", c => c.Warn("x")))).ExitCode);
        Assert.Equal(1, (await Run(new SelfTestCheck("M", "a", c => c.Warn("x")), new SelfTestCheck("M", "b", (Action<CheckContext>)(_ => throw new IOException("y"))))).ExitCode);
    }

    [Fact]
    public async Task Shared_data_flows_between_checks_in_order()
    {
        var r = await Run(
            new SelfTestCheck("M", "produz", c => c.Shared["x"] = "valor"),
            new SelfTestCheck("M", "consome", c => c.Info("lido", c.Get<string>("x"))));
        Assert.Equal("lido: valor", r.Results[1].Summary);
    }

    // ------------------------------------------------------------------ relatórios

    private static async Task<SelfTestReport> Sample() => await Run(
        new SelfTestCheck("Programas", "inventário", c => { c.Count("programas", 82); c.Flag("winget", true); c.Number("GB", 9.6); }),
        new SelfTestCheck("Ferramentas", "DNS <script>", c => c.Warn("contar o cache DNS precisa de <b>administrador</b>")),
        new SelfTestCheck("Disco", "quebra", (Action<CheckContext>)(_ => throw new FormatException("formato & inválido"))));

    [Fact]
    public async Task Json_report_has_schema_totals_and_typed_data()
    {
        var r = await Sample();
        using var doc = JsonDocument.Parse(r.ToJson());
        var root = doc.RootElement;
        Assert.Equal(SelfTestReport.Schema, root.GetProperty("schema").GetString());
        Assert.Equal(3, root.GetProperty("totals").GetProperty("checks").GetInt32());
        Assert.Equal(1, root.GetProperty("totals").GetProperty("failures").GetInt32());
        Assert.Equal(1, root.GetProperty("exitCode").GetInt32());
        var first = root.GetProperty("results")[0];
        Assert.Equal("Ok", first.GetProperty("status").GetString());
        Assert.Equal("OK", first.GetProperty("statusText").GetString());
        Assert.Equal(82, first.GetProperty("data").GetProperty("programas").GetInt64());
        Assert.True(first.GetProperty("data").GetProperty("winget").GetBoolean());
        Assert.Equal(9.6, first.GetProperty("data").GetProperty("GB").GetDouble());
        Assert.Equal("Failure", root.GetProperty("results")[2].GetProperty("status").GetString());
        Assert.Contains("FormatException", root.GetProperty("results")[2].GetProperty("error").GetString());
    }

    [Fact]
    public async Task Html_report_escapes_everything_and_lists_failures_first()
    {
        var html = (await Sample()).ToHtml();
        Assert.StartsWith("<!doctype html>", html.TrimStart(), StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("DNS &lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;administrador&lt;/b&gt;", html, StringComparison.Ordinal);
        Assert.Contains("formato &amp; inv", html, StringComparison.Ordinal);
        Assert.True(html.IndexOf(">quebra<", StringComparison.Ordinal) is var f && f > 0 && f < html.IndexOf(">Programas<", StringComparison.Ordinal), "FALHA vem antes de OK");
    }

    [Fact]
    public async Task Html_includes_the_interface_section_with_relative_screenshots()
    {
        var ui = new UiSelfTestReport
        {
            InitialLoadCompleted = true,
            InitialLoadSeconds = 8.2,
            Pages =
            [
                new UiPageResult { Index = 1, Title = "Meu PC", Group = "Meu PC", Status = CheckStatus.Ok, ReachedIdle = true, Screenshot = "telas/01-meu-pc.png" },
                new UiPageResult { Index = 2, Title = "Medir", Group = "Medir", Status = CheckStatus.Failure, Exceptions = ["[x] InvalidOperationException: binding"], BindingErrors = 2 },
            ],
        };
        var html = (await Sample()).ToHtml(ui, "ui");
        Assert.Contains("Interface (cada tela aberta de verdade)", html, StringComparison.Ordinal);
        Assert.Contains("src=\"ui/telas/01-meu-pc.png\"", html, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException: binding", html, StringComparison.Ordinal);
        Assert.Equal(1, ui.ExitCode);
    }

    [Fact]
    public async Task Text_report_has_one_line_per_check_and_the_footer()
    {
        var text = (await Sample()).ToText();
        Assert.Contains("[OK      ] Programas · inventário", text, StringComparison.Ordinal);
        Assert.Contains("[AVISO   ] Ferramentas", text, StringComparison.Ordinal);
        Assert.Contains("[FALHA   ] Disco · quebra", text, StringComparison.Ordinal);
        Assert.Contains("1 OK · 1 AVISO · 1 FALHA · 0 IGNORADO", text, StringComparison.Ordinal);
        Assert.Contains("Nada foi alterado no sistema.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Ui_report_round_trips_and_classifies_pages()
    {
        var ui = new UiSelfTestReport
        {
            InitialLoadCompleted = true,
            Pages = [new UiPageResult { Title = "Limpeza", Status = CheckStatus.Warning, BindingErrors = 1, BindingSamples = ["System.Windows.Data Error: 40"] }],
        };
        var back = UiSelfTestReport.FromJson(ui.ToJson());
        Assert.NotNull(back);
        Assert.Equal("Limpeza", back!.Pages[0].Title);
        Assert.Equal(CheckStatus.Warning, back.Pages[0].Status);
        Assert.Equal(0, back.ExitCode);
        Assert.Null(UiSelfTestReport.FromJson("{ quebrado"));
        Assert.Equal(1, (ui with { InitialLoadCompleted = false }).ExitCode);

        Assert.Equal(CheckStatus.Ok, UiSelfTestReport.Classify(0, true, 0.5, 0, true));
        Assert.Equal(CheckStatus.Warning, UiSelfTestReport.Classify(0, true, 0.5, 3, true));
        Assert.Equal(CheckStatus.Warning, UiSelfTestReport.Classify(0, true, 2.5, 0, true));
        Assert.Equal(CheckStatus.Warning, UiSelfTestReport.Classify(0, true, 0, 0, false));
        Assert.Equal(CheckStatus.Failure, UiSelfTestReport.Classify(1, true, 0, 0, true));
        Assert.Equal(CheckStatus.Failure, UiSelfTestReport.Classify(0, false, 0, 0, true));
        Assert.Equal(CheckStatus.Failure, UiSelfTestReport.Classify(0, true, 12, 0, true));
    }

    // ------------------------------------------------------------------ sanitização

    [Fact]
    public void Sanitizer_removes_profile_user_machine_and_email()
    {
        const string profile = @"C:\Users\Matheus";
        string Clean(string s) => Sanitizer.Clean(s, profile, "Matheus", "MATHEUSANJOS");

        Assert.Equal(@"%USERPROFILE%\AppData\Local\NEXUS\x.json", Clean(@"C:\Users\Matheus\AppData\Local\NEXUS\x.json"));
        Assert.Equal(@"D:\Users\<usuário>\Desktop", Clean(@"D:\Users\Outra.Pessoa\Desktop"));
        Assert.Equal(@"C:\Usuários\<usuário>\Documentos", Clean(@"C:\Usuários\Fulano\Documentos"));
        Assert.Equal("/home/<usuário>/nexus", Clean("/home/claude/nexus"));
        Assert.Equal("erro em <pc>: acesso negado para <usuário>", Clean("erro em MATHEUSANJOS: acesso negado para matheus"));
        Assert.Equal("fale com <e-mail>", Clean("fale com alguem@exemplo.com.br"));
        Assert.Equal("MatheusFoo continua", Clean("MatheusFoo continua")); // só palavra inteira
        Assert.Equal("", Sanitizer.Clean(null, null, null, null));
        Assert.Equal("ab", Sanitizer.Clean("ab", null, "ab", "ab")); // nomes curtos não trocam pedaços de texto
    }

    [Fact]
    public async Task Exception_text_in_the_report_is_sanitized()
    {
        var user = Environment.UserName;
        var r = await Run(new SelfTestCheck("M", "x", (Action<CheckContext>)(_ => throw new IOException(@"C:\Users\" + "Alguem" + @"\arquivo.txt em uso"))));
        Assert.Contains(@"C:\Users\<usuário>\arquivo.txt", r.Results[0].Reason);
        Assert.DoesNotContain("Alguem", r.ToJson(), StringComparison.Ordinal);
        if (user.Length >= 3) Assert.DoesNotContain(@"\" + user + @"\", r.ToJson(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Info_values_are_sanitized_and_clipped()
    {
        var r = await Run(new SelfTestCheck("M", "x", c => c.Info("caminho", @"C:\Users\Alguem\" + new string('a', 200))));
        var v = (string)r.Results[0].Data["caminho"];
        Assert.StartsWith(@"C:\Users\<usuário>\", v, StringComparison.Ordinal);
        Assert.True(v.Length <= 80);
    }

    // ------------------------------------------------------------------ argumentos

    [Fact]
    public void Arguments_parse_options_and_reject_unknown_ones()
    {
        var a = SelfTestArguments.Parse(["--json", "r.json", "--html", "r.html", "--with-benchmarks", "--timeout", "30", "--only", "Programas, Segurança", "--max-files", "50", "--quiet"], out var error);
        Assert.Null(error);
        Assert.Equal("r.json", a.JsonPath);
        Assert.Equal("r.html", a.HtmlPath);
        Assert.True(a.Options.WithBenchmarks);
        Assert.False(a.Options.WithWindowsUpdate);
        Assert.Equal(TimeSpan.FromSeconds(30), a.Options.Timeout);
        Assert.Equal(["Programas", "Segurança"], a.Options.Only);
        Assert.Equal(50, a.Options.ScannerMaxFiles);
        Assert.True(a.Quiet);

        SelfTestArguments.Parse(["--apply"], out error);
        Assert.Contains("não existe", error);
        SelfTestArguments.Parse(["--json"], out error);
        Assert.NotNull(error);
        SelfTestArguments.Parse(["--timeout", "2"], out error);
        Assert.NotNull(error);
        SelfTestArguments.Parse(["--json", "--html", "x"], out error);
        Assert.NotNull(error);
        Assert.True(SelfTestArguments.Parse(["--help"], out _).Help);
    }

    // ------------------------------------------------------------------ verificações reais (no que roda fora do Windows)

    [Fact]
    public void Every_check_has_a_module_a_unique_name_and_a_sane_timeout()
    {
        var all = NexusChecks.All();
        Assert.True(all.Count >= 30);
        Assert.Equal(all.Count, all.Select(c => c.Module + "|" + c.Name).Distinct().Count());
        Assert.All(all, c => Assert.False(string.IsNullOrWhiteSpace(c.Module)));
        Assert.All(all, c => Assert.True(c.Timeout is null || c.Timeout <= TimeSpan.FromMinutes(5)));
        // Os módulos pedidos estão todos lá.
        string[] modules = ["Descoberta", "Diagnóstico", "Catálogo", "Limpeza", "Disco", "Programas", "Inicialização", "Serviços", "Privacidade", "Segurança",
            "Verificar arquivos", "Monitor", "Ferramentas do sistema", "Atualizar drivers", "Laboratório: processador", "Laboratório: placa de vídeo",
            "Laboratório: memória", "Memória: diagnóstico", "Medir", "Automático", "Ajustes"];
        Assert.All(modules, m => Assert.Contains(all, c => c.Module == m));
    }

    [Fact]
    public async Task Portable_checks_run_without_failures_and_windows_ones_are_ignored()
    {
        // isWindows: false também no Windows do CI, para o teste unitário não rodar winget, Defender etc.
        var r = await SelfTestRunner.RunAsync(NexusChecks.All(), new SelfTestOptions { Timeout = TimeSpan.FromSeconds(120) }, isWindows: false);
        var failures = r.Results.Where(x => x.Status == CheckStatus.Failure).Select(x => $"{x.Module} · {x.Name}: {x.Reason}").ToList();
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.All(r.Results.Where(x => NexusChecks.All().Single(c => c.Module == x.Module && c.Name == x.Name).WindowsOnly),
            x => Assert.Equal(CheckStatus.Skipped, x.Status));
        var catalog = r.Results.Single(x => x.Name == "Carregar catalog/*.json");
        Assert.True((long)catalog.Data["ações"] > 100);
    }

    [Fact]
    public void Settings_check_never_touches_a_corrupted_file()
    {
        var dir = Directory.CreateTempSubdirectory("nexus-selftest-");
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            File.WriteAllText(path, "{ isto não é json");
            var before = File.GetLastWriteTimeUtc(path);
            var r = RunSync(c => NexusChecks.CheckSettingsFile(c, path));
            Assert.Equal(CheckStatus.Warning, r.Status);
            Assert.Contains("corrompido", r.Reason);
            Assert.Equal(["settings.json"], dir.GetFiles().Select(f => f.Name)); // NexusSettings.Load guardaria uma cópia; o autoteste não
            Assert.Equal(before, File.GetLastWriteTimeUtc(path));

            File.WriteAllText(path, "{ \"format\": 1, \"trayIcon\": false, // comentário\n }");
            r = RunSync(c => NexusChecks.CheckSettingsFile(c, path));
            Assert.Equal(CheckStatus.Ok, r.Status);
            Assert.Equal(false, r.Data["ícone na bandeja"]);

            File.WriteAllText(path, "{ \"format\": 99 }");
            Assert.Equal(CheckStatus.Warning, RunSync(c => NexusChecks.CheckSettingsFile(c, path)).Status);
        }
        finally { dir.Delete(recursive: true); }
    }

    private static CheckResult RunSync(Action<CheckContext> body) =>
        SelfTestRunner.RunOneAsync(new SelfTestCheck("Ajustes", "t", body) { WindowsOnly = false }, new SelfTestOptions(), new ConcurrentDictionary<string, object>(), CancellationToken.None)
            .GetAwaiter().GetResult();

    // ------------------------------------------------------------------ proibição de escrita (no assembly compilado)

    private static List<(string Type, string Member)> References(string path)
    {
        using var pe = new PEReader(File.OpenRead(path));
        var md = pe.GetMetadataReader();
        return md.MemberReferences.Select(h => md.GetMemberReference(h))
            .Where(mr => mr.Parent.Kind == HandleKind.TypeReference)
            .Select(mr => (md.GetString(md.GetTypeReference((TypeReferenceHandle)mr.Parent).Name), md.GetString(mr.Name)))
            .Distinct().ToList();
    }

    [Fact]
    public void SelfTest_assembly_never_calls_a_write_api()
    {
        var refs = References(typeof(SelfTestRunner).Assembly.Location);
        (string, string)[] forbidden =
        [
            // Arquivos e pastas: o autoteste só monta texto; quem grava o relatório pedido é o nexus.exe.
            ("File", "WriteAllText"), ("File", "WriteAllBytes"), ("File", "WriteAllLines"), ("File", "AppendAllText"), ("File", "AppendAllLines"),
            ("File", "WriteAllTextAsync"), ("File", "WriteAllBytesAsync"), ("File", "AppendAllTextAsync"), ("File", "Create"), ("File", "CreateText"),
            ("File", "OpenWrite"), ("File", "Delete"), ("File", "Move"), ("File", "Copy"), ("File", "Replace"), ("File", "SetAttributes"),
            ("File", "SetLastWriteTimeUtc"), ("Directory", "CreateDirectory"), ("Directory", "Delete"), ("Directory", "Move"),
            ("FileInfo", "Delete"), ("FileInfo", "MoveTo"), ("FileInfo", "CopyTo"), ("FileInfo", "Create"), ("FileInfo", "OpenWrite"),
            ("DirectoryInfo", "Create"), ("DirectoryInfo", "Delete"), ("DirectoryInfo", "MoveTo"), ("FileStream", ".ctor"), ("StreamWriter", ".ctor"),
            // Registro e processos.
            ("RegistryKey", "SetValue"), ("RegistryKey", "CreateSubKey"), ("RegistryKey", "DeleteValue"), ("RegistryKey", "DeleteSubKey"),
            ("RegistryKey", "DeleteSubKeyTree"), ("Registry", "SetValue"), ("Process", "Start"), ("Process", "Kill"),
            // Motor e aplicador (mesma lista do DesktopReadOnlyTests).
            ("ActionExecutor", ".ctor"), ("ActionExecutor", "Execute"), ("ActionExecutor", "Revert"), ("ActionExecutor", "RevertRun"),
            ("RecoveryService", ".ctor"), ("RecoveryService", "Recover"), ("FileRunCoordinator", "TryAcquire"), ("IRunCoordinator", "TryAcquire"),
            ("SqliteJournal", ".ctor"), ("SqliteJournal", "OpenReadOnly"), ("IActionHandler", "Apply"), ("IActionHandler", "Restore"),
            ("BrokerClient", "Connect"), ("BrokerSession", ".ctor"), ("BrokerSession", "ForThisPc"), ("BrokerSession", "ForInstalledService"), ("BrokerService", ".ctor"),
            // APIs dos módulos que escrevem sem passar por handler (revisão de segurança) + "Save" das preferências e resultados.
            ("ScannerQuarantineStore", "Quarantine"), ("ScannerQuarantineStore", "Restore"), ("ScannerQuarantineStore", "WriteManifest"), ("ScannerQuarantineStore", "Discard"),
            ("SecureDeleteFileSystem", "OverwriteAndDelete"), ("ISecureDeleteFileSystem", "OverwriteAndDelete"),
            ("LeftoverQuarantine", "QuarantinePath"), ("LeftoverQuarantine", "RestorePath"), ("LeftoverQuarantine", "Discard"), ("LeftoverQuarantine", "WriteManifest"),
            ("QuarantineAcl", "Harden"), ("IRegistryTreeBackend", "DeleteTree"), ("IRegistryTreeBackend", "Import"), ("IRegistryBackend", "Write"),
            ("IRegistryBackend", "DeleteValue"), ("IWifiBackend", "Delete"), ("IWifiBackend", "Import"), ("IDriverStore", "Delete"), ("IDriverStore", "Add"),
            ("IAppxBackend", "Remove"), ("IAppxBackend", "Register"), ("IScheduledTaskBackend", "SetEnabled"), ("ITaskSchedulerBackend", "CreateOrReplace"),
            ("ITaskSchedulerBackend", "Delete"), ("IHostsStore", "Write"), ("AdminOnlyDirectory", "Ensure"), ("AdminOnlyDirectory", "RestrictToAdministrators"),
            ("NexusSettings", "Save"), ("NexusSettings", "Load"), ("IgnoreList", "Save"), ("CpuLabStore", "Save"), ("BackupStore", "Save"),
            ("BulkUninstaller", "RunAsync"), ("DefenderScanner", "ScanAsync"), ("DefenderScanner", "UpdateSignaturesAsync"),
            ("VendorTools", "OpenSystemRestore"), ("VendorTools", "OpenSystemProtection"), ("VendorTools", "OpenFolder"), ("VendorTools", "OpenStorePage"),
        ];
        var hits = forbidden.Where(refs.Contains).ToList();
        Assert.True(hits.Count == 0, "Escrita no autoteste: " + string.Join(", ", hits));

        // Controle: a inspeção enxerga as leituras esperadas.
        Assert.Contains(("SimulationOnlyHandler", "Wrap"), refs);
        Assert.Contains(("CatalogLoader", "LoadDirectory"), refs);
        Assert.Contains(("Discovery", "Capture"), refs);
        Assert.Contains(("IActionHandler", "Detect"), refs);
    }

    // ------------------------------------------------------------------ código de saída do nexus selftest (processo real)

    private static string? CliDll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Nexus.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Debug" : "Release";
        var path = Path.Combine(dir.FullName, "src", "Nexus.Cli", "bin", config, "net10.0", "nexus.dll");
        return File.Exists(path) ? path : null;
    }

    private static (int Code, string Out) RunCli(string dll, params string[] args)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";
        var psi = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(dll);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(120_000), "nexus selftest não terminou em 2 min");
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    [Fact]
    public void Cli_selftest_exit_codes_and_json_file()
    {
        var dll = CliDll();
        Assert.True(dll is not null, "nexus.dll não encontrado: compile a solução inteira antes dos testes.");
        Assert.Equal(2, RunCli(dll!, "selftest", "--apagar-tudo").Code);
        var help = RunCli(dll!, "selftest", "--help");
        Assert.Equal(0, help.Code);
        Assert.Contains("SOMENTE LEITURA", help.Out, StringComparison.Ordinal);

        var dir = Directory.CreateTempSubdirectory("nexus-selftest-cli-");
        try
        {
            var json = Path.Combine(dir.FullName, "sub", "r.json");
            var html = Path.Combine(dir.FullName, "r.html");
            var run = RunCli(dll!, "selftest", "--only", "Ajustes,Catálogo", "--json", json, "--html", html, "--quiet");
            Assert.True(run.Code == 0, run.Out);
            using var doc = JsonDocument.Parse(File.ReadAllText(json));
            Assert.Equal(0, doc.RootElement.GetProperty("totals").GetProperty("failures").GetInt32());
            Assert.Contains("Autoteste do NEXUS", File.ReadAllText(html), StringComparison.Ordinal);
        }
        finally { dir.Delete(recursive: true); }
    }
}
