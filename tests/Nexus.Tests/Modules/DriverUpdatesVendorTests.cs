using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Nexus.Core;
using Nexus.DriverUpdates;
using Nexus.DriverUpdates.Vendor;
using Nexus.Lab.Gpu;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;
using F = Nexus.Tests.Modules.DriverUpdatesVendorFixtures;

namespace Nexus.Tests.Modules;

// ====================================================================== consulta oficial da NVIDIA

[Trait("Category", "Unit")]
public sealed class DriverUpdatesNvidiaLookupTests
{
    [Fact]
    public void Real_lookup_response_for_the_gtx_1650_notebook_is_parsed()
    {
        var (offer, problem) = NvidiaDriverLookup.ParseLookup(F.NvidiaLookupGtx1650NotebookWin11, notebook: true);
        Assert.Null(problem);
        Assert.NotNull(offer);
        Assert.Equal(GpuVendor.Nvidia, offer!.Vendor);
        Assert.Equal("621.01", offer.Version);
        Assert.Equal("https://us.download.nvidia.com/Windows/621.01/621.01-notebook-win10-win11-64bit-international-dch-whql.exe", offer.DownloadUrl.ToString());
        Assert.Equal(1_040_000_000, offer.ApproxBytes);
        Assert.Equal(new DateOnly(2026, 10, 21), offer.ReleaseDate);
        Assert.Equal("Driver GeForce Game Ready", offer.Label);
        Assert.Equal("https://www.nvidia.com/pt-br/drivers/details/282471/", offer.DetailsUrl);
        Assert.True(offer.Notebook);
        // Tamanho exato real do servidor (Content-Length em 01/11/2026) confere com o anunciado "1.04 GB".
        Assert.True(VendorDriverPolicy.SizeMatchesAnnounced(1_037_190_000, offer.ApproxBytes));
    }

    [Fact]
    public void Lookup_without_result_says_so()
    {
        var (offer, problem) = NvidiaDriverLookup.ParseLookup(F.NvidiaLookupNotFound, notebook: false);
        Assert.Null(offer);
        Assert.Contains("não tem driver", problem, StringComparison.Ordinal);
        Assert.Equal("Resposta da NVIDIA ilegível.", NvidiaDriverLookup.ParseLookup("<html>", false).Problem);
    }

    [Theory]
    [InlineData("https://us.download.nvidia.com/Windows/", "https://evil.example.com/Windows/")]
    [InlineData("https://us.download.nvidia.com/Windows/", "http://us.download.nvidia.com/Windows/")]
    [InlineData("\"IsBeta\" : \"0\"", "\"IsBeta\" : \"1\"")]
    [InlineData("\"IsWHQL\" : \"1\"", "\"IsWHQL\" : \"0\"")]
    [InlineData("\"Version\" : \"621.01\"", "\"Version\" : \"621.01; calc\"")]
    [InlineData("\"DownloadURLFileSize\" : \"1.04 GB\"", "\"DownloadURLFileSize\" : \"9 GB\"")]
    public void Lookup_with_anything_suspicious_is_refused(string from, string to)
    {
        var json = F.NvidiaLookupGtx1650NotebookWin11.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(F.NvidiaLookupGtx1650NotebookWin11, json);
        var (offer, problem) = NvidiaDriverLookup.ParseLookup(json, true);
        Assert.Null(offer);
        Assert.False(string.IsNullOrEmpty(problem));
    }

    [Fact]
    public void Products_are_read_from_the_official_lists_and_only_geforce_counts()
    {
        var products = NvidiaDriverLookup.ParseProducts(F.NvidiaSeriesXml, F.NvidiaProductsXml);
        Assert.Contains(products, p => p is { Psid: 115, Pfid: 898, Notebook: true, Name: "GeForce GTX 1650" });
        Assert.Contains(products, p => p is { Psid: 112, Pfid: 897, Notebook: false, Name: "GeForce GTX 1650" });
        Assert.DoesNotContain(products, p => p.Psid == 73); // família que não é GeForce
        Assert.Equal("GeForce GTX 16 Series (Notebooks)", products.First(p => p.Psid == 115).Series);
    }

    [Fact]
    public void Gpu_is_matched_by_name_preferring_the_notebook_series_on_a_laptop()
    {
        var products = NvidiaDriverLookup.ParseProducts(F.NvidiaSeriesXml, F.NvidiaProductsXml);
        // PC do responsável: Acer Nitro AN515-45, "NVIDIA GeForce GTX 1650" (o Windows não diz "Laptop").
        Assert.Equal(new NvidiaProductRef(115, 898, true, "nome", "GeForce GTX 1650"), NvidiaDriverLookup.Resolve(products, "NVIDIA GeForce GTX 1650", 0x1F99, laptop: true));
        Assert.Equal(new NvidiaProductRef(112, 897, false, "nome", "GeForce GTX 1650"), NvidiaDriverLookup.Resolve(products, "NVIDIA GeForce GTX 1650", 0x1F82, laptop: false));
        // "Laptop GPU" no nome vale como notebook mesmo sem bateria detectada.
        var rtx = NvidiaDriverLookup.Resolve(products, "NVIDIA GeForce RTX 3050 Laptop GPU", 0x25A2, laptop: false)!;
        Assert.Equal((123, true, "nome"), (rtx.Psid, rtx.Notebook, rtx.Source));
        // Nome desconhecido: plano B pelo Device ID.
        Assert.Equal(new NvidiaProductRef(115, 898, true, "Device ID"), NvidiaDriverLookup.Resolve(products, "NVIDIA GeForce GTX 1650 (OEM estranho)", 0x1F99, laptop: true));
        Assert.Equal(new NvidiaProductRef(112, 897, false, "Device ID"), NvidiaDriverLookup.Resolve(null, "qualquer", 0x1F82, laptop: false));
    }

    [Theory]
    [InlineData(0x2B85u, false, 131)] // RTX 5090
    [InlineData(0x2860u, true, 129)]  // RTX 4070 notebook
    [InlineData(0x2489u, false, 120)] // RTX 3060 Ti
    [InlineData(0x25A2u, true, 123)]  // RTX 3050 notebook
    [InlineData(0x1F99u, true, 115)]  // GTX 1650 notebook
    [InlineData(0x2184u, false, 112)] // GTX 1660
    [InlineData(0x1C82u, false, 101)] // GTX 1050 Ti (ramo 58x)
    [InlineData(0x1C8Cu, true, 102)]
    [InlineData(0x13C2u, false, 98)]  // GTX 970
    [InlineData(0x1187u, false, 95)]  // GTX 760 (ramo 47x)
    public void Device_id_fallback_maps_to_the_series(uint deviceId, bool notebook, int psid)
    {
        var r = NvidiaDriverLookup.FromDeviceId(deviceId, notebook)!;
        Assert.Equal(psid, r.Psid);
        Assert.Equal(notebook, r.Notebook);
        Assert.Equal("Device ID", r.Source);
    }

