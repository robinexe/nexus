using System.Net;
using System.Security.Cryptography;
using System.Text;
using Nexus.Updates;

namespace Nexus.Tests.Modules;

/// <summary>Atualização do NEXUS: versões, assinatura, manifesto, hosts, download conferido e decisão de instalar.</summary>
[Trait("Category", "Unit")]
public sealed class UpdatesTests : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-updates-" + Guid.NewGuid().ToString("N"));
    private static readonly Uri Feed = new("https://github.com/robinexe/nexus-releases/releases/latest/download/nexus-update.json");
    private static readonly Uri FeedSig = new("https://github.com/robinexe/nexus-releases/releases/latest/download/nexus-update.json.sig");
    private static readonly Uri Asset = new("https://github.com/robinexe/nexus-releases/releases/download/v0.9.1/NEXUS-0.9.1-win-x64-instalador.exe");

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(_dir, true); } catch (DirectoryNotFoundException) { }
    }

    private ECDsa PublicOnly()
    {
        var pub = ECDsa.Create();
        pub.ImportFromPem(_key.ExportSubjectPublicKeyInfoPem());
        return pub;
    }

    private static byte[] Installer(int size = 4096)
    {
        var data = new byte[size];
        for (var i = 0; i < size; i++) data[i] = (byte)(i * 31 + 7);
        return data;
    }

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static byte[] ManifestJson(string version = "0.9.1", string? url = null, string? sha = null, long? size = null, string schema = "nexus.update/1",
        string channel = "stable", string? minimum = null)
    {
        var data = Installer();
        var min = minimum is null ? "" : $"\"minimumVersion\": \"{minimum}\",";
        return Encoding.UTF8.GetBytes($$"""
            {
              "schema": "{{schema}}",
              "version": "{{version}}",
              "channel": "{{channel}}",
              "publishedAt": "2026-11-01T12:00:00Z",
              "notes": "Correções.",
              {{min}}
              "installer": { "url": "{{url ?? Asset.ToString()}}", "sha256": "{{sha ?? Sha(data)}}", "size": {{size ?? data.Length}} }
            }
            """);
    }

    private byte[] Sig(byte[] manifest) => Encoding.ASCII.GetBytes(UpdatePolicy.Sign(manifest, _key) + "\n");

    private sealed class FakeHttp : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = new(StringComparer.Ordinal);
        public List<Uri> Requested { get; } = [];

        public void Bytes(Uri url, byte[] body) => Routes[url.ToString()] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        public void Redirect(Uri from, Uri to) => Routes[from.ToString()] = () =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = to;
            return r;
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            return Task.FromResult(Routes.TryGetValue(request.RequestUri!.ToString(), out var f) ? f() : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private UpdateClient Client(FakeHttp http, string current = "0.9.0") => new(SemVersion.Parse(current), PublicOnly(), Feed, http);

    // ------------------------------------------------------------------ versões

    [Theory]
    [InlineData("0.9.0-ci.7", "0.9.0")]
    [InlineData("0.9.0", "0.9.1")]
    [InlineData("0.9.0-ci.7", "0.9.0-ci.8")]
    [InlineData("0.9.0-ci.9", "0.9.0-ci.10")]
    [InlineData("0.9.0-alpha", "0.9.0-alpha.1")]
    [InlineData("0.9.0-alpha.1", "0.9.0-alpha.beta")]
    [InlineData("0.9.0-beta", "0.9.0-rc.1")]
    [InlineData("0.9.0-rc.1", "0.9.0")]
    [InlineData("0.9.9", "0.10.0")]
    [InlineData("1.9.9", "2.0.0")]
    [InlineData("0.9.0-ci.7", "0.9.1-ci.1")]
    public void SemVer_orders_per_spec(string lower, string higher)
    {
        Assert.True(SemVersion.Parse(lower) < SemVersion.Parse(higher));
        Assert.True(SemVersion.Parse(higher) > SemVersion.Parse(lower));
    }

    [Fact]
    public void SemVer_ignores_build_metadata_and_v_prefix()
    {
        Assert.Equal(SemVersion.Parse("0.9.0-ci.7"), SemVersion.Parse("v0.9.0-ci.7+abc1234567"));
        Assert.Equal("0.9.0-ci.7", SemVersion.Parse("0.9.0-ci.7+abc").ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("0.9")]
    [InlineData("0.9.0.1")]
    [InlineData("01.9.0")]
    [InlineData("0.9.0-")]
    [InlineData("0.9.0-ci..7")]
    [InlineData("0.9.0-ci.07")]
    [InlineData("0.9.0-ci_7")]
    [InlineData("a.b.c")]
    public void SemVer_rejects_invalid(string text) => Assert.False(SemVersion.TryParse(text, out _));

    [Fact]
    public void App_version_strips_hash_and_reads_packaged_marker()
    {
        var v = AppVersionInfo.From("0.9.0-ci.7+0123456789", new Version(0, 9, 0, 0), packaged: true);
        Assert.Equal("0.9.0-ci.7", v.Display);
        Assert.True(v.Packaged);
        Assert.Equal("0.9.0", AppVersionInfo.From(null, new Version(0, 9, 0, 0), false).Display);
        // O próprio assembly de testes não é publicado: sem a marca.
        Assert.False(AppVersionInfo.FromAssembly(typeof(UpdatesTests).Assembly).Packaged);
    }

    // ------------------------------------------------------------------ assinatura

    [Fact]
    public void Signature_round_trip_in_DER()
    {
        var manifest = ManifestJson();
        var sig = Sig(manifest);
        var der = Convert.FromBase64String(Encoding.ASCII.GetString(sig).Trim());
        Assert.Equal(0x30, der[0]); // SEQUENCE do DER (RFC 3279), igual ao openssl dgst -sign
        using var pub = PublicOnly();
        Assert.True(UpdatePolicy.VerifySignature(manifest, sig, pub));
        // P1363 (r||s cru, 64 bytes) não é aceito como DER.
        var p1363 = Encoding.ASCII.GetBytes(Convert.ToBase64String(_key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        Assert.False(UpdatePolicy.VerifySignature(manifest, p1363, pub));
    }

    /// <summary>
    /// Gerado por tools/packaging/sign-update.py com o openssl (chave de teste descartável): o formato do CI é o que o NEXUS confere.
    /// </summary>
    [Fact]
    public void Signature_from_openssl_via_sign_update_script_verifies()
    {
        const string pub = """
            -----BEGIN PUBLIC KEY-----
            MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAELqOWjnXa5BAWjpdLWe1UBbtT14UR
            WxIQzkZWzZYVGU1LIYzVUhu21EkkSagFviuynlBei8r8aLcnuT+GWqTpsg==
            -----END PUBLIC KEY-----
            """;
        var manifest = Convert.FromBase64String(
            "ewogICJzY2hlbWEiOiAibmV4dXMudXBkYXRlLzEiLAogICJ2ZXJzaW9uIjogIjAuOS4xIiwKICAiY2hhbm5lbCI6ICJzdGFibGUiLAogICJwdWJsaXNoZWRBdCI6ICIyMDI2LTExLTAxVDA1OjM4OjUzWiIsCiAgIm5vdGVzIjogIkNvcnJlw6fDtWVzIGUgbWVsaG9yaWFzLiIsCiAgImluc3RhbGxlciI6IHsKICAgICJ1cmwiOiAiaHR0cHM6Ly9naXRodWIuY29tL3JvYmluZXhlL25leHVzLXJlbGVhc2VzL3JlbGVhc2VzL2Rvd25sb2FkL3YwLjkuMS9ORVhVUy0wLjkuMS13aW4teDY0LWluc3RhbGFkb3IuZXhlIiwKICAgICJzaGEyNTYiOiAiOWQ1YjViMGNlMDY2YWJjZGUzYmI1NzRlZjk4NzhkMTI5NGVhNWJkODBkNDNiYzc5MzAzYjc5ZDA5OWIyMWIwMCIsCiAgICAic2l6ZSI6IDMwMDAKICB9Cn0K");
        var sig = "MEQCIBzVfNVjhh+lQs2Ftxd3FKybmjy+QRA2u8ksLAVrp5ZfAiB2zRWSHaLxkF+4H+dyAkjcgRd2fHtgr7ZKz30Yrcbctw==\n"u8.ToArray();
        using var key = UpdatePolicy.ImportPublicKey(pub);
        Assert.True(UpdatePolicy.VerifySignature(manifest, sig, key));
        using var client = new UpdateClient(SemVersion.Parse("0.9.0"), key, Feed, new FakeHttp());
        var r = client.Evaluate(manifest, sig);
        Assert.Equal(UpdateCheckStatus.Available, r.Status);
        Assert.Equal("Correções e melhorias.", r.Manifest!.Notes);
        Assert.Equal(3000, r.Manifest.Installer.Size);
    }

    [Fact]
    public void Signature_rejects_changed_bytes_other_key_and_garbage()
    {
        var manifest = ManifestJson();
        var sig = Sig(manifest);
        using var pub = PublicOnly();
        var changed = (byte[])manifest.Clone();
        changed[^3] ^= 1;
        Assert.False(UpdatePolicy.VerifySignature(changed, sig, pub));
        Assert.False(UpdatePolicy.VerifySignature(manifest, "não é base64!"u8, pub));
        Assert.False(UpdatePolicy.VerifySignature(manifest, ""u8, pub));
        using var trusted = UpdatePolicy.CreateTrustedKey(); // chave embutida: não é a do teste
        Assert.False(UpdatePolicy.VerifySignature(manifest, sig, trusted));
    }

    [Fact]
    public void Embedded_key_is_P256()
    {
        using var k = UpdatePolicy.CreateTrustedKey();
        Assert.Equal(256, k.KeySize);
        Assert.Equal("1.2.840.10045.3.1.7", k.ExportParameters(false).Curve.Oid.Value);
    }

    // ------------------------------------------------------------------ manifesto

    [Fact]
    public void Manifest_parses_valid()
    {
        var m = UpdateManifest.Parse(ManifestJson(minimum: "0.8.0"));
        Assert.Equal("0.9.1", m.Version.ToString());
        Assert.Equal(Asset, m.Installer.Url);
        Assert.Equal(4096, m.Installer.Size);
        Assert.Equal("0.8.0", m.MinimumVersion!.ToString());
        Assert.Equal("NEXUS-0.9.1-win-x64-instalador.exe", m.Installer.FileName(m.Version));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("channel")]
    [InlineData("version")]
    [InlineData("http")]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("sha")]
    [InlineData("size0")]
    [InlineData("sizeBig")]
    [InlineData("minimum")]
    public void Manifest_rejects(string what)
    {
        var json = what switch
        {
            "schema" => ManifestJson(schema: "nexus.update/2"),
            "channel" => ManifestJson(channel: "beta"),
            "version" => ManifestJson(version: "latest"),
            "http" => ManifestJson(url: "http://github.com/robinexe/nexus-releases/releases/download/v0.9.1/x.exe"),
            "host" => ManifestJson(url: "https://evil.example.com/NEXUS.exe"),
            "port" => ManifestJson(url: "https://github.com:8443/x.exe"),
            "sha" => ManifestJson(sha: "abc"),
            "size0" => ManifestJson(size: 0),
            "sizeBig" => ManifestJson(size: UpdatePolicy.MaxInstallerBytes + 1),
            "minimum" => ManifestJson(minimum: "x"),
            _ => throw new ArgumentOutOfRangeException(nameof(what)),
        };
        Assert.Throws<UpdateException>(() => UpdateManifest.Parse(json));
    }

    [Theory]
    [InlineData("https://github.com/a/b", true)]
    [InlineData("https://objects.githubusercontent.com/x", true)]
    [InlineData("https://release-assets.githubusercontent.com/x", true)]
    [InlineData("https://GITHUB.com/x", true)]
    [InlineData("http://github.com/x", false)]
    [InlineData("https://github.com.evil.com/x", false)]
    [InlineData("https://evilgithub.com/x", false)]
    [InlineData("https://user:pw@github.com/x", false)]
    [InlineData("https://raw.githubusercontent.com/x", false)]
    public void Download_host_policy(string url, bool allowed) => Assert.Equal(allowed, UpdatePolicy.IsAllowedDownload(new Uri(url)));

    [Fact]
    public void Feed_override_by_environment()
    {
        Assert.Equal(UpdatePolicy.DefaultFeed, UpdatePolicy.FeedUri(_ => null));
        Assert.Equal(UpdatePolicy.DefaultFeed, UpdatePolicy.FeedUri(_ => "não é url"));
        Assert.Equal(new Uri("http://127.0.0.1:8080/m.json"), UpdatePolicy.FeedUri(n => n == UpdatePolicy.FeedVariable ? "http://127.0.0.1:8080/m.json" : null));
        Assert.Equal(FeedSig, UpdatePolicy.SignatureUri(Feed));
        Assert.Equal("https://github.com/robinexe/nexus-releases/releases/latest/download/nexus-update.json", UpdatePolicy.DefaultFeed.ToString());
    }

    // ------------------------------------------------------------------ procurar

    [Fact]
    public async Task Check_offers_newer_signed_version()
    {
        var http = new FakeHttp();
        var m = ManifestJson("0.9.1");
        http.Bytes(Feed, m);
        http.Bytes(FeedSig, Sig(m));
        using var c = Client(http);
        var r = await c.CheckAsync();
        Assert.Equal(UpdateCheckStatus.Available, r.Status);
        Assert.Equal("0.9.1", r.Manifest!.Version.ToString());
    }

    [Fact]
    public async Task Check_ci_build_is_offered_next_stable()
    {
        var http = new FakeHttp();
        var m = ManifestJson("0.9.0");
        http.Bytes(Feed, m);
        http.Bytes(FeedSig, Sig(m));
        using var c = Client(http, "0.9.0-ci.7");
        Assert.Equal(UpdateCheckStatus.Available, (await c.CheckAsync()).Status);
    }

    [Theory]
    [InlineData("0.9.0")]
    [InlineData("0.8.5")]
    [InlineData("0.9.0-ci.99")]
    public async Task Check_same_or_older_is_up_to_date(string published)
    {
        var http = new FakeHttp();
        var m = ManifestJson(published);
        http.Bytes(Feed, m);
        http.Bytes(FeedSig, Sig(m));
        using var c = Client(http, "0.9.0");
        Assert.Equal(UpdateCheckStatus.UpToDate, (await c.CheckAsync()).Status);
    }

    [Fact]
    public async Task Check_not_published_is_not_an_error()
    {
        using var c = Client(new FakeHttp());
        var r = await c.CheckAsync();
        Assert.Equal(UpdateCheckStatus.NotPublished, r.Status);
    }

    [Fact]
    public async Task Check_rejects_missing_or_bad_signature()
    {
        var http = new FakeHttp();
        var m = ManifestJson("0.9.1");
        http.Bytes(Feed, m);
        using (var c = Client(http)) Assert.Equal(UpdateCheckStatus.Failed, (await c.CheckAsync()).Status);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        http.Bytes(FeedSig, Encoding.ASCII.GetBytes(UpdatePolicy.Sign(m, other)));
        using (var c = Client(http))
        {
            var r = await c.CheckAsync();
            Assert.Equal(UpdateCheckStatus.Failed, r.Status);
            Assert.Null(r.Manifest);
            Assert.Contains("Assinatura", r.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Check_rejects_signed_but_invalid_manifest()
    {
        var http = new FakeHttp();
        var m = ManifestJson("0.9.1", url: "https://evil.example.com/x.exe");
        http.Bytes(Feed, m);
        http.Bytes(FeedSig, Sig(m));
        using var c = Client(http);
        var r = await c.CheckAsync();
        Assert.Equal(UpdateCheckStatus.Failed, r.Status);
        Assert.Null(r.Manifest);
    }

    [Fact]
    public async Task Check_follows_github_redirects()
    {
        var http = new FakeHttp();
        var m = ManifestJson("0.9.1");
        var real = new Uri("https://release-assets.githubusercontent.com/m.json");
        var realSig = new Uri("https://release-assets.githubusercontent.com/m.json.sig");
        http.Redirect(Feed, real);
        http.Redirect(FeedSig, realSig);
        http.Bytes(real, m);
        http.Bytes(realSig, Sig(m));
        using var c = Client(http);
        Assert.Equal(UpdateCheckStatus.Available, (await c.CheckAsync()).Status);
    }

    // ------------------------------------------------------------------ baixar

    [Fact]
    public async Task Download_verifies_and_renames()
    {
        var http = new FakeHttp();
        var data = Installer();
        var final = new Uri("https://objects.githubusercontent.com/github-production-release-asset/abc");
        http.Redirect(Asset, final);
        http.Bytes(final, data);
        using var c = Client(http);
        var m = UpdateManifest.Parse(ManifestJson());
        var reports = new List<DownloadProgress>();
        var path = await c.DownloadAsync(m, _dir, new SyncProgress(reports.Add));
        Assert.Equal(Path.Combine(_dir, "0.9.1", "NEXUS-0.9.1-win-x64-instalador.exe"), path);
        Assert.Equal(data, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".partial"));
        Assert.Equal(1.0, reports[^1].Fraction);
        Assert.True(UpdateClient.Matches(path, m.Installer));

        // Segunda vez: reaproveita sem baixar.
        http.Requested.Clear();
        Assert.Equal(path, await c.DownloadAsync(m, _dir));
        Assert.Empty(http.Requested);
    }

    [Fact]
    public async Task Download_rejects_wrong_hash_and_leaves_nothing()
    {
        var http = new FakeHttp();
        var data = Installer();
        data[10] ^= 0xFF;
        http.Bytes(Asset, data);
        using var c = Client(http);
        var m = UpdateManifest.Parse(ManifestJson());
        var ex = await Assert.ThrowsAsync<UpdateException>(() => c.DownloadAsync(m, _dir));
        Assert.Contains("SHA-256", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "0.9.1")));
    }

    [Theory]
    [InlineData(4000)]
    [InlineData(5000)]
    public async Task Download_rejects_wrong_size(int size)
    {
        var http = new FakeHttp();
        http.Bytes(Asset, Installer(size));
        using var c = Client(http);
        var m = UpdateManifest.Parse(ManifestJson());
        await Assert.ThrowsAsync<UpdateException>(() => c.DownloadAsync(m, _dir));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "0.9.1")));
    }

    [Fact]
    public async Task Download_rejects_redirect_to_other_host()
    {
        var http = new FakeHttp();
        var evil = new Uri("https://evil.example.com/NEXUS.exe");
        http.Redirect(Asset, evil);
        http.Bytes(evil, Installer());
        using var c = Client(http);
        var m = UpdateManifest.Parse(ManifestJson());
        await Assert.ThrowsAsync<UpdateException>(() => c.DownloadAsync(m, _dir));
        Assert.DoesNotContain(evil, http.Requested); // nem chega a pedir ao host não permitido
    }

    [Fact]
    public async Task Download_rejects_redirect_to_http()
    {
        var http = new FakeHttp();
        var plain = new Uri("http://objects.githubusercontent.com/x");
        http.Redirect(Asset, plain);
        using var c = Client(http);
        await Assert.ThrowsAsync<UpdateException>(() => c.DownloadAsync(UpdateManifest.Parse(ManifestJson()), _dir));
        Assert.DoesNotContain(plain, http.Requested);
    }

    [Fact]
    public async Task Download_replaces_tampered_existing_file()
    {
        var http = new FakeHttp();
        http.Bytes(Asset, Installer());
        using var c = Client(http);
        var m = UpdateManifest.Parse(ManifestJson());
        var dir = Directory.CreateDirectory(Path.Combine(_dir, "0.9.1"));
        var target = Path.Combine(dir.FullName, "NEXUS-0.9.1-win-x64-instalador.exe");
        File.WriteAllBytes(target, new byte[4096]);
        Assert.False(UpdateClient.Matches(target, m.Installer));
        await c.DownloadAsync(m, _dir);
        Assert.True(UpdateClient.Matches(target, m.Installer));
    }

    [Fact]
    public void CleanOld_removes_only_other_version_folders()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "0.9.0"));
        File.WriteAllText(Path.Combine(_dir, "0.9.0", "a.exe"), "x");
        Directory.CreateDirectory(Path.Combine(_dir, "0.9.1"));
        Directory.CreateDirectory(Path.Combine(_dir, "outra"));
        UpdateClient.CleanOld(_dir, SemVersion.Parse("0.9.1"));
        Assert.False(Directory.Exists(Path.Combine(_dir, "0.9.0")));
        Assert.True(Directory.Exists(Path.Combine(_dir, "0.9.1")));
        Assert.True(Directory.Exists(Path.Combine(_dir, "outra")));
    }

    [Theory]
    [InlineData("NEXUS-0.9.1-win-x64-instalador.exe", true)]
    [InlineData("..\\x.exe", false)]
    [InlineData("a/b.exe", false)]
    [InlineData("x.msi", false)]
    [InlineData(".exe", false)]
    public void Safe_file_names(string name, bool ok) => Assert.Equal(ok, UpdatePolicy.IsSafeFileName(name));

    // ------------------------------------------------------------------ tipo de cópia e instalação

    private static readonly string[] Pf = [@"C:\Program Files", @"C:\Program Files (x86)"];

    [Fact]
    public void Classify_install_kinds()
    {
        Assert.Equal(InstallKind.Development, UpdateInstallation.Classify(false, @"C:\Program Files\NEXUS\", @"C:\Program Files\NEXUS\", Pf, true));
        Assert.Equal(InstallKind.Installed, UpdateInstallation.Classify(true, @"C:\Program Files\NEXUS\", @"c:\program files\nexus", Pf, true));
        Assert.Equal(InstallKind.Portable, UpdateInstallation.Classify(true, @"C:\Users\a\Downloads\NEXUS\", @"C:\Program Files\NEXUS\", Pf, false));
        Assert.Equal(InstallKind.Installed, UpdateInstallation.Classify(true, @"C:\Program Files\NEXUS\", null, Pf, true));
        Assert.Equal(InstallKind.Portable, UpdateInstallation.Classify(true, @"C:\Program Files\NEXUS\", null, Pf, false));
        Assert.Equal(InstallKind.Portable, UpdateInstallation.Classify(true, @"D:\Jogos\NEXUS\", null, Pf, true));
    }

    [Fact]
    public void Mode_installs_only_installed_copies_above_minimum()
    {
        var m = UpdateManifest.Parse(ManifestJson(minimum: "0.9.0"));
        var current = SemVersion.Parse("0.9.0");
        Assert.Equal(UpdateMode.Install, UpdateInstallation.ModeFor(InstallKind.Installed, m, current));
        Assert.Equal(UpdateMode.NotifyOnly, UpdateInstallation.ModeFor(InstallKind.Portable, m, current));
        Assert.Equal(UpdateMode.Disabled, UpdateInstallation.ModeFor(InstallKind.Development, m, current));
        Assert.Equal(UpdateMode.NotifyOnly, UpdateInstallation.ModeFor(InstallKind.Installed, m, SemVersion.Parse("0.8.9")));
    }

    [Fact]
    public void Installer_command_line()
    {
        const string setup = @"C:\Users\a\AppData\Local\NEXUS\updates\0.9.1\NEXUS-0.9.1-win-x64-instalador.exe";
        const string app = @"C:\Program Files\NEXUS\Nexus.Desktop.exe";
        const string log = @"C:\Users\a\AppData\Local\NEXUS\updates\0.9.1\instalacao.log";
        var (file, args) = UpdateInstallation.BuildCommand(setup, log, relaunch: false, app, @"C:\Windows\System32\cmd.exe");
        Assert.Equal(setup, file);
        Assert.Equal($"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /LOG=\"{log}\"", args);

        (file, args) = UpdateInstallation.BuildCommand(setup, log, relaunch: true, app, @"C:\Windows\System32\cmd.exe");
        Assert.Equal(@"C:\Windows\System32\cmd.exe", file);
        Assert.Contains($"start \"\" /wait \"{setup}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1", args, StringComparison.Ordinal);
        Assert.Contains($"if errorlevel 1 start \"\" \"{app}\" --update-failed", args, StringComparison.Ordinal);

        // Caminho com % (cmd expandiria): sem cmd, só o instalador (que reabre o NEXUS no sucesso).
        (file, args) = UpdateInstallation.BuildCommand(@"C:\Users\a%b\x.exe", null, relaunch: true, app, @"C:\Windows\System32\cmd.exe");
        Assert.Equal(@"C:\Users\a%b\x.exe", file);
        Assert.EndsWith("/RELAUNCH=1", args, StringComparison.Ordinal);
    }

    private sealed class SyncProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}
