using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NspdRepack.Core;

namespace NspdRepack.UI
{
    public sealed class MainForm : Form
    {
        TextBox _nspd, _keys, _out, _titleId, _keyGen, _sdk, _titleVer;
        CheckBox _verify, _deep, _keep;
        Button _start, _cancel, _open;
        ProgressBar _bar;
        Label _status;
        TextBox _log;

        ToolTip _tips; 
        CancellationTokenSource _cts;
        Task<RepackResult> _runTask;
        string _lastNsp;
        string _toolHeader = "";   
        bool _running;

        public MainForm(string initialNspd)
        {
            Text = "NSPD Repack Tool";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            ClientSize = new Size(860, 660);
            MinimumSize = new Size(720, 560);

            BuildUi();
            InitFields(initialNspd);
            SetupTooltips();
            ShowToolStatus();

            AllowDrop = true;
            foreach (Control c in new Control[] { this, _nspd, _log })
            {
                c.AllowDrop = true;
                c.DragEnter += OnDragEnter;
                c.DragDrop += OnDragDrop;
            }
        }


        void BuildUi()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 6,
                Padding = new Padding(12)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 5; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            var paths = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
            paths.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            paths.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _nspd = AddPathRow(paths, "NSPD folder", BrowseNspd);
            _keys = AddPathRow(paths, "prod.keys", BrowseKeys);
            _out = AddPathRow(paths, "Output folder", BrowseOut);
            root.Controls.Add(paths, 0, 0);