    [Fact]
    public void Device_id_zero_has_no_series_and_lookup_url_uses_windows_11_id()
    {
        Assert.Null(NvidiaDriverLookup.FromDeviceId(0, false));
        Assert.Equal(135, NvidiaDriverLookup.OsId(26200));
        Assert.Equal(57, NvidiaDriverLookup.OsId(19045));
        Assert.Equal("https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup&psid=115&pfid=898&osID=135&languageCode=1046&isWHQL=1&dch=1&sort1=0&numberOfResults=1",
            NvidiaDriverLookup.LookupUrl(new NvidiaProductRef(115, 898, true, "nome"), 135).ToString());
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3050 Laptop GPU", true)]
    [InlineData("NVIDIA GeForce GTX 1050 Ti with Max-Q Design", true)]
    [InlineData("NVIDIA GeForce GTX 960M", true)]
    [InlineData("NVIDIA GeForce GTX 1650", false)]
    [InlineData("NVIDIA GeForce RTX 4090", false)]
    public void Notebook_names(string name, bool notebook) => Assert.Equal(notebook, NvidiaDriverLookup.NameLooksNotebook(name));
}

// ====================================================================== página oficial da AMD

[Trait("Category", "Unit")]
public sealed class DriverUpdatesAmdLookupTests
{
    [Fact]
    public void Real_ryzen_5600h_page_gives_the_vega_polaris_package()
    {
        var (offer, problem) = AmdDriverLookup.ParseProductPage(F.AmdRyzen5600hPage, windows11: true);
        Assert.Null(problem);
        Assert.Equal("26.5.2", offer!.Version);
        Assert.Equal("https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-26.5.2-win11-may-vega-polaris.exe", offer.DownloadUrl.ToString());
        Assert.Equal(623_000_000, offer.ApproxBytes);
        Assert.Equal(new DateOnly(2026, 5, 21), offer.ReleaseDate);
        Assert.Equal("Adrenalin 26.5.2 (WHQL Recommended)", offer.Label);
        // Tamanho exato real (Content-Length com Referer em 01/11/2026).
        Assert.True(VendorDriverPolicy.SizeMatchesAnnounced(653_644_328, offer.ApproxBytes));
        Assert.Equal("26.5.2", AmdDriverLookup.ParseProductPage(F.AmdRyzen5600hPage, windows11: false).Offer!.Version);
    }

    [Fact]
    public void Real_rx_7600_page_gives_the_newest_full_installer_not_the_auto_detect_tool()
    {
        var (offer, _) = AmdDriverLookup.ParseProductPage(F.AmdRx7600Page, windows11: true);
        Assert.Equal("26.10.2", offer!.Version);
        Assert.EndsWith("-26.10.2-win11-b.exe", offer.DownloadUrl.ToString(), StringComparison.Ordinal);
        Assert.Equal(new Uri("https://drivers.amd.com/drivers/installer/26.20/beta/amd-software-adrenalin-edition-26.10.2-minimalsetup-261029_web.exe"),
            AmdDriverLookup.ParseAutoDetectUrl(F.AmdRx7600Page));
        Assert.Null(AmdDriverLookup.ParseAutoDetectUrl(F.AmdRyzen5600hPage));
    }

    [Fact]
    public void Page_with_inconsistent_version_or_foreign_host_is_ignored()
    {
        var foreign = F.AmdRyzen5600hPage.Replace("https://drivers.amd.com/drivers/whql", "https://drivers.amd.com.evil.io/drivers/whql", StringComparison.Ordinal);
        Assert.Null(AmdDriverLookup.ParseProductPage(foreign, true).Offer);
        var mismatch = F.AmdRyzen5600hPage.Replace("Adrenalin 26.5.2 (WHQL", "Adrenalin 26.6.1 (WHQL", StringComparison.Ordinal);
        Assert.Null(AmdDriverLookup.ParseProductPage(mismatch, true).Offer);
        Assert.Contains("formato", AmdDriverLookup.ParseProductPage("<html></html>", true).Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x1638u, AmdDriverBranch.VegaPolaris)] // Ryzen 5 5600H (Cezanne) — PC do responsável
    [InlineData(0x15DDu, AmdDriverBranch.VegaPolaris)]
    [InlineData(0x67DFu, AmdDriverBranch.VegaPolaris)] // RX 580
    [InlineData(0x73FFu, AmdDriverBranch.Rdna12)]      // RX 6600
    [InlineData(0x731Fu, AmdDriverBranch.Rdna12)]      // RX 5700 XT
    [InlineData(0x1681u, AmdDriverBranch.Rdna12)]      // Radeon 680M
    [InlineData(0x7480u, AmdDriverBranch.Rdna34)]      // RX 7600
    [InlineData(0x15BFu, AmdDriverBranch.Rdna34)]      // Radeon 780M
    [InlineData(0x7550u, AmdDriverBranch.Rdna34)]      // RX 9070
    [InlineData(0x6798u, AmdDriverBranch.Legacy)]      // HD 7970
    [InlineData(0x0001u, AmdDriverBranch.Unknown)]
    public void Amd_branch_by_device_id(uint deviceId, AmdDriverBranch branch)
    {
        Assert.Equal(branch, AmdDriverLookup.Classify(deviceId));
        Assert.Equal(branch is AmdDriverBranch.Rdna12 or AmdDriverBranch.Rdna34 or AmdDriverBranch.VegaPolaris, AmdDriverLookup.BranchPage(branch) is not null);
    }
}

// ====================================================================== regras puras

[Trait("Category", "Unit")]
public sealed class DriverUpdatesVendorPolicyTests
{
    [Theory]
    [InlineData("https://us.download.nvidia.com/Windows/621.01/x.exe", GpuVendor.Nvidia, true)]
    [InlineData("https://international.download.nvidia.com/Windows/621.01/x.exe", GpuVendor.Nvidia, true)]
    [InlineData("https://download.nvidia.com/Windows/621.01/x.exe", GpuVendor.Nvidia, true)]
    [InlineData("https://drivers.amd.com/drivers/x.exe", GpuVendor.Amd, true)]
    [InlineData("http://us.download.nvidia.com/Windows/621.01/x.exe", GpuVendor.Nvidia, false)]
    [InlineData("https://us.download.nvidia.com:8443/x.exe", GpuVendor.Nvidia, false)]
    [InlineData("https://user:pw@us.download.nvidia.com/x.exe", GpuVendor.Nvidia, false)]
    [InlineData("https://us.download.nvidia.com.evil.com/x.exe", GpuVendor.Nvidia, false)]
    [InlineData("https://www.nvidia.com/x.exe", GpuVendor.Nvidia, false)]
    [InlineData("https://us.download.nvidia.com/Windows/621.01/x.zip", GpuVendor.Nvidia, false)]
    [InlineData("https://drivers.amd.com/drivers/x.exe", GpuVendor.Nvidia, false)]
    [InlineData("https://us.download.nvidia.com/x.exe", GpuVendor.Amd, false)]
    [InlineData("https://www.amd.com/en/resources/support-articles/faqs/Download-Incomplete.html", GpuVendor.Amd, false)]
    [InlineData("https://us.download.nvidia.com/x.exe", GpuVendor.Intel, false)]
    public void Download_host_allow_list(string url, GpuVendor vendor, bool allowed) =>
        Assert.Equal(allowed, VendorDriverPolicy.IsAllowedDownload(vendor, new Uri(url)));

