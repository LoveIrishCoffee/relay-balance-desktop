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
[assembly: AssemblyVersion("1.1.4.0")]
[assembly: AssemblyFileVersion("1.1.4.0")]

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
                hiddenProviders = new List<HiddenProviderSnapshot> { new HiddenProviderSnapshot { id="p-00000000000000000006", name="已移出的示例配置 · 可随时恢复", app="codex", origin="https://paused-relay.example" } },
                providers = new List<ProviderSnapshot>
                {
                    new ProviderSnapshot { id="p-00000000000000000001",name="示例中转 A · 高级套餐 · 团队共享账户",app="codex",origin="https://relay-a.example",status="ok",remaining=26.7500,unit="USD",todayUsage=1.2500,totalUsage=73.25,threshold=5,current=true,adapter="sub2api",adapterLabel="Sub2API",balanceKindLabel="账户钱包余额",message="示例数据；余额来自站点接口，用量为当前 API Key 实际花费",updatedAt=time,lastSuccessAt=time },
                    new ProviderSnapshot { id="p-00000000000000000002",name="示例中转 B",app="claude",origin="https://relay-b.example",status="ok",remaining=118.4000,unit="CNY",totalUsage=482.10,threshold=5,adapter="newapi-token",adapterLabel="New API · Key 额度",balanceKindLabel="API Key 剩余额度",message="示例数据；按站点公布比例换算，非账户钱包",updatedAt=time,lastSuccessAt=time },
                    new ProviderSnapshot { id="p-00000000000000000003",name="备用账户",app="codex",origin="https://relay-a.example",status="ok",remaining=4.6500,unit="USD",todayUsage=0,totalUsage=95.35,threshold=5,lowBalance=true,adapter="sub2api",adapterLabel="Sub2API",balanceKindLabel="账户钱包余额",message="示例数据；同站点不同配置分别显示",updatedAt=time,lastSuccessAt=time },
                    new ProviderSnapshot { id="p-00000000000000000004",name="未获取账户余额的示例配置",app="gemini",origin="https://relay-c.example",status="ok",remaining=null,unit="quota",threshold=5,unlimited=true,adapter="newapi-token",adapterLabel="New API · Key 额度",balanceKindLabel="API Key 限额（非余额）",message="API Key未设限额，不代表账户资金无限；当前Key未返回账户余额",updatedAt=time,lastSuccessAt=time },
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
                AssertContentTitleRemoved(form);
                SavePreview(form, Path.Combine(directory,"desktop-preview.png"));
                DataGridView previewGrid=FormField<DataGridView>(form,"_grid"); previewGrid.CurrentCell=previewGrid.Rows[3].Cells[0];Application.DoEvents();
                AssertActionReachable(FormField<Button>(form,"_retryDetectionButton"));
                SavePreview(form,Path.Combine(directory,"account-unavailable-preview.png"));
                CaptureHiddenPreview(form,Path.Combine(directory,"hidden-providers-preview.png"));
                Snapshot manualAmount=DemoSnapshot();manualAmount.providers[0].adapterConfig=new AdapterConfig {type="auto",amountMode="manual",unit="CNY",divisor=500000,balanceKind="account"};manualAmount.providers[0].unit="CNY";manualAmount.providers[0].usageUnit="USD";
                form.ApplySnapshot(manualAmount);previewGrid.CurrentCell=previewGrid.Rows[0].Cells[0];Application.DoEvents();
                CaptureAdapterDialog(form,delegate(Form dialog) { SavePreview(dialog,Path.Combine(directory,"manual-amount-preview.png"));dialog.Close(); });
                manualAmount.providers[0].adapterConfig=new AdapterConfig {type="custom",path="/api/balance",remainingPath="data.balance",unit="CNY",divisor=100,balanceKind="account"};
                form.ApplySnapshot(manualAmount);Application.DoEvents();
                CaptureAdapterDialog(form,delegate(Form dialog) { SavePreview(dialog,Path.Combine(directory,"manual-configuration.png"));dialog.Close(); });
                Snapshot failedFirst = DemoSnapshot();
                ProviderSnapshot failed = failedFirst.providers[failedFirst.providers.Count - 1];
                failedFirst.providers.RemoveAt(failedFirst.providers.Count - 1); failedFirst.providers.Insert(0,failed);
                form.ApplySnapshot(new Snapshot { providers=new List<ProviderSnapshot>(), intervalSeconds=300 });
                form.ApplySnapshot(failedFirst); Application.DoEvents();
                AssertActionReachable((Button)typeof(BalanceForm).GetField("_retryDetectionButton",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form));
                AssertActionReachable((Button)typeof(BalanceForm).GetField("_adapterButton",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form));
                SavePreview(form, Path.Combine(directory,"adaptation-preview.png"));
                failed.status="error";failed.message="示例：连接暂时失败，可点击“适配”重试";
                form.ApplySnapshot(failedFirst);Application.DoEvents();
                AssertActionReachable((Button)typeof(BalanceForm).GetField("_retryDetectionButton",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form));
                SavePreview(form,Path.Combine(directory,"query-retry-preview.png"));
                form.Close();
            }
            return 0;
        }
        private static void SavePreview(Form form,string filename)
        {
            using(Bitmap image=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(image,new Rectangle(0,0,form.Width,form.Height));image.Save(filename,ImageFormat.Png); }
        }
        private static T FormField<T>(BalanceForm form,string name) { return (T)typeof(BalanceForm).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form); }
        private static T DialogField<T>(Form form,string name) { return (T)form.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form); }
        private static void CaptureAdapterDialog(BalanceForm form,Action<Form> action)
        {
            bool opened=false;Exception failure=null;
            using(System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer())
            {
                timer.Interval=100;timer.Tick+=delegate {
                    foreach(Form dialog in Application.OpenForms)
                        if(dialog.GetType().Name=="AdapterDialog")
                        {
                            timer.Stop();opened=true;try { action(dialog); } catch(Exception ex) { failure=ex;dialog.Close(); }return;
                        }
                };
                timer.Start();FormField<Button>(form,"_adapterButton").PerformClick();timer.Stop();
            }
            if(failure!=null)throw failure;if(!opened)throw new Exception("amount configuration dialog not opened");
        }
        private static void AssertContentTitleRemoved(Control parent)
        {
            foreach(Control child in parent.Controls) { if(child is Label && child.Text=="中转站余额")throw new Exception("large content title remains");AssertContentTitleRemoved(child); }
        }
        private static void CaptureHiddenPreview(BalanceForm form,string file)
        {
            bool captured=false;Exception failure=null;
            using(System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer())
            {
                timer.Interval=100;timer.Tick+=delegate {
                    foreach(Form window in Application.OpenForms)
                        if(window.GetType().Name=="HiddenProvidersDialog")
                        {
                            timer.Stop();
                            try { AssertActionReachable((Button)window.GetType().GetField("_restoreButton",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(window));SavePreview(window,file);captured=true; }
                            catch(Exception ex){failure=ex;}
                            window.Close();return;
                        }
                };
                timer.Start();FormField<Button>(form,"_hiddenButton").PerformClick();timer.Stop();
            }
            if(failure!=null)throw failure;if(!captured)throw new Exception("hidden providers preview did not open");
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
                AssertContentTitleRemoved(form);
                foreach(int width in new int[]{form.Width,form.Width-22,form.MinimumSize.Width})
                {
                    form.Width=width;Application.DoEvents();
                    for(int round=0;round<3;round++)
                    {
                        grid.CurrentCell=grid.Rows[0].Cells[0];form.ApplySnapshot(snapshot);Application.DoEvents();
                        AssertActionReachable(retry);AssertActionReachable(manual);AssertActionReachable(FormField<Button>(form,"_hideButton"));AssertActionReachable(FormField<Button>(form,"_hiddenButton"));
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
                foreach(string failedStatus in new string[]{"error","stale"})
                {
                    snapshot.providers[0].status=failedStatus;
                    snapshot.providers[0].adapterConfig=new AdapterConfig { type="custom",path="/balance",remainingPath="data.balance",unit="USD",balanceKind="account",divisor=1 };
                    form.ApplySnapshot(snapshot);Application.DoEvents();AssertActionReachable(retry);AssertActionReachable(manual);
                }
                snapshot.providers[0].status="unsupported";snapshot.providers[0].adapterConfig=new AdapterConfig{type="auto"};
                snapshot.providers[0].name="长名称中转站示例 · 多项目开发与研究团队共享的高级账户 · 按站点配置完整展示名称与应用类型";
                snapshot.providers[1].name="短名称";
                form.ApplySnapshot(snapshot);Application.DoEvents();
                foreach(int width in new int[]{form.Width,form.Width+300,form.MinimumSize.Width})
                {
                    form.Width=width;Application.DoEvents();
                    for(int round=0;round<2;round++)
                    {
                        form.ApplySnapshot(snapshot);Application.DoEvents();
                        for(int i=0;i<grid.Rows.Count;i++)
                        {
                            int required=grid.Rows[i].GetPreferredHeight(i,DataGridViewAutoSizeRowMode.AllCellsExceptHeader,true);
                            if(grid.Rows[i].Height<required)throw new Exception("provider row clips wrapped name or app");
                        }
                        if(grid.Rows[0].Height<=grid.Rows[1].Height)throw new Exception("long provider name does not expand its row");
                    }
                }
                form.Close();
            }
        }
        private static void TestBalancePresentation()
        {
            using(BackendClient backend=new BackendClient())
            using(BalanceForm form=new BalanceForm(backend,true))
            {
                form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-30000,-30000);form.ShowInTaskbar=false;form.Show();
                Snapshot snapshot=DemoSnapshot();form.ApplySnapshot(snapshot);Application.DoEvents();AssertContentTitleRemoved(form);
                DataGridView grid=FormField<DataGridView>(form,"_grid");
                if((string)grid.Rows[3].Cells[2].Value!="未获取账户余额" || (string)grid.Rows[3].Cells[5].Value!="余额未获取")throw new Exception("unlimited key presented as account balance");
                if(!FormField<Label>(form,"_summaryLabel").Text.Contains("2 个需关注"))throw new Exception("missing account balance not flagged");
                grid.CurrentCell=grid.Rows[3].Cells[0];Application.DoEvents();
                if(!FormField<Label>(form,"_details").Text.Contains("当前Key未返回账户余额"))throw new Exception("unlimited key explanation missing");
                Button getBalance=FormField<Button>(form,"_retryDetectionButton");AssertActionReachable(getBalance);
                if(getBalance.Text!="获取余额")throw new Exception("unlimited key retrieval label");
                foreach(bool refreshing in new bool[]{true,false}) {snapshot.refreshing=refreshing;form.ApplySnapshot(snapshot);Application.DoEvents();if(getBalance.Enabled==refreshing)throw new Exception("retrieval refresh guard");}
                Dictionary<string,bool> pending=FormField<Dictionary<string,bool>>(form,"_pendingVisibility");pending[snapshot.providers[3].id]=true;
                typeof(BalanceForm).GetMethod("ShowSelection",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,null);if(getBalance.Enabled)throw new Exception("retrieval visibility guard");pending.Clear();
                snapshot.providers[3].adapterConfig=new AdapterConfig{type="newapi-token",amountMode="manual",unit="CNY",divisor=500000,balanceKind="account"};snapshot.providers[3].unit="CNY";snapshot.providers[3].usageUnit="USD";snapshot.providers[3].todayUsage=1.25;
                form.ApplySnapshot(snapshot);Application.DoEvents();if((string)grid.Rows[3].Cells[2].Value!="未获取账户余额" || !FormField<Label>(form,"_details").Text.Contains("1.25 USD"))throw new Exception("manual amount fabricated balance or changed usage unit");
                snapshot.providers[3].adapter="newapi-account";snapshot.providers[3].balanceKindLabel="账户余额";snapshot.providers[3].remaining=26.75;snapshot.providers[3].unit="USD";
                form.ApplySnapshot(snapshot);Application.DoEvents();if((string)grid.Rows[3].Cells[2].Value!="26.75 USD")throw new Exception("real account balance hidden");
                snapshot.providers[3].adapter="sub2api";snapshot.providers[3].balanceKindLabel="订阅剩余额度";snapshot.providers[3].remaining=null;
                form.ApplySnapshot(snapshot);Application.DoEvents();if((string)grid.Rows[3].Cells[2].Value!="不限额")throw new Exception("unlimited subscription changed");
                snapshot.providers.Clear();form.ApplySnapshot(snapshot);Application.DoEvents();
                AssertActionReachable(FormField<Button>(form,"_hiddenButton"));if(FormField<Button>(form,"_hideButton").Enabled)throw new Exception("empty list remove enabled");
                snapshot.hiddenProviders.Clear();form.ApplySnapshot(snapshot);Application.DoEvents();AssertActionReachable(FormField<Button>(form,"_hiddenButton"));
                form.Close();
            }
            Snapshot invalid=DemoSnapshot();invalid.hiddenProviders[0].id=invalid.providers[0].id;if(BackendClient.ValidSnapshot(invalid))throw new Exception("hidden duplicate accepted");
            invalid=DemoSnapshot();invalid.hiddenProviders[0].id="invalid";if(BackendClient.ValidSnapshot(invalid))throw new Exception("hidden invalid id accepted");
            invalid=DemoSnapshot();invalid.providers[0].adapterConfig=new AdapterConfig{type="auto",amountMode="invalid"};if(BackendClient.ValidSnapshot(invalid))throw new Exception("invalid amount mode accepted");
            invalid.providers[0].adapterConfig=new AdapterConfig{type="auto",amountMode="manual",unit="USD",divisor=1,balanceKind="account"};if(!BackendClient.ValidSnapshot(invalid))throw new Exception("manual amount config rejected");
            invalid.providers[0].adapterConfig.unit="";if(BackendClient.ValidSnapshot(invalid))throw new Exception("incomplete manual amount config accepted");
        }
        private static void TestAmountConfiguration()
        {
            using(BackendClient backend=new BackendClient())
            using(BalanceForm form=new BalanceForm(backend,true))
            {
                form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-30000,-30000);form.ShowInTaskbar=false;form.Show();
                Snapshot snapshot=DemoSnapshot();snapshot.providers[0].adapterConfig=new AdapterConfig{type="newapi-token",amountMode="manual",unit="CNY",divisor=500000,balanceKind="key"};
                snapshot.providers[0].unit="CNY";snapshot.providers[0].usageUnit="USD";form.ApplySnapshot(snapshot);Application.DoEvents();
                DataGridView grid=FormField<DataGridView>(form,"_grid");grid.CurrentCell=grid.Rows[0].Cells[0];Application.DoEvents();
                if(!((string)grid.Rows[0].Cells[3].Value).Contains("手动指定") || !FormField<Label>(form,"_details").Text.Contains("1.25 USD"))throw new Exception("manual scope or usage unit presentation");
                CaptureAdapterDialog(form,delegate(Form dialog) {
                    ComboBox type=DialogField<ComboBox>(dialog,"_type"),mode=DialogField<ComboBox>(dialog,"_amountMode"),kind=DialogField<ComboBox>(dialog,"_kind");
                    TextBox unit=DialogField<TextBox>(dialog,"_unit"),path=DialogField<TextBox>(dialog,"_path"),field=DialogField<TextBox>(dialog,"_remainingPath");NumericUpDown divisor=DialogField<NumericUpDown>(dialog,"_divisor");
                    if(mode.SelectedIndex!=1 || !unit.Enabled || unit.Text!="CNY" || divisor.Value!=500000M || kind.SelectedIndex!=1 || path.Enabled)throw new Exception("built-in manual rule not restored");
                    mode.SelectedIndex=0;if(unit.Enabled)throw new Exception("automatic amount fields enabled");mode.SelectedIndex=1;if(unit.Text!="CNY"||divisor.Value!=500000M)throw new Exception("mode switch cleared amount rule");
                    for(int i=0;i<6;i++){type.SelectedIndex=i;Application.DoEvents();if(mode.SelectedIndex!=1 || !mode.Enabled || !unit.Enabled || unit.Text!="CNY")throw new Exception("built-in protocol lost manual rule");}
                    type.SelectedIndex=6;path.Text="/api/balance";field.Text="data.balance";Application.DoEvents();if(mode.Enabled||mode.SelectedIndex!=1||!path.Enabled||!unit.Enabled)throw new Exception("custom amount schema unavailable");
                    type.SelectedIndex=2;if(mode.SelectedIndex!=1||path.Enabled||unit.Text!="CNY")throw new Exception("return to built-in lost manual mode");
                    mode.SelectedIndex=0;type.SelectedIndex=6;type.SelectedIndex=2;if(mode.SelectedIndex!=0||unit.Enabled||path.Text!="/api/balance"||field.Text!="data.balance")throw new Exception("automatic mode or custom drafts lost");
                    mode.SelectedIndex=1;unit.Text="";((Button)dialog.AcceptButton).PerformClick();if(dialog.IsDisposed||String.IsNullOrWhiteSpace(DialogField<Label>(dialog,"_error").Text))throw new Exception("manual amount allowed missing unit");
                    unit.Text="CNY";divisor.Value=1000M;kind.SelectedIndex=2;AssertActionReachable((Button)dialog.AcceptButton);((Button)dialog.AcceptButton).PerformClick();
                });
                string id=snapshot.providers[0].id;Dictionary<string,AdapterConfig> adapters=FormField<Dictionary<string,AdapterConfig>>(form,"_adapters");AdapterConfig saved=adapters[id];
                if(saved.type!="newapi-token"||saved.amountMode!="manual"||saved.unit!="CNY"||saved.divisor!=1000||saved.balanceKind!="account"||saved.path!=null||saved.remainingPath!=null)throw new Exception("built-in amount config output");
                form.ApplySnapshot(snapshot);Application.DoEvents();if(adapters[id].divisor!=1000||!FormField<HashSet<string>>(form,"_editedAdapters").Contains(id))throw new Exception("snapshot overwrote unsaved amount rule");
                CaptureAdapterDialog(form,delegate(Form dialog) {
                    if(DialogField<ComboBox>(dialog,"_amountMode").SelectedIndex!=1||DialogField<NumericUpDown>(dialog,"_divisor").Value!=1000M)throw new Exception("manual amount edit did not reopen");
                    DialogField<ComboBox>(dialog,"_type").SelectedIndex=6;DialogField<TextBox>(dialog,"_path").Text="/api/balance";DialogField<TextBox>(dialog,"_remainingPath").Text="data.balance";((Button)dialog.AcceptButton).PerformClick();
                });
                saved=adapters[id];if(saved.type!="custom"||saved.amountMode!=null||saved.path!="/api/balance"||saved.remainingPath!="data.balance"||saved.divisor!=1000||saved.balanceKind!="account")throw new Exception("custom legacy amount schema changed");
                CaptureAdapterDialog(form,delegate(Form dialog) {DialogField<ComboBox>(dialog,"_type").SelectedIndex=0;DialogField<ComboBox>(dialog,"_amountMode").SelectedIndex=0;((Button)dialog.AcceptButton).PerformClick();});
                saved=adapters[id];if(saved.type!="auto"||saved.amountMode!="auto"||saved.unit!=null||saved.divisor.HasValue)throw new Exception("automatic amount serialized disabled fields");
                form.Close();
            }
        }
        private static void PumpUntil(Func<bool> condition,int timeout,string failure)
        {
            DateTime deadline=DateTime.UtcNow.AddMilliseconds(timeout);
            while(!condition() && DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(15);}
            Application.DoEvents();if(!condition())throw new Exception(failure);
        }
        private static void RunFixtureScript(string runtime,string script)
        {
            ProcessStartInfo info=new ProcessStartInfo(Path.Combine(runtime,"node.exe"),"-");info.UseShellExecute=false;info.CreateNoWindow=true;info.WindowStyle=ProcessWindowStyle.Hidden;
            info.RedirectStandardInput=true;info.RedirectStandardOutput=true;info.RedirectStandardError=true;
            using(Process process=Process.Start(info)) { process.StandardInput.Write(script);process.StandardInput.Close();if(!process.WaitForExit(15000)||process.ExitCode!=0)throw new Exception("visibility fixture script"); }
        }
        private static void TestVisibilityLifecycle(string runtime,string fixture)
        {
            string fixtureLiteral=new JavaScriptSerializer().Serialize(fixture);
            RunFixtureScript(runtime,"const {DatabaseSync}=require('node:sqlite');const d=new DatabaseSync("+fixtureLiteral+");d.prepare('DELETE FROM providers WHERE id=?').run('desktop-visibility');d.prepare('INSERT INTO providers(id,name,app_type,settings_config,is_current,meta) VALUES(?,?,?,?,?,?)').run('desktop-visibility','可恢复的虚构配置','codex',JSON.stringify({base_url:'https://fixture.invalid'}),0,'{}');d.close();");
            using(BackendClient backend=new BackendClient())
            using(BalanceForm form=new BalanceForm(backend,false))
            {
                int settingsEvents=0;backend.SettingsSaved+=delegate{Interlocked.Increment(ref settingsEvents);};
                form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-30000,-30000);form.ShowInTaskbar=false;form.Show();
                DataGridView grid=FormField<DataGridView>(form,"_grid");PumpUntil(delegate{return grid.Rows.Count==1;},20000,"visibility fixture not loaded");
                string id=(string)grid.Rows[0].Tag;NumericUpDown threshold=FormField<NumericUpDown>(form,"_threshold");threshold.Value=7.1254M;
                ComboBox interval=FormField<ComboBox>(form,"_interval");interval.SelectedIndex=2;
                Dictionary<string,AdapterConfig> adapters=FormField<Dictionary<string,AdapterConfig>>(form,"_adapters");
                HashSet<string> editedAdapters=FormField<HashSet<string>>(form,"_editedAdapters");
                adapters[id]=new AdapterConfig{type="custom",path="/fixture-balance",remainingPath="data.balance",unit="USD",balanceKind="account",divisor=1};editedAdapters.Add(id);
                Button hide=FormField<Button>(form,"_hideButton"),hidden=FormField<Button>(form,"_hiddenButton");AssertActionReachable(hide);hide.PerformClick();
                PumpUntil(delegate{return grid.Rows.Count==0 && hidden.Text.Contains("(1)");},10000,"remove did not update list");AssertActionReachable(hidden);
                if(Volatile.Read(ref settingsEvents)!=0 || !editedAdapters.Contains(id) || !FormField<HashSet<string>>(form,"_editedThresholds").Contains(id))throw new Exception("remove cleared unsaved settings");
                bool restored=false;Exception modalError=null;DateTime deadline=DateTime.UtcNow.AddSeconds(12);bool clicked=false;
                using(System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer())
                {
                    timer.Interval=100;timer.Tick+=delegate {
                        foreach(Form window in Application.OpenForms)
                            if(window.GetType().Name=="HiddenProvidersDialog")
                            {
                                try
                                {
                                    if(!clicked){Button restore=(Button)window.GetType().GetField("_restoreButton",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(window);AssertActionReachable(restore);clicked=true;restore.PerformClick();}
                                    if(grid.Rows.Count==1 && hidden.Text.Contains("(0)")){restored=true;timer.Stop();window.Close();return;}
                                    if(DateTime.UtcNow>deadline)throw new Exception("restore did not update list");
                                }
                                catch(Exception ex){modalError=ex;timer.Stop();window.Close();return;}
                                break;
                            }
                    };
                    timer.Start();hidden.PerformClick();timer.Stop();
                }
                if(modalError!=null)throw modalError;if(!restored)throw new Exception("restore dialog not opened");
                if(threshold.Value!=7.1254M || interval.SelectedIndex!=2 || adapters[id].type!="custom" || !editedAdapters.Contains(id) || Volatile.Read(ref settingsEvents)!=0)throw new Exception("restore lost unsaved settings");
                AssertActionReachable(hide);
                ContextMenuStrip menu=FormField<ContextMenuStrip>(form,"_trayMenu");menu.Items[3].PerformClick();Application.DoEvents();if(!form.IsDisposed||backend.IsRunning)throw new Exception("visibility UI cleanup");
            }
            RunFixtureScript(runtime,"const {DatabaseSync}=require('node:sqlite');const d=new DatabaseSync("+fixtureLiteral+",{readOnly:true});const row=d.prepare('SELECT settings_config FROM providers WHERE id=?').get('desktop-visibility');if(!row||JSON.parse(row.settings_config).base_url!=='https://fixture.invalid')process.exit(1);d.close();");
            RunFixtureScript(runtime,"const {DatabaseSync}=require('node:sqlite');const d=new DatabaseSync("+fixtureLiteral+");d.prepare('DELETE FROM providers WHERE id=?').run('desktop-visibility');d.close();");
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
                TestBalancePresentation();TestAmountConfiguration();TestVisibilityLifecycle(runtime,fixture);
                File.WriteAllText(report,"{\"passed\":true,\"checks\":[\"embedded runtime integrity\",\"empty database isolation\",\"dynamic providers\",\"settings persistence\",\"child process cleanup\",\"snapshot validation\",\"minimize to tray\",\"restore from tray\",\"close to tray\",\"tray exit and worker cleanup\",\"notification preference persistence\",\"adaptation and manual buttons reachable after selection refresh and resize\",\"manual configuration opens\",\"wrapped provider names and apps fit after refresh and resize\",\"content title removed\",\"hidden providers validated\",\"remove and restore through actual buttons\",\"visibility preserves unsaved settings\",\"visibility does not modify CC Switch\",\"unlimited key is not an account balance\",\"real account balance and unlimited subscription preserved\",\"missing balance retrieval action and busy guards\",\"built-in manual amount round trip and mode switching\",\"manual amount validation and custom compatibility\",\"usage unit remains independent of manual balance unit\"]}",new UTF8Encoding(false));
                return 0;
            }
            catch (Exception ex) { File.WriteAllText(report,new JavaScriptSerializer().Serialize(new {passed=false,error=ex.Message}),new UTF8Encoding(false));return 1; }
        }
    }
}
