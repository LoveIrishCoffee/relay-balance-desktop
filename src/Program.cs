using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("中转站余额")]
[assembly: AssemblyDescription("自动发现 CC Switch 配置的本地 API 余额监控")]
[assembly: AssemblyCompany("Relay Balance contributors")]
[assembly: AssemblyProduct("Relay Balance Desktop")]
[assembly: AssemblyVersion("1.1.1.0")]
[assembly: AssemblyFileVersion("1.1.1.0")]

namespace RelayBalanceDesktop
{
    public static class Program
    {
        public static Icon AppIcon;
        private static string dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelayBalanceDesktop");
        public static string DataDirectory { get { return dataDirectory; } }
        [STAThread]
        public static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (Stream icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("Payload.Icon")) AppIcon = new Icon(icon);
            if (args.Length == 2 && args[0] == "--self-test") return SelfTest(Path.GetFullPath(args[1]));
            if (args.Length == 2 && args[0] == "--render-preview") return RenderPreview(Path.GetFullPath(args[1]));
            bool first;
            using (Mutex instance = new Mutex(true, "Local\\RelayBalanceDesktop-Main", out first))
            {
                if (!first)
                {
                    try { using (EventWaitHandle signal = EventWaitHandle.OpenExisting("Local\\RelayBalanceDesktop-Show")) signal.Set(); } catch { }
                    return 0;
                }
                using (EventWaitHandle signal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\RelayBalanceDesktop-Show"))
                using (BackendClient backend = new BackendClient())
                using (BalanceForm form = new BalanceForm(backend, false))
                {
                    bool exiting = false;
                    Task signalListener = Task.Run(delegate
                    {
                        while (!Volatile.Read(ref exiting))
                        {
                            signal.WaitOne();
                            if (Volatile.Read(ref exiting)) break;
                            try { if (form.IsHandleCreated && !form.IsDisposed) form.BeginInvoke((Action)form.ShowFromTray); } catch { }
                        }
                    });
                    Application.ThreadException += delegate { MessageBox.Show("界面遇到问题，请退出后重新打开。", "中转站余额", MessageBoxButtons.OK, MessageBoxIcon.Warning); };
                    try { Application.Run(form); }
                    finally { Volatile.Write(ref exiting, true); signal.Set(); signalListener.Wait(1000); backend.Stop(); instance.ReleaseMutex(); }
                }
            }
            return 0;
        }