    [Theory]
    [InlineData("https://gfwsl.geforce.com/services_toolkit/x", true)]
    [InlineData("https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3", true)]
    [InlineData("https://www.amd.com/en/support/download/drivers.html", true)]
    [InlineData("http://www.amd.com/", false)]
    [InlineData("https://amd.com.example.org/", false)]
    public void Lookup_host_allow_list(string url, bool allowed) => Assert.Equal(allowed, VendorDriverPolicy.IsAllowedLookup(new Uri(url)));

    [Theory]
    [InlineData("30.0.15.1274", 51274, "512.74")]   // PC do responsável (2022)
    [InlineData("32.0.15.6636", 56636, "566.36")]
    [InlineData("32.0.16.2101", 62101, "621.01")]
    public void Nvidia_windows_version_maps_to_the_last_five_digits(string windows, int key, string vendor)
    {
        Assert.Equal(key, VendorVersions.NvidiaKeyFromWindows(windows));
        Assert.Equal(key, VendorVersions.NvidiaKey(vendor));
        Assert.Equal(vendor, VendorVersions.NvidiaFromWindows(windows));
    }

    [Theory]
    [InlineData(GpuVendor.Nvidia, "512.74", "621.01", -1)]
    [InlineData(GpuVendor.Nvidia, "621.01", "621.01", 0)]
    [InlineData(GpuVendor.Nvidia, "99.10", "100.01", -1)]
    [InlineData(GpuVendor.Amd, "26.5.2", "26.10.2", -1)]
    [InlineData(GpuVendor.Amd, "26.10.2", "26.10.1", 1)]
    public void Vendor_versions_compare_numerically(GpuVendor vendor, string a, string b, int expected) =>
        Assert.Equal(expected, VendorVersions.Compare(vendor, a, b));

    [Fact]
    public void Version_compare_with_unknown_returns_null()
    {
        Assert.Null(VendorVersions.Compare(GpuVendor.Amd, null, "26.5.2"));
        Assert.Null(VendorVersions.Compare(GpuVendor.Nvidia, "27.20.14032.15001", "621.01"));
        Assert.Null(VendorVersions.NvidiaKey("621.1"));
    }

    [Theory]
    [InlineData("1.04 GB", 1_040_000_000L)]
    [InlineData("912.74 MB", 912_740_000L)]
    [InlineData("623 MB", 623_000_000L)]
    [InlineData("1.04%20GB", 1_040_000_000L)]
    [InlineData("muito", null)]
    public void Announced_size(string text, long? bytes) => Assert.Equal(bytes, VendorDriverPolicy.ParseAnnouncedSize(text));

    [Fact]
    public void Signers_and_file_names_are_fixed()
    {
        Assert.True(VendorDriverPolicy.IsAllowedSigner(GpuVendor.Nvidia, "NVIDIA Corporation"));
        Assert.False(VendorDriverPolicy.IsAllowedSigner(GpuVendor.Nvidia, "NVIDIA Corporation Ltd"));
        Assert.False(VendorDriverPolicy.IsAllowedSigner(GpuVendor.Amd, "NVIDIA Corporation"));
        Assert.True(VendorDriverPolicy.IsAllowedSigner(GpuVendor.Amd, "Advanced Micro Devices"));
        Assert.True(VendorDriverPolicy.IsAllowedSigner(GpuVendor.Amd, "Advanced Micro Devices, Inc."));
        Assert.Equal("nvidia-621.01.exe", VendorDriverPolicy.FileName(GpuVendor.Nvidia, "621.01"));
        Assert.Equal("amd-adrenalin-26.5.2.exe", VendorDriverPolicy.FileName(GpuVendor.Amd, "26.5.2"));
        Assert.Throws<ArgumentException>(() => VendorDriverPolicy.FileName(GpuVendor.Nvidia, @"..\..\x"));
        Assert.Throws<ArgumentException>(() => VendorDriverPolicy.FileName(GpuVendor.Amd, "621.01"));
        Assert.Equal(Path.Combine("C:", "Users", "ana", "AppData", "Local", "NEXUS", "drivers"), VendorDriverPolicy.UserDownloadRoot(Path.Combine("C:", "Users", "ana")));
    }

    [Fact]
    public void Space_check_counts_the_installer_and_the_extraction()
    {
        Assert.Null(GpuDriverUpdater.SpaceProblem(null, 1_000_000_000));
        Assert.Null(GpuDriverUpdater.SpaceProblem(10L * 1024 * 1024 * 1024, 1_000_000_000));
        Assert.Contains("Espaço insuficiente", GpuDriverUpdater.SpaceProblem(2L * 1024 * 1024 * 1024, 1_000_000_000), StringComparison.Ordinal);
    }