            var adv = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 8, 0, 0) };
            _titleId = AddField(adv, "Title ID", 150);
            _keyGen = AddField(adv, "Key gen", 40);
            _sdk = AddField(adv, "SDK ver", 85);
            _titleVer = AddField(adv, "Title ver", 85);
            adv.Controls.Add(new Label { Text = "(blank Title ID = read from main.npdm)", AutoSize = true, Margin = new Padding(8, 6, 0, 0), ForeColor = SystemColors.GrayText });
            root.Controls.Add(adv, 0, 1);

            var opts = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 4, 0, 0) };
            _verify = AddCheck(opts, "Verify output");
            _deep = AddCheck(opts, "Deep verify (ExeFS round-trip)");
            _keep = AddCheck(opts, "Keep work files");
            _verify.CheckedChanged += (s, e) => _deep.Enabled = _verify.Checked;
            root.Controls.Add(opts, 0, 2);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 8, 0, 8) };
            _start = new Button { Text = "Start repack", AutoSize = true, Padding = new Padding(10, 3, 10, 3) };
            _cancel = new Button { Text = "Cancel", AutoSize = true, Enabled = false };
            _open = new Button { Text = "Show NSP in folder", AutoSize = true, Enabled = false };
            _start.Click += OnStart;
            _cancel.Click += (s, e) => { if (_cts != null) _cts.Cancel(); _cancel.Enabled = false; _status.Text = "Cancelling..."; };
            _open.Click += OnOpen;
            buttons.Controls.Add(_start);
            buttons.Controls.Add(_cancel);
            buttons.Controls.Add(_open);
            root.Controls.Add(buttons, 0, 3);

            var prog = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2 };
            prog.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _bar = new ProgressBar { Dock = DockStyle.Fill, Height = 14, Style = ProgressBarStyle.Continuous, Maximum = 100 };
            _status = new Label { Text = "Select an extracted .nspd folder (or drop one on this window).", AutoSize = true, Margin = new Padding(0, 4, 0, 6) };
            prog.Controls.Add(_bar, 0, 0);
            prog.Controls.Add(_status, 0, 1);
            root.Controls.Add(prog, 0, 4);

            _log = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9F),
                BackColor = Color.FromArgb(16, 21, 28),
                ForeColor = Color.FromArgb(215, 224, 234),
                MaxLength = int.MaxValue
            };
            root.Controls.Add(_log, 0, 5);
        }

        static TextBox AddPathRow(TableLayoutPanel t, string label, Action browse)
        {
            int row = t.RowCount;
            t.RowCount = row + 1;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 10, 0) }, 0, row);
            var tb = new TextBox { Dock = DockStyle.Fill };
            t.Controls.Add(tb, 1, row);
            var b = new Button { Text = "Browse...", AutoSize = true };
            b.Click += (s, e) => browse();
            t.Controls.Add(b, 2, row);
            return tb;
        }

        static TextBox AddField(FlowLayoutPanel p, string label, int width)
        {
            p.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
            var tb = new TextBox { Width = width, Margin = new Padding(0, 3, 12, 3) };
            p.Controls.Add(tb);
            return tb;
        }

        static CheckBox AddCheck(FlowLayoutPanel p, string text)
        {
            var c = new CheckBox { Text = text, AutoSize = true, Margin = new Padding(0, 0, 16, 0) };
            p.Controls.Add(c);
            return c;
        }


        void SetupTooltips()
        {
            _tips = new ToolTip { AutoPopDelay = 30000, InitialDelay = 350, ReshowDelay = 100, ShowAlways = true };

            _tips.SetToolTip(_deep,
                "After building, pulls the ExeFS (main, main.npdm, rtld,\n" +
                "sdk, subsdk) back out of the finished Program NCA and\n" +
                "compares SHA-256 hashes against your NSPD files.\n" +
                "Proves the code inside the NSP is exactly what you put in.\n" +
                "Adds a few seconds. Needs hactool.");

            _tips.SetToolTip(_keep,
                "Keeps the temporary work folder (staged files, hacpack temp\n" +
                "data, extracted ExeFS) after a successful build instead of\n" +
                "deleting it. Only useful for troubleshooting.\n" +
                "Failed or cancelled runs always keep it.");
        }

        void ShowToolStatus()
        {
            string hacpack = ToolLocator.FindTool("hacpack.exe");
            string hactool = ToolLocator.FindTool("hactool.exe");
            string keys = Clean(_keys.Text);
            string header =
                "hacpack.exe : " + Describe(hacpack, "NOT FOUND - this build has no hacpack inside") + Environment.NewLine +
                "hactool.exe : " + Describe(hactool, "not included - verification will be limited (optional)") + Environment.NewLine +
                "prod.keys   : " + Describe(keys, "NOT FOUND - click Browse and pick your prod.keys") + Environment.NewLine +
                "Base RomFS  : " + DescribeRomfs() + Environment.NewLine;
            string initError = EmbeddedTools.InitError;
            if (!string.IsNullOrEmpty(initError))
                header += "WARNING: could not unpack the built-in tools: " + initError + Environment.NewLine;
            _toolHeader = header + Environment.NewLine;
            _log.AppendText(_toolHeader);
        }

        static string Describe(string path, string missing)
        {
            if (string.IsNullOrEmpty(path)) return missing;
            return EmbeddedTools.IsEmbedded(path) ? "built in" : path;
        }

        static string DescribeRomfs()
        {
            if (EmbeddedTools.HasRomfs)
                return "built in (" + EmbeddedTools.RomfsFileCount + " files, " + Repacker.FormatSize(EmbeddedTools.RomfsBytes)
                       + ") - unpacked on the first repack, deleted when the app closes";
            string dev = ToolLocator.FindRomfsFolder();
            if (dev != null) return dev + " (not built in)";
            return "NOT BUNDLED - put it in Tools\\romfs_base next to NspdRepack.csproj and rebuild";
        }

        void InitFields(string initialNspd)
        {
            var d = new RepackOptions();
            _nspd.Text = initialNspd ?? "";
            _keys.Text = ToolLocator.FindKeys() ?? "";
            _out.Text = ToolLocator.DefaultOutputRoot();
            _titleId.Text = "";
            _keyGen.Text = d.KeyGeneration;
            _sdk.Text = d.SdkVersion;
            _titleVer.Text = d.TitleVersion;
            _verify.Checked = d.Verify;
            _deep.Checked = d.DeepVerify;
            _deep.Enabled = d.Verify;
            _keep.Checked = d.KeepWork;
        }

        static string Clean(string t)
        {
            return (t ?? "").Trim().Trim('"');
        }

        RepackOptions BuildOptions()
        {
            return new RepackOptions
            {
                NspdPath = Clean(_nspd.Text),
                OutputRoot = Clean(_out.Text),
                HacpackPath = ToolLocator.FindTool("hacpack.exe") ?? "",
                HactoolPath = ToolLocator.FindTool("hactool.exe") ?? "",
                KeysPath = Clean(_keys.Text),
                RomfsBasePath = "",   
                TitleIdOverride = Clean(_titleId.Text),
                KeyGeneration = Clean(_keyGen.Text).Length > 0 ? Clean(_keyGen.Text) : "1",
                SdkVersion = Clean(_sdk.Text).Length > 0 ? Clean(_sdk.Text) : "000C1100",
                TitleVersion = Clean(_titleVer.Text).Length > 0 ? Clean(_titleVer.Text) : "00000000",
                Verify = _verify.Checked,
                DeepVerify = _deep.Checked,
                KeepWork = _keep.Checked
            };
        }


        void BrowseNspd() { BrowseFolder(_nspd, "Select the extracted .nspd folder"); }
        void BrowseOut() { BrowseFolder(_out, "Select the output folder"); }

        void BrowseFolder(TextBox target, string title)
        {
            using (var d = new FolderBrowserDialog { Description = title })
            {
                string current = Clean(target.Text);
                if (Directory.Exists(current)) d.SelectedPath = current;
                if (d.ShowDialog(this) == DialogResult.OK) target.Text = d.SelectedPath;
            }
        }

        void BrowseKeys()
        {
            using (var d = new OpenFileDialog { Title = "Select prod.keys", Filter = "Key files (*.keys)|*.keys|All files (*.*)|*.*" })
            {
                if (d.ShowDialog(this) == DialogResult.OK) _keys.Text = d.FileName;
            }
        }

        void OnDragEnter(object sender, DragEventArgs e)
        {
            if (!_running && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        void OnDragDrop(object sender, DragEventArgs e)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            if (Directory.Exists(files[0])) _nspd.Text = files[0];
        }


        async void OnStart(object sender, EventArgs e)
        {
            if (_running) return;

            RepackOptions options = BuildOptions();

            _log.Clear();
            _log.AppendText(_toolHeader);
            _lastNsp = null;
            if (_cts != null) _cts.Dispose();
            SetRunning(true);
            _cts = new CancellationTokenSource();
            CancellationToken ct = _cts.Token;
            var repacker = new Repacker(options, AppendLog, OnStage);

            string summary;
            MessageBoxIcon icon;
            try
            {
                _runTask = Task.Run(() => repacker.Run(ct));
                RepackResult result = await _runTask;
                if (IsDisposed) return;
                _lastNsp = result.NspPath;
                _status.Text = "Completed: " + result.NspPath;
                icon = result.Warnings.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information;
                summary = "The NSP was created:\n" + result.NspPath;
                if (result.Warnings.Count > 0) summary += "\n\n" + result.Warnings.Count + " warning(s) - see the log.";
            }
            catch (OperationCanceledException)
            {
                if (IsDisposed) return;
                AppendLog("Cancelled. Partial files were left in the output/_work folders.");
                _status.Text = "Cancelled";
                SetRunning(false);
                return;
            }
            catch (RepackException ex)
            {
                if (IsDisposed) return;
                AppendLog("ERROR: " + ex.Message);
                _status.Text = "Failed";
                summary = ex.Message;
                icon = MessageBoxIcon.Error;
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                AppendLog("UNEXPECTED ERROR: " + ex);
                _status.Text = "Failed";
                summary = ex.Message;
                icon = MessageBoxIcon.Error;
            }

            SetRunning(false);
            MessageBox.Show(this, summary, Text, MessageBoxButtons.OK, icon);
        }

        void SetRunning(bool running)
        {
            _running = running;
            _start.Enabled = !running;
            _cancel.Enabled = running;
            _open.Enabled = !running && _lastNsp != null;
            _bar.Style = running ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            if (!running) _bar.Value = 0;
            foreach (Control c in new Control[] { _nspd, _keys, _out, _titleId, _keyGen, _sdk, _titleVer, _verify, _keep })
                c.Enabled = !running;
            _deep.Enabled = !running && _verify.Checked;
        }

        void AppendLog(string line)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(AppendLog), line); }
                catch (InvalidOperationException) { }
                return;
            }
            _log.AppendText(line + Environment.NewLine);
        }

        void OnStage(int index, int total, string name)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<int, int, string>(OnStage), index, total, name); }
                catch (InvalidOperationException) { }
                return;
            }
            _status.Text = "Step " + index + "/" + total + ": " + name;
            if (_running)
            {
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Value = Math.Max(0, Math.Min(100, index * 100 / Math.Max(1, total)));
            }
        }

        void OnOpen(object sender, EventArgs e)
        {
            if (_lastNsp == null) return;
            try
            {
                if (File.Exists(_lastNsp))
                    Process.Start("explorer.exe", "/select," + ToolRunner.Quote(_lastNsp));
                else
                    Process.Start("explorer.exe", ToolRunner.Quote(Path.GetDirectoryName(_lastNsp)));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_running)
            {
                if (MessageBox.Show(this, "A repack is running. Cancel it and exit?", Text,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }

                Visible = false;
                if (_cts != null) _cts.Cancel();
                ToolRunner.KillAll();
                try { if (_runTask != null) _runTask.Wait(10000); }
                catch (AggregateException) { }   
            }
            base.OnFormClosing(e);
        }
    }
}
