using System.Diagnostics;
using System.Net.Http;
using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiSecretary.Desktop.Tests;

public sealed class TtsServerLifecycleTests
{
    private sealed class DummyHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class DummyModelGateway : IModelGateway
    {
        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ModelMessage> messages, LlmCallOptions options, CancellationToken ct = default)
            => Task.FromResult(new ModelResponse("", 0));
    }

    private static (TtsService Tts, string TempDb) CreateTts(bool defaultEnabled = true, bool autoStart = true)
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"aisec_tts_{Guid.NewGuid():N}.db");
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = $"Data Source={tempDb}",
                ["Tts:Enabled"] = defaultEnabled ? "true" : "false",
                ["Tts:AutoStartServer"] = autoStart ? "true" : "false",
                ["Tts:ApiBaseUrl"] = "http://127.0.0.1:9880",
                ["Tts:SoVitsRoot"] = @"C:\AI\GPT-SoVITS"
            })
            .Build();
        var store = new SqliteStore(cfg);
        var settings = new AppSettingsService(cfg);
        var model = new DummyModelGateway();
        var localizer = new UiLocalizer(settings, store, model, NullLogger<UiLocalizer>.Instance);
        var styles = new StyleService();
        var tts = new TtsService(new DummyHttpClientFactory(), model, settings, styles, localizer, NullLogger<TtsService>.Instance);
        return (tts, tempDb);
    }

    private static void CleanupDb(string tempDb)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        try { if (File.Exists(tempDb + "-wal")) File.Delete(tempDb + "-wal"); } catch { }
        try { if (File.Exists(tempDb + "-shm")) File.Delete(tempDb + "-shm"); } catch { }
    }

    [Fact]
    public void IsMatchingSoVitsServerProcess_CatchesVenvChildAndIgnoresUnrelatedPython()
    {
        // 1) .venv launcher stub
        Assert.True(TtsService.IsMatchingSoVitsServerProcess(
            @"C:\AI\GPT-SoVITS\.venv\Scripts\python.exe",
            "\"C:\\AI\\GPT-SoVITS\\.venv\\Scripts\\python.exe\" \"C:\\AI\\GPT-SoVITS\\api_v2.py\" -a 127.0.0.1 -p 9880 -c \"C:\\Users\\ymkz\\AppData\\Local\\AiSecretary.Desktop\\tts\\tts_infer_sana.yaml\"",
            9880,
            @"C:\AI\GPT-SoVITS"));

        // 2) Real 3.1 GB Python311 child spawned by .venv (MainModule does NOT contain "GPT-SoVITS"!)
        Assert.True(TtsService.IsMatchingSoVitsServerProcess(
            @"C:\Users\ymkz\AppData\Local\Programs\Python\Python311\python.exe",
            "\"C:\\Users\\ymkz\\AppData\\Local\\Programs\\Python\\Python311\\python.exe\" \"C:\\AI\\GPT-SoVITS\\api_v2.py\" -a 127.0.0.1 -p 9880 -c \"C:\\Users\\ymkz\\AppData\\Local\\AiSecretary.Desktop\\tts\\tts_infer_sana.yaml\"",
            9880,
            @"C:\AI\GPT-SoVITS"));

        // 3) Another GPT-SoVITS instance on a different port -> leave untouched
        Assert.False(TtsService.IsMatchingSoVitsServerProcess(
            @"C:\Users\ymkz\AppData\Local\Programs\Python\Python311\python.exe",
            "\"C:\\Users\\ymkz\\AppData\\Local\\Programs\\Python\\Python311\\python.exe\" \"C:\\AI\\GPT-SoVITS\\api_v2.py\" -a 127.0.0.1 -p 9881 -c \"tts_infer_other.yaml\"",
            9880,
            @"C:\AI\GPT-SoVITS"));

        // 4) GPT-SoVITS WebUI / training script (not api_v2.py) -> leave untouched
        Assert.False(TtsService.IsMatchingSoVitsServerProcess(
            @"C:\AI\GPT-SoVITS\.venv\Scripts\python.exe",
            "\"C:\\AI\\GPT-SoVITS\\.venv\\Scripts\\python.exe\" \"C:\\AI\\GPT-SoVITS\\webui.py\"",
            9880,
            @"C:\AI\GPT-SoVITS"));

        // 5) Unrelated MCP / user Python scripts -> leave untouched
        Assert.False(TtsService.IsMatchingSoVitsServerProcess(
            @"C:\Users\ymkz\AppData\Local\Programs\Python\Python311\python.exe",
            "\"C:\\Users\\ymkz\\AppData\\Local\\Programs\\Python\\Python311\\python.exe\" C:\\Users\\ymkz\\.gemini\\mcp-servers\\demucs-mcp\\server.py",
            9880,
            @"C:\AI\GPT-SoVITS"));
    }

    [Fact]
    public void ToggleTtsEnabled_StopsServerOnDisable_AndRestartsOnReEnable()
    {
        var (tts, tempDb) = CreateTts(defaultEnabled: true, autoStart: true);
        try
        {
            int stops = 0;
            int starts = 0;
            tts.StopServerHookForTest = () => stops++;
            tts.StartServerHookForTest = () => starts++;

            // 1) Disable TTS -> immediately stops server
            tts.Configure(enabled: false, voiceProfile: "sana_auto", chatMode: "full", manageServerLifecycle: true);
            Assert.False(tts.IsEnabled);
            Assert.Equal(1, stops);
            Assert.Equal(0, starts);

            // 2) Change voice profile while still disabled -> does NOT spuriously start or re-stop server
            tts.Configure(enabled: false, voiceProfile: "rui_calm", chatMode: "smart", manageServerLifecycle: true);
            Assert.Equal(1, stops);
            Assert.Equal(0, starts);

            // 3) Enqueue while disabled -> immediately invokes onBeforePlay so chat bubble never hangs
            bool revealed = false;
            tts.EnqueueJapaneseSpeech("テスト", onBeforePlay: () => revealed = true);
            Assert.True(revealed);

            // 4) Re-enable TTS -> restarts server in background
            tts.Configure(enabled: true, voiceProfile: "rui_calm", chatMode: "smart", manageServerLifecycle: true);
            Assert.True(tts.IsEnabled);
            Assert.Equal(1, stops);
            Assert.Equal(1, starts);
        }
        finally
        {
            tts.Dispose();
            CleanupDb(tempDb);
        }
    }

    [Fact]
    public void NativeWin32Helpers_ReadCurrentCommandLine_AndCreateKillOnCloseJobObject()
    {
        // Verify NtQueryInformationProcess(ProcessCommandLineInformation) works live on Windows
        var cmd = TtsService.TryGetProcessCommandLine(Environment.ProcessId);
        Assert.False(string.IsNullOrWhiteSpace(cmd));

        // Verify Win32 Job Object struct alignment, BELOW_NORMAL priority, and tail-core affinity setup succeed live
        var tailMask = TtsService.ComputeTailCpuAffinityMask(Environment.ProcessorCount, 4, (UIntPtr)Process.GetCurrentProcess().ProcessorAffinity);
        var job = TtsService.CreateKillOnCloseJobObject(setBelowNormalPriority: true, affinityMask: tailMask);
        try
        {
            Assert.NotEqual(IntPtr.Zero, job);
        }
        finally
        {
            TtsService.CloseNativeHandle(job);
        }
    }

    [Fact]
    public void CpuOffloadHelpers_RewriteYamlToCpuFp32_AndComputeTailCoreAffinity()
    {
        const string sampleYaml = """
            custom:
              bert_base_path: GPT_SoVITS/pretrained_models/chinese-roberta-wwm-ext-large
              device: cuda
              is_half: true
              t2s_weights_path: E:\Anime\sarutobi_sana_tts\weights_36\sana_36-e15.ckpt
              version: v2Pro
            """;

        var cpuYaml = TtsService.BuildCpuOverrideYamlContent(sampleYaml);
        Assert.Contains("  device: cpu", cpuYaml);
        Assert.Contains("  is_half: false", cpuYaml);
        Assert.DoesNotContain("device: cuda", cpuYaml);
        Assert.DoesNotContain("is_half: true", cpuYaml);
        Assert.Contains("sana_36-e15.ckpt", cpuYaml);

        // 16-thread CPU (e.g. Ryzen 7 9800X3D), 4 threads -> last 4 logical CPUs (bits 12..15 = 0xF000), leaving cores 0..11 free for games
        var mask16 = TtsService.ComputeTailCpuAffinityMask(16, 4, new UIntPtr(0xFFFF));
        Assert.Equal(new UIntPtr(0xF000), mask16);

        // Low-core machine (4 threads) -> returns 0 (do not restrict affinity)
        var mask4 = TtsService.ComputeTailCpuAffinityMask(4, 4, new UIntPtr(0xF));
        Assert.Equal(UIntPtr.Zero, mask4);
    }
}