    [Fact]
    public void Progress_text_shows_percent_speed_and_time_left()
    {
        var text = GpuDriverUpdater.ProgressText(new VendorDownloadProgress(500L * 1024 * 1024, 1000L * 1024 * 1024, 10 * 1024 * 1024));
        Assert.Contains("50", text, StringComparison.Ordinal);
        Assert.Contains("MB/s", text, StringComparison.Ordinal);
        Assert.Contains("faltam 50 s", text, StringComparison.Ordinal);
        Assert.Contains("faltam 2 min", GpuDriverUpdater.ProgressText(new VendorDownloadProgress(0, 1000L * 1024 * 1024, 10 * 1024 * 1024)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(GpuVendor.Nvidia, 0, true, false)]
    [InlineData(GpuVendor.Nvidia, 1, true, true)]
    [InlineData(GpuVendor.Nvidia, 2, false, false)]
    [InlineData(GpuVendor.Amd, 0, true, false)]
    [InlineData(GpuVendor.Amd, 3010, true, true)]
    [InlineData(GpuVendor.Amd, 1, false, false)]
    public void Exit_codes(GpuVendor vendor, int code, bool ok, bool reboot) =>
        Assert.Equal((ok, reboot), VendorDriverInstallHandler.Interpret(vendor, code));

    [Fact]
    public void Silent_switches_are_the_documented_ones()
    {
        Assert.Equal(["-s", "-noreboot", "-noeula"], VendorDriverInstallHandler.SilentArguments(GpuVendor.Nvidia));
        Assert.Equal(["-INSTALL"], VendorDriverInstallHandler.SilentArguments(GpuVendor.Amd));
    }
}

// ====================================================================== download (rede falsa)

internal sealed class FakeHttp : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(Respond(request));
    }

    public static HttpResponseMessage Bytes(byte[] data, HttpStatusCode code = HttpStatusCode.OK, long? rangeFrom = null, long? total = null)
    {
        var r = new HttpResponseMessage(code) { Content = new ByteArrayContent(data) };
        r.Content.Headers.ContentLength = data.Length;
        if (rangeFrom is { } f) r.Content.Headers.ContentRange = new ContentRangeHeaderValue(f, f + data.Length - 1, total!.Value);
        return r;
    }

    public static HttpResponseMessage Redirect(string to)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);
        r.Headers.Location = new Uri(to);
        return r;
    }
}

[Trait("Category", "Unit")]
public sealed class DriverUpdatesDownloadTests : IDisposable
{
    private static readonly Uri Url = new("https://us.download.nvidia.com/Windows/621.01/621.01-notebook-win10-win11-64bit-international-dch-whql.exe");
    private readonly string _dir = Directory.CreateTempSubdirectory("nexus-gpudl-").FullName;
    private readonly byte[] _payload = RandomNumberGenerator.GetBytes(300_000);
    private readonly FakeHttp _http = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Dest => Path.Combine(_dir, "nvidia", "621.01", "nvidia-621.01.exe");

