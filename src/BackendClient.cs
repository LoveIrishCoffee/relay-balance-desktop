using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RelayBalanceDesktop
{
    public sealed class AdapterConfig
    {
        public string type { get; set; }
        public string path { get; set; }
        public string remainingPath { get; set; }
        public string unit { get; set; }
        public string balanceKind { get; set; }
        public double? divisor { get; set; }
    }
    public sealed class ProviderSnapshot
    {
        public string id { get; set; }
        public string name { get; set; }
        public string app { get; set; }
        public string origin { get; set; }
        public string adapter { get; set; }
        public string adapterLabel { get; set; }
        public AdapterConfig adapterConfig { get; set; }
        public bool unlimited { get; set; }
        public string status { get; set; }
        public string unit { get; set; }
        public string balanceKindLabel { get; set; }
        public string message { get; set; }
        public string updatedAt { get; set; }
        public string lastSuccessAt { get; set; }
        public bool current { get; set; }
        public bool lowBalance { get; set; }
        public double? remaining { get; set; }
        public double? todayUsage { get; set; }
        public double? totalUsage { get; set; }
        public double threshold { get; set; }
    }
    public sealed class HiddenProviderSnapshot
    {
        public string id { get; set; }
        public string name { get; set; }
        public string app { get; set; }
        public string origin { get; set; }
    }
    public sealed class Snapshot
    {
        public List<ProviderSnapshot> providers { get; set; }
        public List<HiddenProviderSnapshot> hiddenProviders { get; set; }
        public string checkedAt { get; set; }
        public string message { get; set; }
        public int intervalSeconds { get; set; }
        public bool refreshing { get; set; }
    }
    public sealed class BackendMessage
    {
        public string type { get; set; }
        public object data { get; set; }
    }
    public sealed class BackendClient : IDisposable
    {
        public event Action<Snapshot> SnapshotReceived;
        public event Action<string> ErrorReceived;
        public event Action SettingsSaved;
        private readonly object gate = new object();
        private Process process;
        private bool starting;
        private bool stopped;
        public bool IsRunning { get { lock (gate) return !stopped && process != null && !process.HasExited; } }
        public int WorkerId { get { lock (gate) return stopped || process == null || process.HasExited ? 0 : process.Id; } }

        public void Start()
        {
            lock (gate) { if (starting || IsRunning || stopped) return; starting = true; }
            Task.Run((Action)StartCore);
        }
        private void StartCore()
        {
            try
            {
                string runtime = RuntimeBundle.Prepare();
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = Path.Combine(runtime, "node.exe");
                info.Arguments = "\"" + Path.Combine(runtime, "desktop-worker.mjs") + "\"";
                info.WorkingDirectory = runtime;
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.WindowStyle = ProcessWindowStyle.Hidden;
                info.RedirectStandardInput = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                info.StandardOutputEncoding = Encoding.UTF8;
                info.StandardErrorEncoding = Encoding.UTF8;
                if (String.IsNullOrWhiteSpace(info.EnvironmentVariables["RELAY_BALANCE_STATE_DIR"]))
                    info.EnvironmentVariables["RELAY_BALANCE_STATE_DIR"] = Path.Combine(Program.DataDirectory, "state");
                // Environment inherited from development terminals must not load arbitrary Node hooks.
                info.EnvironmentVariables.Remove("NODE_OPTIONS");
                info.EnvironmentVariables.Remove("NODE_PATH");
                info.EnvironmentVariables.Remove("NODE_REPL_EXTERNAL_MODULE");
                Process child = new Process(); child.StartInfo = info; child.EnableRaisingEvents = true;
                child.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args) { ReadMessage(args.Data); };
                child.ErrorDataReceived += delegate { /* Raw stderr never goes to UI or logs. */ };
                child.Exited += delegate { if (!stopped) RaiseError("余额服务已停止，请点击刷新重新启动"); };
                lock (gate)
                {
                    if (stopped) { starting = false; child.Dispose(); return; }
                    if (process != null) process.Dispose();
                    child.Start(); process = child;
                    child.BeginOutputReadLine(); child.BeginErrorReadLine(); starting = false;
                }
            }
            catch { lock (gate) starting = false; RaiseError("启动失败，请检查本机应用目录权限或重新打开软件"); }
        }
        private void ReadMessage(string line)
        {
            if (String.IsNullOrEmpty(line) || line.Length > 4194304 || stopped) return;
            try
            {
                JavaScriptSerializer json = new JavaScriptSerializer();
                json.MaxJsonLength = 4194304;
                BackendMessage message = json.Deserialize<BackendMessage>(line);
                if (message.type == "snapshot" || message.type == "settings_saved")
                {
                    Snapshot snapshot = json.ConvertToType<Snapshot>(message.data);
                    if (!ValidSnapshot(snapshot)) { RaiseError("收到无法识别的余额数据"); return; }
                    Action<Snapshot> action = SnapshotReceived; if (action != null) action(snapshot);
                    if (message.type == "settings_saved") { Action saved = SettingsSaved; if (saved != null) saved(); }
                }
                else if (message.type == "error") RaiseError("查询或保存失败，请稍后刷新重试");
            }
            catch { RaiseError("收到无法识别的余额数据，请重新刷新"); }
        }
        public static bool ValidSnapshot(Snapshot data)
        {
            if (data == null || data.providers == null || data.providers.Count + (data.hiddenProviders == null ? 0 : data.hiddenProviders.Count) > 1000 || data.intervalSeconds < 60 || data.intervalSeconds > 86400) return false;
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ProviderSnapshot p in data.providers)
            {
                if (p == null || String.IsNullOrEmpty(p.id) || !Regex.IsMatch(p.id, "^p-[0-9a-f]{20}$", RegexOptions.CultureInvariant) || !seen.Add(p.id)) return false;
                if (!Finite(p.remaining) || !Finite(p.todayUsage) || !Finite(p.totalUsage) || !Finite(p.threshold) || p.threshold < 0) return false;
                if (p.status != "pending" && p.status != "ok" && p.status != "missing" && p.status != "unsupported" && p.status != "error" && p.status != "stale" && p.status != "disabled") return false;
                if (p.adapterConfig != null && p.adapterConfig.divisor.HasValue && (!Finite(p.adapterConfig.divisor) || p.adapterConfig.divisor.Value <= 0)) return false;
            }
            if (data.hiddenProviders != null)
                foreach (HiddenProviderSnapshot hidden in data.hiddenProviders)
                    if (hidden == null || String.IsNullOrEmpty(hidden.id) || !Regex.IsMatch(hidden.id, "^p-[0-9a-f]{20}$", RegexOptions.CultureInvariant) || !seen.Add(hidden.id)) return false;
            return true;
        }
        private static bool Finite(double? value) { return !value.HasValue || (!Double.IsNaN(value.Value) && !Double.IsInfinity(value.Value)); }
        private void RaiseError(string message) { Action<string> action = ErrorReceived; if (action != null && !stopped) action(message); }
        private void Send(object request)
        {
            try
            {
                lock (gate)
                {
                    if (stopped) return;
                    if (process == null || process.HasExited) { Start(); RaiseError("正在启动余额服务，请稍后重试"); return; }
                    process.StandardInput.WriteLine(new JavaScriptSerializer().Serialize(request));
                    process.StandardInput.Flush();
                }
            }
            catch { RaiseError("余额服务暂不可用，请重新打开软件"); }
        }
        public void Refresh() { Send(new { method = "refresh" }); }
        public void RetryDetection(string id)
        {
            if (String.IsNullOrEmpty(id) || !Regex.IsMatch(id, "^p-[0-9a-f]{20}$", RegexOptions.CultureInvariant)) { RaiseError("请选择有效的中转站配置后重新适配"); return; }
            Send(new { method = "detect", providerId = id });
        }
        public void SaveSettings(Dictionary<string, double> thresholds, Dictionary<string, AdapterConfig> adapters, int intervalSeconds)
        {
            Send(new { method = "settings", settings = new { intervalSeconds = intervalSeconds, thresholds = thresholds, adapters = adapters } });
        }
        public void SetProviderHidden(string id, bool hidden)
        {
            if (String.IsNullOrEmpty(id) || !Regex.IsMatch(id, "^p-[0-9a-f]{20}$", RegexOptions.CultureInvariant)) { RaiseError("请选择有效的中转站配置"); return; }
            Send(new { method = "visibility", providerId = id, hidden = hidden });
        }
        public void Stop()
        {
            Process child;
            lock (gate)
            {
                if (stopped) return; stopped = true; child = process; process = null;
                if (child == null) return;
                try { if (!child.HasExited) { child.StandardInput.WriteLine("{\"method\":\"stop\"}"); child.StandardInput.Flush(); child.StandardInput.Close(); } } catch { }
            }
            try { if (!child.WaitForExit(2500)) child.Kill(); } catch { }
            child.Dispose();
        }
        public void Dispose() { Stop(); }
    }
}