        private static Snapshot DemoSnapshot()
        {
            string time = DateTime.UtcNow.ToString("o");
            return new Snapshot
            {
                checkedAt = time, intervalSeconds = 300, refreshing = false,
                providers = new List<ProviderSnapshot>
                {
                    new ProviderSnapshot { id="p-00000000000000000001",name="示例中转 A",app="codex",origin="https://relay-a.example",status="ok",remaining=26.7500,unit="USD",todayUsage=1.2500,totalUsage=73.25,threshold=5,current=true,adapter="sub2api",adapterLabel="Sub2API",balanceKindLabel="账户钱包余额",message="示例数据；余额来自站点接口，用量为当前 API Key 实际花费",updatedAt=time,lastSuccessAt=time },
                    new ProviderSnapshot { id="p-00000000000000000002",name="示例中转 B",app="claude",origin="https://relay-b.example",status="ok",remaining=118.4000,unit="CNY",totalUsage=482.10,threshold=5,adapter="newapi-token",adapterLabel="New API · Key 额度",balanceKindLabel="API Key 剩余额度",message="示例数据；按站点公布比例换算，非账户钱包",updatedAt=time,lastSuccessAt=time },
                    new ProviderSnapshot { id="p-00000000000000000003",name="备用账户",app="codex",origin="https://relay-a.example",status="ok",remaining=4.6500,unit="USD",todayUsage=0,totalUsage=95.35,threshold=5,lowBalance=true,adapter="sub2api",adapterLabel="Sub2API",balanceKindLabel="账户钱包余额",message="示例数据；同站点不同配置分别显示",updatedAt=time,lastSuccessAt=time },
                    new ProviderSnapshot { id="p-00000000000000000004",name="未设 Key 限额",app="gemini",origin="https://relay-c.example",status="ok",remaining=null,unit="quota",threshold=5,unlimited=true,adapter="newapi-token",adapterLabel="New API · Key 额度",balanceKindLabel="API Key 剩余额度",message="此 Key 未设限额，不代表账户资金无限",updatedAt=time,lastSuccessAt=time },
                    new ProviderSnapshot { id="p-00000000000000000005",name="待适配站点",app="codex",origin="https://relay-d.example",status="unsupported",threshold=5,adapter="auto",adapterLabel="自动识别",message="适配失败，可点击“适配”重新尝试。手动配置可随时使用",updatedAt=time }
                }
            };
        }
        private static int RenderPreview(string directory)
        {
            Directory.CreateDirectory(directory);
            using (BackendClient backend = new BackendClient())
            using (BalanceForm form = new BalanceForm(backend, true))
            {
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000,-30000); form.ShowInTaskbar = false;
                form.Show(); form.ApplySnapshot(DemoSnapshot()); Application.DoEvents();
                SavePreview(form, Path.Combine(directory,"desktop-preview.png"));
                Snapshot failedFirst = DemoSnapshot();
                ProviderSnapshot failed = failedFirst.providers[failedFirst.providers.Count - 1];
                failedFirst.providers.RemoveAt(failedFirst.providers.Count - 1); failedFirst.providers.Insert(0,failed);
                form.ApplySnapshot(new Snapshot { providers=new List<ProviderSnapshot>(), intervalSeconds=300 });
                form.ApplySnapshot(failedFirst); Application.DoEvents();
                AssertActionReachable((Button)typeof(BalanceForm).GetField("_retryDetectionButton",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form));
                AssertActionReachable((Button)typeof(BalanceForm).GetField("_adapterButton",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form));
                SavePreview(form, Path.Combine(directory,"adaptation-preview.png"));
                form.Close();
            }
            return 0;
        }
        private static void SavePreview(Form form,string filename)
        {
            using(Bitmap image=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(image,new Rectangle(0,0,form.Width,form.Height));image.Save(filename,ImageFormat.Png); }
        }
        // Visible=true alone does not prove that a user can see or click a button.
        private static void AssertActionReachable(Button button)
        {
            if(!button.Visible || !button.Enabled || String.IsNullOrWhiteSpace(button.Text))throw new Exception("action unavailable: "+button.Text);
            if(!button.Parent.ClientRectangle.Contains(button.Bounds))throw new Exception("action clipped: "+button.Text);
            foreach(Control sibling in button.Parent.Controls)
                if(sibling!=button && sibling.Visible && sibling.Bounds.IntersectsWith(button.Bounds))throw new Exception("action overlapped: "+button.Text+" / "+sibling.GetType().Name);
            Point center=button.PointToScreen(new Point(button.Width/2,button.Height/2));
            for(Control child=button;child.Parent!=null;child=child.Parent)
            {
                Control hit=child.Parent.GetChildAtPoint(child.Parent.PointToClient(center),GetChildAtPointSkip.Invisible);
                if(hit!=child)throw new Exception("action click intercepted: "+button.Text);
            }
        }
        private static void TestProviderActions()
        {
            using(BackendClient backend=new BackendClient())
            using(BalanceForm form=new BalanceForm(backend,true))
            {
                form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-30000,-30000);form.ShowInTaskbar=false;form.Show();
                DataGridView grid=(DataGridView)typeof(BalanceForm).GetField("_grid",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form);
                Button retry=(Button)typeof(BalanceForm).GetField("_retryDetectionButton",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form);
                Button manual=(Button)typeof(BalanceForm).GetField("_adapterButton",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form);
                Snapshot snapshot=DemoSnapshot();
                ProviderSnapshot failed=snapshot.providers[snapshot.providers.Count-1];snapshot.providers.RemoveAt(snapshot.providers.Count-1);snapshot.providers.Insert(0,failed);
                form.ApplySnapshot(snapshot);Application.DoEvents();
                foreach(int width in new int[]{form.Width,form.Width-22,form.MinimumSize.Width})
                {
                    form.Width=width;Application.DoEvents();
                    for(int round=0;round<3;round++)
                    {
                        grid.CurrentCell=grid.Rows[0].Cells[0];form.ApplySnapshot(snapshot);Application.DoEvents();
                        AssertActionReachable(retry);AssertActionReachable(manual);
                        grid.CurrentCell=grid.Rows[1].Cells[0];Application.DoEvents();AssertActionReachable(manual);
                    }
                }
                grid.CurrentCell=grid.Rows[0].Cells[0];Application.DoEvents();
                bool opened=false;Exception modalError=null;
                using(System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer())
                {
                    timer.Interval=100;timer.Tick+=delegate {
                        foreach(Form window in Application.OpenForms)
                            if(window!=form && window.GetType().Name=="AdapterDialog") { opened=true;timer.Stop();window.Close();return; }
                        modalError=new Exception("manual configuration did not open");timer.Stop();
                    };
                    timer.Start();manual.PerformClick();timer.Stop();
                }
                if(modalError!=null)throw modalError;
                if(!opened)throw new Exception("manual configuration did not open");
                form.Close();
            }
        }
        private static int SelfTest(string directory)
        {
            dataDirectory = directory;
            Directory.CreateDirectory(directory);
            string report = Path.Combine(directory,"desktop-test.json");
            Environment.SetEnvironmentVariable("RELAY_BALANCE_STATE_DIR", Path.Combine(directory,"state"));
            try
            {
                string runtime = RuntimeBundle.Prepare();
                string fixture = Path.Combine(directory,"fixture.db");
                ProcessStartInfo info = new ProcessStartInfo(Path.Combine(runtime,"node.exe"), "-");
                info.UseShellExecute=false; info.CreateNoWindow=true;info.WindowStyle=ProcessWindowStyle.Hidden;
                info.RedirectStandardInput=true;info.RedirectStandardOutput=true;info.RedirectStandardError=true;
                using (Process create = Process.Start(info))
                {
                    create.StandardInput.Write("const {DatabaseSync}=require('node:sqlite');const d=new DatabaseSync("+new JavaScriptSerializer().Serialize(fixture)+");d.exec('CREATE TABLE IF NOT EXISTS providers(id TEXT,name TEXT,app_type TEXT,settings_config TEXT,is_current INTEGER,meta TEXT)');d.close();");
                    create.StandardInput.Close();if(!create.WaitForExit(15000)||create.ExitCode!=0)throw new Exception("fixture");
                }
                Environment.SetEnvironmentVariable("CC_SWITCH_DB", fixture);
                Snapshot received=null;
                using (ManualResetEvent ready = new ManualResetEvent(false))
                using (ManualResetEvent saved = new ManualResetEvent(false))
                using (BackendClient backend = new BackendClient())
                {
                    backend.SnapshotReceived += delegate(Snapshot data) { received=data;ready.Set(); };
                    backend.SettingsSaved += delegate { saved.Set(); };
                    backend.Start();
                    if(!ready.WaitOne(20000)||!BackendClient.ValidSnapshot(received))throw new Exception("snapshot");
                    if(received.providers.Count!=0)throw new Exception("fixture isolation");
                    backend.SaveSettings(new Dictionary<string,double> { {"p-00000000000000000001",2} },new Dictionary<string,AdapterConfig> { {"p-00000000000000000001",new AdapterConfig {type="auto"}} },600);
                    if(!saved.WaitOne(10000)||received.intervalSeconds!=600)throw new Exception("settings");
                    int worker=backend.WorkerId; backend.Stop();
                    bool alive=false;try { using(Process p=Process.GetProcessById(worker)) alive=!p.HasExited; } catch(ArgumentException) { }
                    if(alive)throw new Exception("worker cleanup");
                }
                if(!BackendClient.ValidSnapshot(DemoSnapshot()))throw new Exception("dynamic snapshot");
                Snapshot invalid=DemoSnapshot();invalid.providers[0].id="unrelated";
                if(BackendClient.ValidSnapshot(invalid))throw new Exception("scope");
                using (BackendClient uiBackend = new BackendClient())
                using (BalanceForm form = new BalanceForm(uiBackend, false))
                {
                    form.StartPosition=FormStartPosition.Manual; form.Location=new Point(-30000,-30000); form.ShowInTaskbar=false;
                    form.Show();
                    DateTime deadline=DateTime.UtcNow.AddSeconds(20);
                    while(!uiBackend.IsRunning && DateTime.UtcNow<deadline) { Application.DoEvents();Thread.Sleep(10); }
                    if(!uiBackend.IsRunning)throw new Exception("UI backend startup");
                    CheckBox notifications=(CheckBox)typeof(BalanceForm).GetField("_notifications",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form);
                    notifications.Checked=true; notifications.Checked=false;
                    if(!File.ReadAllText(Path.Combine(directory,"ui.json")).Contains("false"))throw new Exception("notification persistence");
                    typeof(BalanceForm).GetField("_trayHintShown",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(form,true);
                    form.WindowState=FormWindowState.Minimized;Application.DoEvents();
                    if(form.Visible||form.IsDisposed)throw new Exception("minimize to tray");
                    form.ShowFromTray();Application.DoEvents();
                    if(!form.Visible||form.WindowState!=FormWindowState.Normal)throw new Exception("restore from tray");
                    form.Close();Application.DoEvents();
                    if(form.Visible||form.IsDisposed)throw new Exception("close to tray");
                    int uiWorker=uiBackend.WorkerId;
                    ContextMenuStrip menu=(ContextMenuStrip)typeof(BalanceForm).GetField("_trayMenu",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form);
                    menu.Items[3].PerformClick();Application.DoEvents();
                    if(!form.IsDisposed||uiBackend.IsRunning)throw new Exception("tray exit");
                    bool alive=false;try { using(Process p=Process.GetProcessById(uiWorker)) alive=!p.HasExited; } catch(ArgumentException) { }
                    if(alive)throw new Exception("UI worker cleanup");
                }
                using (BackendClient uiBackend=new BackendClient())
                using (BalanceForm form=new BalanceForm(uiBackend,false))
                {
                    CheckBox notifications=(CheckBox)typeof(BalanceForm).GetField("_notifications",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form);
                    if(notifications.Checked)throw new Exception("notification reload");
                }
                TestProviderActions();
                File.WriteAllText(report,"{\"passed\":true,\"checks\":[\"embedded runtime integrity\",\"empty database isolation\",\"dynamic providers\",\"settings persistence\",\"child process cleanup\",\"snapshot validation\",\"minimize to tray\",\"restore from tray\",\"close to tray\",\"tray exit and worker cleanup\",\"notification preference persistence\",\"adaptation and manual buttons reachable after selection refresh and resize\",\"manual configuration opens\"]}",new UTF8Encoding(false));
                return 0;
            }
            catch (Exception ex) { File.WriteAllText(report,new JavaScriptSerializer().Serialize(new {passed=false,error=ex.Message}),new UTF8Encoding(false));return 1; }
        }
    }
}