    [Fact]
    public async Task Downloads_through_an_allowed_redirect_and_reports_progress()
    {
        _http.Respond = r => r.RequestUri!.Host == "us.download.nvidia.com"
            ? FakeHttp.Redirect("https://international.download.nvidia.com/Windows/621.01/x.exe")
            : FakeHttp.Bytes(_payload);
        using var client = new VendorDriverClient(_http);
        var reports = new List<VendorDownloadProgress>();
        var path = await client.DownloadAsync(GpuVendor.Nvidia, Url, _payload.Length, Dest, new SyncProgress(reports.Add), CancellationToken.None);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(Dest + ".partial"));
        Assert.Equal(_payload.Length, reports[^1].Received);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(_payload)), VendorDriverClient.Sha256(path));
    }

    [Fact]
    public async Task Redirect_to_a_foreign_host_is_refused_before_reading_anything()
    {
        _http.Respond = r => r.RequestUri!.Host == "us.download.nvidia.com" ? FakeHttp.Redirect("https://cdn.evil.example/x.exe") : FakeHttp.Bytes(_payload);
        using var client = new VendorDriverClient(_http);
        var ex = await Assert.ThrowsAsync<VendorDriverException>(() => client.DownloadAsync(GpuVendor.Nvidia, Url, _payload.Length, Dest, null, CancellationToken.None));
        Assert.Contains("não permitido", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_http.Requests, r => r.RequestUri!.Host == "cdn.evil.example");
        Assert.False(File.Exists(Dest));
    }

    [Fact]
    public async Task Amd_download_sends_the_referer_and_refuses_the_error_page_redirect()
    {
        var amd = new Uri("https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-26.5.2-win11-may-vega-polaris.exe");
        _http.Respond = r => r.Headers.Referrer == AmdDriverLookup.Referer
            ? FakeHttp.Bytes(_payload)
            : FakeHttp.Redirect("https://www.amd.com/en/resources/support-articles/faqs/Download-Incomplete.html");
        using var client = new VendorDriverClient(_http);
        await client.DownloadAsync(GpuVendor.Amd, amd, _payload.Length, Dest, null, CancellationToken.None);
        Assert.All(_http.Requests, r => Assert.Equal(AmdDriverLookup.Referer, r.Headers.Referrer));
    }

    [Fact]
    public async Task Partial_download_resumes_with_range()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Dest)!);
        await File.WriteAllBytesAsync(Dest + ".partial", _payload[..100_000]);
        _http.Respond = r => r.Headers.Range is { } range && range.Ranges.First().From == 100_000
            ? FakeHttp.Bytes(_payload[100_000..], HttpStatusCode.PartialContent, 100_000, _payload.Length)
            : FakeHttp.Bytes(_payload);
        using var client = new VendorDriverClient(_http);
        await client.DownloadAsync(GpuVendor.Nvidia, Url, _payload.Length, Dest, null, CancellationToken.None);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(Dest));
        Assert.Single(_http.Requests);
        Assert.Equal(100_000, _http.Requests[0].Headers.Range!.Ranges.First().From);
    }

    [Fact]
    public async Task Server_ignoring_range_restarts_from_zero()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Dest)!);
        await File.WriteAllBytesAsync(Dest + ".partial", RandomNumberGenerator.GetBytes(50_000)); // lixo: não pode ficar na frente
        _http.Respond = _ => FakeHttp.Bytes(_payload);
        using var client = new VendorDriverClient(_http);
        await client.DownloadAsync(GpuVendor.Nvidia, Url, _payload.Length, Dest, null, CancellationToken.None);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(Dest));
    }

    [Fact]
    public async Task Wrong_size_is_refused()
    {
        _http.Respond = _ => FakeHttp.Bytes(_payload);
        using var client = new VendorDriverClient(_http);
        await Assert.ThrowsAsync<VendorDriverException>(() => client.DownloadAsync(GpuVendor.Nvidia, Url, _payload.Length + 10, Dest, null, CancellationToken.None));
        Assert.False(File.Exists(Dest));
        await Assert.ThrowsAsync<VendorDriverException>(() => client.DownloadAsync(GpuVendor.Nvidia, Url, VendorDriverPolicy.MaxInstallerBytes + 1, Dest, null, CancellationToken.None));
    }

    [Fact]
    public async Task Updater_checks_nvidia_end_to_end_with_the_real_samples()
    {
        _http.Respond = r => r.RequestUri!.ToString() switch
        {
            var u when u.Contains("TypeID=2", StringComparison.Ordinal) => Text(F.NvidiaSeriesXml),
            var u when u.Contains("TypeID=3", StringComparison.Ordinal) => Text(F.NvidiaProductsXml),
            var u when u.Contains("psid=115&pfid=898&osID=135", StringComparison.Ordinal) => Text(F.NvidiaLookupGtx1650NotebookWin11),
            var u when r.Method == HttpMethod.Head && u.StartsWith("https://us.download.nvidia.com/", StringComparison.Ordinal) => Length(1_037_190_000),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        using var updater = new GpuDriverUpdater(new VendorDriverClient(_http), new FakeVerifier());
        var gpu = Gpu(GpuVendor.Nvidia, "NVIDIA GeForce GTX 1650", "30.0.15.1274", "512.74", 0x1F99, laptop: true);
        var check = await updater.CheckAsync(gpu, 26200, CancellationToken.None);
        Assert.Equal(GpuUpdateState.Available, check.State);
        Assert.Equal("621.01", check.Offer!.Version);
        Assert.Equal(1_037_190_000, check.ExactBytes);
        Assert.Contains("512.74", check.Message, StringComparison.Ordinal);
        Assert.Contains("Optimus", check.Note, StringComparison.Ordinal);

        var current = await updater.CheckAsync(gpu with { WindowsVersion = "32.0.16.2101", VendorVersion = "621.01" }, 26200, CancellationToken.None);
        Assert.Equal(GpuUpdateState.UpToDate, current.State);
        Assert.Equal("Em dia: 621.01 é a mais nova.", current.Message);
    }

    [Fact]
    public async Task Updater_checks_amd_vega_igpu_and_reports_offline_in_portuguese()
    {
        _http.Respond = r => r.RequestUri!.AbsolutePath.EndsWith("amd-ryzen-5-5600h.html", StringComparison.Ordinal)
            ? Text(F.AmdRyzen5600hPage)
            : r.Method == HttpMethod.Head ? Length(653_644_328) : new HttpResponseMessage(HttpStatusCode.NotFound);
        using var updater = new GpuDriverUpdater(new VendorDriverClient(_http), new FakeVerifier());
        var igpu = Gpu(GpuVendor.Amd, "AMD Radeon(TM) Graphics", "27.20.14032.15001", null, 0x1638, laptop: true, integrated: true);
        var check = await updater.CheckAsync(igpu, 26200, CancellationToken.None);
        Assert.Equal(GpuUpdateState.Available, check.State);
        Assert.Equal("26.5.2", check.Offer!.Version);
        Assert.Contains("Acer", check.Note, StringComparison.Ordinal);
        Assert.Contains("não informa a versão do Adrenalin", check.Note, StringComparison.Ordinal);

        var offline = new FakeHttp { Respond = _ => throw new HttpRequestException("No such host is known.") };
        using var off = new GpuDriverUpdater(new VendorDriverClient(offline), new FakeVerifier());
        var failed = await off.CheckAsync(igpu, 26200, CancellationToken.None);
        Assert.Equal(GpuUpdateState.Failed, failed.State);
        Assert.Contains("Sem internet", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_signature_after_download_deletes_the_file_and_never_returns_it()
    {
        _http.Respond = _ => FakeHttp.Bytes(_payload);
        var offer = new VendorDriverOffer(GpuVendor.Nvidia, "621.01", Url, _payload.Length, null, null, "x", true);
        var check = new GpuUpdateCheck(GpuUpdateState.Available, "", offer, _payload.Length);
        using var updater = new GpuDriverUpdater(new VendorDriverClient(_http), new FakeVerifier { Result = new SignatureCheck(false, null, "a assinatura não confere com o conteúdo") });
        var ex = await Assert.ThrowsAsync<VendorDriverException>(() => updater.DownloadAsync(check, _dir, null, null, _ => null, CancellationToken.None));
        Assert.Contains("NUNCA", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(VendorDriverPolicy.InstallerPath(VendorDriverPolicy.UserDownloadRoot(_dir), GpuVendor.Nvidia, "621.01")));

        using var ok = new GpuDriverUpdater(new VendorDriverClient(_http), new FakeVerifier());
        var verifying = false;
        var done = await ok.DownloadAsync(check, _dir, null, () => verifying = true, _ => null, CancellationToken.None);
        Assert.True(verifying);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(_payload)), done.Sha256);
        Assert.Equal("NVIDIA Corporation", done.Signer);

        await Assert.ThrowsAsync<VendorDriverException>(() => ok.DownloadAsync(check, _dir, null, null, _ => 1024, CancellationToken.None)); // sem espaço
    }

    internal static GpuDriverStatus Gpu(GpuVendor vendor, string name, string windows, string? vendorVersion, uint deviceId, bool laptop, bool integrated = false) =>
        new(name, vendor, integrated, windows, vendorVersion, new DateOnly(2022, 5, 1), 900, "bad", "velho", DriverInfo.OfficialDownloadPage(vendor), null)
        {
            DeviceId = deviceId,
            Laptop = laptop,
        };

    private static HttpResponseMessage Text(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s) };

    private static HttpResponseMessage Length(long n)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        r.Content.Headers.ContentLength = n;
        return r;
    }

    private sealed class SyncProgress(Action<VendorDownloadProgress> report) : IProgress<VendorDownloadProgress>
    {
        public void Report(VendorDownloadProgress value) => report(value);
    }
}

// ====================================================================== handler (aplicador) com backends falsos

internal sealed class FakeVerifier : ISignatureVerifier
{
    public SignatureCheck Result { get; set; } = new(true, "NVIDIA Corporation", "assinatura válida");
    public List<string> Checked { get; } = [];

    public SignatureCheck Verify(string path)
    {
        Checked.Add(path);
        return Result;
    }
}

internal sealed class FakeRunner(FakeInventory inventory) : IInstallerRunner
{
    public List<(string Exe, IReadOnlyList<string> Args, TimeSpan Timeout, bool ExistedWhenRun)> Runs { get; } = [];
    public int? ExitCode { get; set; }
    public bool TimedOut { get; set; }
    /// <summary>O que o "instalador" faz no inventário: (instância, versão do Windows, pacote AMD).</summary>
    public (string Instance, string Version, string? Package)? Installs { get; set; }

    public InstallerRunResult Run(string executable, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        Runs.Add((executable, arguments, timeout, File.Exists(executable)));
        if (TimedOut) return new InstallerRunResult(null, true);
        if (Installs is { } i)
        {
            var idx = inventory.Devices.FindIndex(d => d.InstanceId == i.Instance);
            inventory.Devices[idx] = inventory.Devices[idx] with { DriverVersion = i.Version, InfName = "oem99.inf", VendorPackageVersion = i.Package };
        }
        return new InstallerRunResult(ExitCode ?? 0, false);
    }
}

internal sealed class FakeProfiles(string? profile) : IUserProfileLocator
{
    public List<string?> Asked { get; } = [];

    public string? UserProfile(string? userSid)
    {
        Asked.Add(userSid);
        return profile;
    }
}

[Trait("Category", "Unit")]
public sealed class DriverUpdatesVendorHandlerTests : IDisposable
{
    private const string NvInstance = @"PCI\VEN_10DE&DEV_1F99&SUBSYS_14E51025&REV_A1\4&1";
    private const string NvMatching = @"pci\ven_10de&dev_1f99&subsys_14e51025";
    private const string AmdInstance = @"PCI\VEN_1002&DEV_1638&SUBSYS_14E51025&REV_C6\4&2";
    private const string AmdMatching = @"pci\ven_1002&dev_1638&subsys_14e51025";
    private static readonly CatalogDocument Catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));

    private readonly string _dir = Directory.CreateTempSubdirectory("nexus-gpuinst-").FullName;
    private readonly string _profile;
    private readonly string _data;
    private readonly FakeInventory _inv = new();
    private readonly FakeTool _tool;
    private readonly FakeClock _clock = new();
    private readonly FakeVerifier _verifier = new();
    private readonly FakeRunner _runner;
    private readonly FakeProfiles _profiles;
    private readonly VendorDriverInstallHandler _handler;

    public DriverUpdatesVendorHandlerTests()
    {
        _profile = Path.Combine(_dir, "Users", "ana");
        _data = Path.Combine(_dir, "ProgramData", "NEXUS");
        var inbox = Path.Combine(_dir, "INF");
        Directory.CreateDirectory(inbox);
        _tool = new FakeTool(_inv, inbox);
        _tool.Store["oem42.inf"] = "nv_dispi.inf";
        _tool.Store["oem43.inf"] = "u0388931.inf";
        _inv.Devices.Add(Nvidia());
        _inv.Devices.Add(Amd());
        _inv.Devices.Add(DriverUpdatesMatchingTests.Audio());
        _tool.RestoresTo[NvMatching] = (NvInstance, "30.0.15.1274", "oem42.inf");
        _tool.RestoresTo[AmdMatching] = (AmdInstance, "27.20.14032.15001", "oem43.inf");
        _runner = new FakeRunner(_inv) { Installs = (NvInstance, "32.0.16.2101", null) };
        _profiles = new FakeProfiles(_profile);
        _handler = new VendorDriverInstallHandler(_inv, _tool, _clock, _verifier, _runner, _profiles, _data);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    internal static DeviceDriver Nvidia(string version = "30.0.15.1274") =>
        new(NvInstance, "NVIDIA GeForce GTX 1650", [@"PCI\VEN_10DE&DEV_1F99&SUBSYS_14E51025&REV_A1", @"PCI\VEN_10DE&DEV_1F99&SUBSYS_14E51025"], [@"PCI\VEN_10DE&DEV_1F99"],
            "Display", DeviceDriver.DisplayClassGuid, "NVIDIA", version, new DateOnly(2022, 5, 1), "NVIDIA", "oem42.inf", NvMatching);

    internal static DeviceDriver Amd(string version = "27.20.14032.15001", string? package = null) =>
        new(AmdInstance, "AMD Radeon(TM) Graphics", [@"PCI\VEN_1002&DEV_1638&SUBSYS_14E51025&REV_C6", @"PCI\VEN_1002&DEV_1638&SUBSYS_14E51025"], [@"PCI\VEN_1002&DEV_1638"],
            "Display", DeviceDriver.DisplayClassGuid, "Advanced Micro Devices, Inc.", version, new DateOnly(2021, 4, 1), "Advanced Micro Devices, Inc.", "oem43.inf", AmdMatching, 0, package);

    /// <summary>Instalador "baixado" na pasta do usuário; devolve o SHA-256.</summary>
    private string Download(GpuVendor vendor, string version, byte[]? content = null)
    {
        var path = VendorDriverPolicy.InstallerPath(VendorDriverPolicy.UserDownloadRoot(_profile), vendor, version);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        content ??= RandomNumberGenerator.GetBytes(4096);
        File.WriteAllBytes(path, content);
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    private static ActionInstance Instance(string vendor, string version, string sha)
    {
        var entry = Catalog.Actions.Single(a => a.Definition.Id == "driverupdates.gpu.install");
        var args = entry.ValidateUserArguments(new Dictionary<string, string> { ["gpuVendor"] = vendor, ["gpuVersion"] = version, ["installerSha256"] = sha }, out var reason);
        Assert.True(args is not null, reason);
        var inst = entry.TryInstantiate(args!, out reason);
        Assert.True(inst is not null, reason);
        return inst!;
    }

    private (IReadOnlyList<ExecutionResult> Results, ActionExecutor Exec, Guid Run) Execute(ActionInstance action)
    {
        var handlers = new HandlerRegistry().Add(_handler);
        var exec = new ActionExecutor(handlers, new InMemoryJournal());
        var run = exec.BeginRun("teste", "1");
        var plan = new DesiredStateResolver(handlers).Resolve([action], T.Ctx);
        return (exec.Execute(run, plan, T.Ctx), exec, run);
    }

    // ------------------------------------------------------------------ catálogo e fiação

    [Fact]
    public void Catalog_entry_is_admin_reversible_and_registered()
    {
        var a = Catalog.Actions.Single(x => x.Definition.Id == "driverupdates.gpu.install");
        Assert.Equal(VendorDriverInstallHandler.Kind, a.Definition.HandlerKind);
        Assert.Equal(PrivilegeLevel.Admin, a.Definition.Privileges);
        Assert.Equal(RebootRequirement.Recommended, a.Definition.Reboot);
        Assert.False(a.Definition.Irreversible);
        Assert.Equal(VendorDriverPolicy.VendorRegex, a.UserParameters["gpuVendor"]);
        Assert.Equal(VendorDriverPolicy.VersionRegex, a.UserParameters["gpuVersion"]);
        Assert.Equal(VendorDriverPolicy.Sha256Regex, a.UserParameters["installerSha256"]);
        Assert.Equal("User", a.TargetTemplate["scope"]); // o aplicador como serviço recebe o SID de quem pediu
        var kinds = DriverUpdatesHandlers.Create(new HandlerEnvironment(new InMemoryRegistryBackend(), _dir, false, true)).Select(h => h.HandlerKind).ToList();
        Assert.Equal([DriverUpdateInstallHandler.Kind, VendorDriverInstallHandler.Kind], kinds);
        Assert.Equal(VendorDriverInstallHandler.StateType, a.DesiredType);
    }

    [Theory]
    [InlineData("intel", "621.01")]
    [InlineData("NVIDIA", "621.01")]
    [InlineData("nvidia", "621.01\n")]
    [InlineData("nvidia", @"..\..\Windows")]
    [InlineData("nvidia", "621")]
    [InlineData("nvidia", "6210.01")]
    [InlineData("amd", "26.5.2.1")]
    public void User_parameters_reject_anything_but_vendor_and_version(string vendor, string version)
    {
        var entry = Catalog.Actions.Single(a => a.Definition.Id == "driverupdates.gpu.install");
        Assert.Null(entry.ValidateUserArguments(new Dictionary<string, string> { ["gpuVendor"] = vendor, ["gpuVersion"] = version, ["installerSha256"] = new string('a', 64) }, out _));
    }

    [Theory]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")] // maiúsculas
    [InlineData("abc")]
    [InlineData(@"C:\Users\ana\Downloads\x.exe")]
    public void User_parameters_reject_a_bad_file_token(string sha)
    {
        var entry = Catalog.Actions.Single(a => a.Definition.Id == "driverupdates.gpu.install");
        Assert.Null(entry.ValidateUserArguments(new Dictionary<string, string> { ["gpuVendor"] = "nvidia", ["gpuVersion"] = "621.01", ["installerSha256"] = sha }, out _));
    }

    [Fact]
    public void Handler_rechecks_the_vendor_specific_version_format()
    {
        // O regex do catálogo aceita os dois formatos; o handler confere o do fabricante.
        var a = _handler.Assess(Instance("amd", "621.01", new string('a', 64)), T.Ctx);
        Assert.Equal(ApplicabilityKind.Blocked, a.Kind);
    }

    // ------------------------------------------------------------------ ida e volta

    [Fact]
    public void Nvidia_install_copies_verifies_backs_up_runs_silently_and_undo_restores()
    {
        var sha = Download(GpuVendor.Nvidia, "621.01");
        Assert.Equal(VendorDriverInstallHandler.Pending, _handler.Detect(Instance("nvidia", "621.01", sha)).Value);
        var (results, exec, run) = Execute(Instance("nvidia", "621.01", sha));
        var r = Assert.Single(results);
        Assert.Equal(ExecutionOutcome.Verified, r.Outcome);

        var (exe, args, timeout, existed) = Assert.Single(_runner.Runs);
        Assert.Equal(VendorDriverPolicy.AdminCopyPath(_data, GpuVendor.Nvidia, "621.01"), exe); // roda a CÓPIA protegida, não o download
        Assert.True(existed);
        Assert.Equal(["-s", "-noreboot", "-noeula"], args);
        Assert.Equal(VendorDriverInstallHandler.NvidiaTimeout, timeout);
        Assert.Equal(exe, Assert.Single(_verifier.Checked)); // assinatura conferida na cópia
        Assert.False(File.Exists(exe)); // cópia apagada depois
        Assert.Equal([null], _profiles.Asked);

        var m = _handler.ReadManifest(GpuVendor.Nvidia, "621.01")!;
        Assert.Equal(RollbackSource.Backup, m.Source);
        Assert.Equal("30.0.15.1274", m.PreviousVersion);
        Assert.Equal("512.74", m.PreviousVendorVersion);
        Assert.Equal(0, m.ExitCode);
        Assert.NotNull(DriverUpdateInstallHandler.FindInf(_handler.BackupDirectory(GpuVendor.Nvidia, "621.01"), "nv_dispi.inf"));

        var undo = Assert.Single(exec.RevertRun(run));
        Assert.Equal(RevertOutcome.Reverted, undo.Outcome);
        Assert.Equal(NvMatching, Assert.Single(_tool.Forced).HardwareId);
        Assert.Equal("30.0.15.1274", _inv.Devices[0].DriverVersion);
        Assert.Equal(VendorDriverInstallHandler.Pending, _handler.Detect(Instance("nvidia", "621.01", sha)).Value);
    }

    [Fact]
    public void Already_current_driver_is_not_reinstalled()
    {
        _inv.Devices[0] = Nvidia("32.0.16.2101");
        var sha = Download(GpuVendor.Nvidia, "621.01");
        Assert.Equal(ExecutionOutcome.AlreadyConfigured, Assert.Single(Execute(Instance("nvidia", "621.01", sha)).Results).Outcome);
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public void Installer_changed_after_confirmation_is_never_run()
    {
        var sha = Download(GpuVendor.Nvidia, "621.01");
        Download(GpuVendor.Nvidia, "621.01", RandomNumberGenerator.GetBytes(4096)); // trocado depois de conferido
        var r = Assert.Single(Execute(Instance("nvidia", "621.01", sha)).Results);
        Assert.Equal(ExecutionOutcome.FailedAndCompensated, r.Outcome);
        Assert.Contains("SHA-256", r.Detail, StringComparison.Ordinal);
        Assert.Empty(_runner.Runs);
        Assert.Empty(_verifier.Checked);
        Assert.False(File.Exists(VendorDriverPolicy.AdminCopyPath(_data, GpuVendor.Nvidia, "621.01")));
    }

    [Fact]
    public void Invalid_signature_is_never_run()
    {
        _verifier.Result = new SignatureCheck(false, null, "a assinatura não confere com o conteúdo (arquivo alterado)");
        var sha = Download(GpuVendor.Nvidia, "621.01");
        var r = Assert.Single(Execute(Instance("nvidia", "621.01", sha)).Results);
        Assert.Equal(ExecutionOutcome.FailedAndCompensated, r.Outcome);
        Assert.Contains("NÃO foi executado", r.Detail, StringComparison.Ordinal);
        Assert.Empty(_runner.Runs);
        Assert.Empty(_tool.Forced);
        Assert.False(File.Exists(VendorDriverPolicy.AdminCopyPath(_data, GpuVendor.Nvidia, "621.01")));
    }

    [Fact]
    public void Valid_signature_from_someone_else_is_never_run()
    {
        _verifier.Result = new SignatureCheck(true, "Fulano Software Ltd", "assinatura válida");
        var sha = Download(GpuVendor.Nvidia, "621.01");
        var r = Assert.Single(Execute(Instance("nvidia", "621.01", sha)).Results);
        Assert.Contains("Fulano", r.Detail, StringComparison.Ordinal);
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public void Missing_download_or_unknown_profile_is_refused_without_running()
    {
        var sha = new string('a', 64);
        Assert.Contains("Baixe de novo", Assert.Single(Execute(Instance("nvidia", "621.01", sha)).Results).Detail, StringComparison.Ordinal);
        var noProfile = new VendorDriverInstallHandler(_inv, _tool, _clock, _verifier, _runner, new FakeProfiles(null), _data);
        Assert.Throws<InvalidOperationException>(() => noProfile.Apply(Instance("nvidia", "621.01", sha)));
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public void Service_mode_resolves_the_profile_from_the_callers_sid()
    {
        var sha = Download(GpuVendor.Nvidia, "621.01");
        var bound = UserProfileScope.Bind(Instance("nvidia", "621.01", sha), "S-1-5-21-1111111111-2222222222-3333333333-1001");
        _handler.Apply(bound);
        Assert.Equal(["S-1-5-21-1111111111-2222222222-3333333333-1001"], _profiles.Asked);
    }

    [Fact]
    public void Failed_exit_code_without_driver_change_fails_and_changes_nothing()
    {
        _runner.Installs = null;
        _runner.ExitCode = 2;
        var sha = Download(GpuVendor.Nvidia, "621.01");
        var r = Assert.Single(Execute(Instance("nvidia", "621.01", sha)).Results);
        Assert.Equal(ExecutionOutcome.FailedAndCompensated, r.Outcome);
        Assert.Contains("código 2", r.Detail, StringComparison.Ordinal);
        Assert.Empty(_tool.Forced); // nada mudou: nada a desfazer
    }

    [Fact]
    public void Exit_code_1_means_reboot_and_counts_as_installed_until_the_next_boot()
    {
        _runner.Installs = null; // a versão só muda depois de reiniciar
        _runner.ExitCode = 1;
        var sha = Download(GpuVendor.Nvidia, "621.01");
        Assert.Equal(ExecutionOutcome.Verified, Assert.Single(Execute(Instance("nvidia", "621.01", sha)).Results).Outcome);
        var state = _handler.Detect(Instance("nvidia", "621.01", sha));
        Assert.Equal("pendente", state.Detail!["reinicio"]);
        Assert.True(_handler.ReadManifest(GpuVendor.Nvidia, "621.01")!.RebootRequired);
        _clock.LastBootUtc = DateTime.UtcNow.AddMinutes(5);
        Assert.Equal(VendorDriverInstallHandler.Pending, _handler.Detect(Instance("nvidia", "621.01", sha)).Value);
    }

    [Fact]
    public void Timeout_is_reported_and_the_installer_is_left_alone()
    {
        _runner.TimedOut = true;
        var sha = Download(GpuVendor.Nvidia, "621.01");
        var r = Assert.Single(Execute(Instance("nvidia", "621.01", sha)).Results);
        Assert.Equal(ExecutionOutcome.FailedAndCompensated, r.Outcome);
        Assert.Contains("15 min", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Amd_install_uses_its_switch_and_detects_by_the_driver_change()
    {
        _verifier.Result = new SignatureCheck(true, "Advanced Micro Devices", "assinatura válida");
        _runner.Installs = (AmdInstance, "31.0.21921.1000", null); // sem RadeonSoftwareVersion
        var sha = Download(GpuVendor.Amd, "26.5.2");
        var (results, exec, run) = Execute(Instance("amd", "26.5.2", sha));
        Assert.Equal(ExecutionOutcome.Verified, Assert.Single(results).Outcome);
        Assert.Equal(["-INSTALL"], _runner.Runs[0].Args);
        Assert.Equal(VendorDriverInstallHandler.AmdTimeout, _runner.Runs[0].Timeout);
        Assert.Equal("driver trocado pela instalação", _handler.Detect(Instance("amd", "26.5.2", sha)).Detail!["conferido"]);
        Assert.Equal(RevertOutcome.Reverted, Assert.Single(exec.RevertRun(run)).Outcome);
        Assert.Equal("27.20.14032.15001", _inv.Devices[1].DriverVersion);
        Assert.Equal(VendorDriverInstallHandler.Pending, _handler.Detect(Instance("amd", "26.5.2", sha)).Value);
    }

    [Fact]
    public void Amd_with_package_version_in_the_registry_compares_it_directly()
    {
        _inv.Devices[1] = Amd(package: "26.5.2");
        Assert.Equal(VendorDriverInstallHandler.Installed, _handler.Detect(Instance("amd", "26.5.2", new string('a', 64))).Value);
        _inv.Devices[1] = Amd(package: "25.8.1");
        Assert.Equal(VendorDriverInstallHandler.Pending, _handler.Detect(Instance("amd", "26.5.2", new string('a', 64))).Value);
    }

    [Fact]
    public void Nvidia_signed_installer_is_refused_for_amd()
    {
        var sha = Download(GpuVendor.Amd, "26.5.2");
        var r = Assert.Single(Execute(Instance("amd", "26.5.2", sha)).Results); // verificador falso responde "NVIDIA Corporation"
        Assert.Contains("NVIDIA Corporation", r.Detail, StringComparison.Ordinal);
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public void Without_the_gpu_the_action_does_not_apply()
    {
        _inv.Devices.RemoveAll(d => d.InstanceId == NvInstance);
        Assert.Equal(ApplicabilityKind.NotApplicable, _handler.Assess(Instance("nvidia", "621.01", new string('a', 64)), T.Ctx).Kind);
    }

    [Fact]
    public void Check_restore_refuses_without_a_backup()
    {
        var a = Instance("nvidia", "621.01", new string('a', 64));
        var original = StateSnapshot.Of(VendorDriverInstallHandler.StateType, VendorDriverInstallHandler.Pending);
        Assert.Contains("Manifesto", _handler.CheckRestore(a, original), StringComparison.Ordinal);
        Assert.Null(_handler.CheckRestore(a, StateSnapshot.Of(VendorDriverInstallHandler.StateType, VendorDriverInstallHandler.Installed)));
    }
}
