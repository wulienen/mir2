using System.Diagnostics;
using System.Net;
using Client;
using Microsoft.Web.WebView2.Core;
using System.Net.Http.Headers;
using System.Net.Http.Handlers;
using Client.Utils;
using System.Drawing.Drawing2D;

namespace Launcher
{
    public partial class AMain : Form
    {
        private const int ProgressBarMaximum = 550;

        long _totalBytes, _completedBytes;
        private int _fileCount, _currentCount;

        public bool Completed, Checked, CleanFiles, LabelSwitch, ErrorFound;

        public List<FileInformation> OldList;
        public Queue<FileInformation> DownloadList = new Queue<FileInformation>();
        public List<Download> ActiveDownloads = new List<Download>();

        private Stopwatch _stopwatch = Stopwatch.StartNew();

        public Thread _workThread;

        private bool dragging = false;
        private Point dragCursorPoint;
        private Point dragFormPoint;

        private Config ConfigForm = new Config();

        private bool Restart = false;

        private Panel _statusPanel;
        private Button _checkUpdateButton;
        private bool _launchHovered;
        private bool _launchPressed;

        public AMain()
        {
            InitializeComponent();
            InitializeLauncherLayout();

            BackColor = Color.FromArgb(1, 0, 0);
            TransparencyKey = Color.FromArgb(1, 0, 0);
        }

        private void InitializeLauncherLayout()
        {
            AutoScaleMode = AutoScaleMode.None;
            Text = "游戏启动器";

            _statusPanel = new Panel
            {
                BackColor = Color.FromArgb(21, 29, 38),
                Bounds = new Rectangle(7, 458, 790, 94)
            };

            Font statusFont = new Font("Microsoft YaHei UI", 8.25F, FontStyle.Regular, GraphicsUnit.Point);
            Color secondaryText = Color.FromArgb(160, 168, 176);

            _statusPanel.Controls.Add(CreateCaptionLabel("文件", new Rectangle(7, 4, 40, 21), statusFont, secondaryText));
            _statusPanel.Controls.Add(CreateCaptionLabel("当前", new Rectangle(7, 29, 40, 17), statusFont, secondaryText));
            _statusPanel.Controls.Add(CreateCaptionLabel("总计", new Rectangle(7, 47, 40, 17), statusFont, secondaryText));

            Panel currentTrack = CreateProgressTrack(new Rectangle(51, 31, 558, 13));
            Panel totalTrack = CreateProgressTrack(new Rectangle(51, 49, 558, 13));
            _statusPanel.Controls.Add(currentTrack);
            _statusPanel.Controls.Add(totalTrack);

            CurrentFile_label.Parent = _statusPanel;
            CurrentFile_label.Bounds = new Rectangle(51, 4, 255, 21);
            CurrentFile_label.Font = statusFont;
            CurrentFile_label.ForeColor = secondaryText;
            CurrentFile_label.Visible = true;

            SpeedLabel.Parent = _statusPanel;
            SpeedLabel.Bounds = new Rectangle(310, 4, 80, 21);
            SpeedLabel.Font = statusFont;
            SpeedLabel.TextAlign = ContentAlignment.MiddleRight;

            ActionLabel.Parent = _statusPanel;
            ActionLabel.Bounds = new Rectangle(394, 4, 116, 21);
            ActionLabel.Font = statusFont;
            ActionLabel.TextAlign = ContentAlignment.MiddleRight;

            CurrentPercent_label.Parent = _statusPanel;
            CurrentPercent_label.Bounds = new Rectangle(610, 28, 40, 17);
            CurrentPercent_label.Font = statusFont;
            CurrentPercent_label.TextAlign = ContentAlignment.MiddleRight;
            CurrentPercent_label.Visible = true;

            TotalPercent_label.Parent = _statusPanel;
            TotalPercent_label.Bounds = new Rectangle(610, 46, 40, 17);
            TotalPercent_label.Font = statusFont;
            TotalPercent_label.TextAlign = ContentAlignment.MiddleRight;
            TotalPercent_label.Visible = true;

            ProgressCurrent_pb.Parent = _statusPanel;
            ProgressCurrent_pb.Location = new Point(51, 31);
            ProgressCurrent_pb.Height = 13;
            ProgressCurrent_pb.Anchor = AnchorStyles.None;

            TotalProg_pb.Parent = _statusPanel;
            TotalProg_pb.Location = new Point(51, 49);
            TotalProg_pb.Height = 13;
            TotalProg_pb.Anchor = AnchorStyles.None;

            ProgEnd_pb.Parent = _statusPanel;
            ProgEnd_pb.Size = new Size(6, 13);
            ProgEnd_pb.Anchor = AnchorStyles.None;

            ProgTotalEnd_pb.Parent = _statusPanel;
            ProgTotalEnd_pb.Size = new Size(6, 13);
            ProgTotalEnd_pb.Anchor = AnchorStyles.None;

            Launch_pb.Parent = _statusPanel;
            Launch_pb.Bounds = new Rectangle(652, 15, 115, 53);
            Launch_pb.Anchor = AnchorStyles.None;
            Launch_pb.Image = null;
            Launch_pb.Paint += Launch_pb_Paint;
            Launch_pb.EnabledChanged += (_, _) => Launch_pb.Invalidate();

            _checkUpdateButton = new Button
            {
                Bounds = new Rectangle(514, 3, 94, 23),
                BackColor = Color.FromArgb(31, 44, 55),
                Cursor = Cursors.Hand,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei UI", 8.25F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.White,
                Text = "检查更新",
                UseVisualStyleBackColor = false
            };
            _checkUpdateButton.FlatAppearance.BorderColor = Color.FromArgb(58, 76, 89);
            _checkUpdateButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(40, 57, 70);
            _checkUpdateButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(15, 23, 30);
            _checkUpdateButton.Click += (_, _) => BeginUpdate();
            _statusPanel.Controls.Add(_checkUpdateButton);

            Credit_label.Parent = _statusPanel;
            Credit_label.AutoSize = false;
            Credit_label.Bounds = new Rectangle(7, 74, 210, 16);
            Credit_label.Font = statusFont;
            Credit_label.Text = "基于 Crystal M2";

            Version_label.Parent = _statusPanel;
            Version_label.Bounds = new Rectangle(420, 74, 347, 16);
            Version_label.Font = statusFont;

            Controls.Add(_statusPanel);
            _statusPanel.BringToFront();
        }

        private static Label CreateCaptionLabel(string text, Rectangle bounds, Font font, Color color)
        {
            return new Label
            {
                BackColor = Color.Transparent,
                Bounds = bounds,
                Font = font,
                ForeColor = color,
                Text = text,
                TextAlign = ContentAlignment.MiddleRight
            };
        }

        private static Panel CreateProgressTrack(Rectangle bounds)
        {
            return new Panel
            {
                BackColor = Color.FromArgb(7, 9, 11),
                BorderStyle = BorderStyle.FixedSingle,
                Bounds = bounds
            };
        }

        public void BeginUpdate(bool cleanFiles = false)
        {
            if (_workThread?.IsAlive == true) return;

            Completed = false;
            Checked = false;
            CleanFiles = cleanFiles;
            ErrorFound = false;
            Restart = false;
            errorcount = 0;
            _totalBytes = 0;
            _completedBytes = 0;
            _fileCount = 0;
            _currentCount = 0;
            OldList = null;
            DownloadList.Clear();
            ActiveDownloads.Clear();

            SetProgress(ProgressCurrent_pb, 0);
            SetProgress(TotalProg_pb, 0);
            CurrentPercent_label.Text = "0%";
            TotalPercent_label.Text = "0%";
            CurrentFile_label.Text = cleanFiles ? "正在准备清理文件..." : "正在检查文件...";
            SpeedLabel.Text = string.Empty;
            ActionLabel.Text = string.Empty;
            SpeedLabel.Visible = false;
            ActionLabel.Visible = false;
            Launch_pb.Enabled = false;
            _checkUpdateButton.Enabled = false;
            InterfaceTimer.Enabled = true;

            _workThread = new Thread(Start) { IsBackground = true };
            _workThread.Start();
        }

        private static void SetProgress(PictureBox progress, int width)
        {
            progress.Width = Math.Clamp(width, 0, ProgressBarMaximum);
        }

        private void Launch_pb_Paint(object sender, PaintEventArgs e)
        {
            Rectangle bounds = new Rectangle(0, 0, Launch_pb.Width - 1, Launch_pb.Height - 1);
            Color top;
            Color bottom;
            Color textColor;

            if (!Launch_pb.Enabled)
            {
                top = Color.FromArgb(34, 43, 50);
                bottom = Color.FromArgb(20, 27, 33);
                textColor = Color.FromArgb(105, 113, 120);
            }
            else if (_launchPressed)
            {
                top = Color.FromArgb(14, 23, 30);
                bottom = Color.FromArgb(34, 47, 57);
                textColor = Color.FromArgb(255, 211, 0);
            }
            else if (_launchHovered)
            {
                top = Color.FromArgb(48, 66, 78);
                bottom = Color.FromArgb(22, 32, 40);
                textColor = Color.FromArgb(255, 211, 0);
            }
            else
            {
                top = Color.FromArgb(45, 60, 71);
                bottom = Color.FromArgb(20, 29, 36);
                textColor = Color.White;
            }

            using LinearGradientBrush background = new(bounds, top, bottom, LinearGradientMode.Vertical);
            using Pen border = new(Color.FromArgb(5, 9, 12));
            using Pen innerBorder = new(Color.FromArgb(55, 73, 85));
            e.Graphics.FillRectangle(background, bounds);
            e.Graphics.DrawRectangle(border, bounds);
            e.Graphics.DrawRectangle(innerBorder, 2, 2, bounds.Width - 4, bounds.Height - 4);
            using Font font = new("Microsoft YaHei UI", 15F, FontStyle.Bold, GraphicsUnit.Point);
            TextRenderer.DrawText(e.Graphics, "开始游戏", font,
                bounds, textColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        public static void SaveError(string ex)
        {
            try
            {
                if (Settings.RemainingErrorLogs-- > 0)
                {
                    File.AppendAllText(@".\Error.txt",
                                       string.Format("[{0}] {1}{2}", DateTime.Now, ex, Environment.NewLine));
                }
            }
            catch
            {
            }
        }

        public void Start()
        {
            try
            {
                GetOldFileList();

                if (OldList.Count == 0)
                {
                    MessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.PatchErr));
                    Completed = true;
                    return;
                }

                _fileCount = OldList.Count;
                for (int i = 0; i < OldList.Count; i++)
                    CheckFile(OldList[i]);

                Checked = true;
                _fileCount = 0;
                _currentCount = 0;

                _fileCount = DownloadList.Count;

                ServicePointManager.DefaultConnectionLimit = Settings.P_Concurrency;

                _stopwatch = Stopwatch.StartNew();
                for (var i = 0; i < Settings.P_Concurrency; i++)
                    BeginDownload();


            }
            catch (EndOfStreamException ex)
            {
                MessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.EndStreamOldVersion));
                Completed = true;
                SaveError(ex.ToString());
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "错误");
                Completed = true;
                SaveError(ex.ToString());
            }

            _stopwatch.Stop();
        }

        private void BeginDownload()
        {
            if (DownloadList.Count == 0)
            {
                Completed = true;

                CleanUp();
                return;
            }

            var download = new Download();
            download.Info = DownloadList.Dequeue();
            DownloadFile(download);

        }

        private void CleanUp()
        {
            if (!CleanFiles) return;

            string[] fileNames = Directory.GetFiles(@".\", "*.*", SearchOption.AllDirectories);
            string fileName;
            for (int i = 0; i < fileNames.Length; i++)
            {
                if (fileNames[i].StartsWith(".\\Screenshots\\")) continue;

                fileName = Path.GetFileName(fileNames[i]);

                if (fileName == "Mir2Config.ini" || fileName == System.AppDomain.CurrentDomain.FriendlyName) continue;

                try
                {
                    if (!NeedFile(fileNames[i]))
                        File.Delete(fileNames[i]);
                }
                catch { }
            }
        }
        public bool NeedFile(string fileName)
        {
            for (int i = 0; i < OldList.Count; i++)
            {
                if (fileName.EndsWith(OldList[i].FileName))
                    return true;
            }

            return false;
        }

        private void GetOldFileList()
        {
            OldList = new List<FileInformation>();

            byte[] data = Download(Settings.P_PatchFileName);
            if (data != null)
            {
                using MemoryStream stream = new MemoryStream(data);
                using BinaryReader reader = new BinaryReader(stream);

                if (reader.ReadByte() == 60)
                {
                    //assume we got a html page back with an error code so it's not a patchlist
                    return;
                }
                reader.BaseStream.Seek(0,SeekOrigin.Begin);
                int count = reader.ReadInt32();

                for (int i = 0; i < count; i++)
                {
                    OldList.Add(new FileInformation(reader));
                }
            }
        }


        public void ParseOld(BinaryReader reader)
        {
            int count = reader.ReadInt32();

            for (int i = 0; i < count; i++)
                OldList.Add(new FileInformation(reader));
        }

        public void CheckFile(FileInformation old)
        {
            FileInformation info = GetFileInformation(Settings.P_Client + old.FileName);
            _currentCount++;

            if (info == null || old.Length != info.Length || old.Creation != info.Creation)
            {
                DownloadList.Enqueue(old);
                _totalBytes += old.Length;
            }
        }

        private int errorcount = 0;

        public void DownloadFile(Download dl)
        {
            var info = dl.Info;
            string fileName = info.FileName.Replace(@"\", "/");

            if (fileName != "PList.gz" && (info.Compressed != info.Length || info.Compressed == 0))
            {
                fileName += ".gz";
            }

            try
            {
                HttpClientHandler httpClientHandler = new() { AllowAutoRedirect = true };
                ProgressMessageHandler progressMessageHandler = new(httpClientHandler);

                progressMessageHandler.HttpReceiveProgress += (_, args) =>
                {

                    dl.CurrentBytes = args.BytesTransferred;

                };

                using (HttpClient client = new(progressMessageHandler))
                {
                    client.DefaultRequestHeaders.Accept.Clear();
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    client.DefaultRequestHeaders.AcceptCharset.Clear();
                    client.DefaultRequestHeaders.AcceptCharset.Add(new StringWithQualityHeaderValue("utf-8"));

                    if (Settings.P_NeedLogin)
                    {
                        string authInfo = Settings.P_Login + ":" + Settings.P_Password;
                        authInfo = Convert.ToBase64String(System.Text.Encoding.Default.GetBytes(authInfo));
                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authInfo);
                    }

                    ActiveDownloads.Add(dl);

                    var task = Task.Run(() => client.GetAsync(new Uri($"{Settings.P_Host}{fileName}"), HttpCompletionOption.ResponseHeadersRead));
                    var response = task.Result;

                    var task2 = Task.Run(() => response.Content.ReadAsByteArrayAsync());
                    byte[] data = task2.Result;

                    _currentCount++;
                    _completedBytes += dl.CurrentBytes;
                    dl.CurrentBytes = 0;
                    dl.Completed = true;

                    if (info.Compressed > 0 && info.Compressed != info.Length)
                    {
                        data = Functions.DecompressBytes(data);
                    }

                    var fileNameOut = Settings.P_Client + info.FileName;
                    var dirName = Path.GetDirectoryName(fileNameOut);
                    if (!Directory.Exists(dirName))
                        Directory.CreateDirectory(dirName);

                    //first remove the original file if needed
                    string[] specialfiles = { ".dll", ".exe", ".pdb" };
                    if (File.Exists(fileNameOut) && ( specialfiles.Contains( Path.GetExtension(fileNameOut).ToLower() )))
                    {
                        string oldFilename = Path.Combine(Path.GetDirectoryName(fileNameOut), ("Old__" + Path.GetFileName(fileNameOut)));

                        try
                        {
                            //if there's another previous backup: delete it first
                            if (File.Exists(oldFilename))
                            {
                                File.Delete(oldFilename);
                            }
                            File.Move(fileNameOut, oldFilename);
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            SaveError(ex.ToString());
                            errorcount++;
                            if (errorcount == 5)
                                MessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.TooManyErrors));
                            if (errorcount < 5)
                                MessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ErrorSavingFile) + fileNameOut);
                        }
                        catch (Exception ex)
                        {
                            SaveError(ex.ToString());
                            errorcount++;
                            if (errorcount == 5)
                                MessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.TooManyErrors));
                            if (errorcount < 5)
                                MessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ErrorSavingFile) + fileNameOut);
                        }
                        finally
                        {
                            //Might cause an infinite loop if it can never gain access
                            Restart = true;
                        }
                    }

                    File.WriteAllBytes(fileNameOut, data);
                    File.SetLastWriteTime(fileNameOut, info.Creation);
                }
            }
            catch (HttpRequestException e)
            {
                File.AppendAllText(@".\Error.txt",
                                       $"[{DateTime.Now}] {info.FileName} could not be downloaded. ({e.Message}) {Environment.NewLine}");
                ErrorFound = true;
            }
            catch (Exception ex)
            {
                SaveError(ex.ToString());
                errorcount++;
                if (errorcount == 5)
                    MessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.TooManyErrors));
                if (errorcount < 5)
                    MessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ErrorSavingFile) + dl.Info.FileName);
            }
            finally
            {
                if (ErrorFound)
                {
                    MessageBox.Show(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.FileDownload_Failure), fileName));
                }
            }

            BeginDownload();
        }

        public byte[] Download(string fileName)
        {
            using (HttpClient client = new())
            {
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.AcceptCharset.Clear();
                client.DefaultRequestHeaders.AcceptCharset.Add(new StringWithQualityHeaderValue("utf-8"));

                if (Settings.P_NeedLogin)
                {
                    string authInfo = Settings.P_Login + ":" + Settings.P_Password;
                    authInfo = Convert.ToBase64String(System.Text.Encoding.Default.GetBytes(authInfo));
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authInfo);
                }

                string uriString = Settings.P_Host + Path.ChangeExtension(fileName, ".gz");

                if (Uri.IsWellFormedUriString(uriString, UriKind.Absolute))
                {
                    var task = Task.Run(() => client.GetAsync(new Uri(uriString), HttpCompletionOption.ResponseHeadersRead));
                    var response = task.Result;
                    using Stream sm = response.Content.ReadAsStream();
                    using MemoryStream ms = new();
                    sm.CopyTo(ms);
                    byte[] data = ms.ToArray();
                    return data;
                }
                else
                {
                    MessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.LauncherHostSettingFormatError), GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.BadHostFormat));
                    return null;
                }
            }
        }

        public FileInformation GetFileInformation(string fileName)
        {
            if (!File.Exists(fileName)) return null;

            FileInfo info = new FileInfo(fileName);
            return new FileInformation
            {
                FileName = fileName.Remove(0, Settings.P_Client.Length),
                Length = (int)info.Length,
                Creation = info.LastWriteTime
            };
        }

        private void AMain_Load(object sender, EventArgs e)
        {
            var envir = CoreWebView2Environment.CreateAsync(null, Settings.ResourcePath).Result;
            Main_browser.EnsureCoreWebView2Async(envir);

            if (Settings.P_BrowserAddress != "")
            {
                if (Uri.IsWellFormedUriString(Settings.P_BrowserAddress, UriKind.Absolute))
                {
                    Main_browser.NavigationCompleted += Main_browser_NavigationCompleted;
                    Main_browser.Source = new Uri(Settings.P_BrowserAddress);
                }
                else
                {
                    MessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.LauncherBrowserFormatError), GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.BadBrowserFormat));
                }
            }

            RepairOldFiles();

            Launch_pb.Enabled = true;
            SetProgress(ProgressCurrent_pb, 0);
            SetProgress(TotalProg_pb, 0);
            CurrentPercent_label.Text = "0%";
            TotalPercent_label.Text = "0%";
            CurrentFile_label.Text = "未检查更新，可直接进入游戏";
            Version_label.Text = string.Format("版本：{0}.{1}.{2}", Globals.ProductCodename, Settings.UseTestConfig ? "调试" : "正式", Application.ProductVersion);

            if (Settings.P_ServerName != String.Empty)
            {
                Name_label.Visible = true;
                Name_label.Text = Settings.P_ServerName;
            }

            InterfaceTimer.Enabled = false;
            if (Settings.P_AutoUpdate)
                BeginUpdate();
        }

        private void Main_browser_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (Main_browser.Source.AbsolutePath != "blank") Main_browser.Visible = true;
        }

        private void Launch_pb_Click(object sender, EventArgs e)
        {
            Launch();
        }

        private void Launch()
        {
            if (ConfigForm.Visible) ConfigForm.Visible = false;

            Program.Launch = true;
            Close();
        }

        private void Close_pb_Click(object sender, EventArgs e)
        {
            if (ConfigForm.Visible) ConfigForm.Visible = false;
            Close();
        }

        private void Movement_panel_MouseClick(object sender, MouseEventArgs e)
        {
            if (ConfigForm.Visible) ConfigForm.Visible = false;
            dragging = true;
            dragCursorPoint = Cursor.Position;
            dragFormPoint = this.Location;
        }

        private void Movement_panel_MouseUp(object sender, MouseEventArgs e)
        {
            dragging = false;
        }

        private void Movement_panel_MouseMove(object sender, MouseEventArgs e)
        {
            if (dragging)
            {
                Point dif = Point.Subtract(Cursor.Position, new Size(dragCursorPoint));
                this.Location = Point.Add(dragFormPoint, new Size(dif));
            }
        }

        private void Launch_pb_MouseEnter(object sender, EventArgs e)
        {
            _launchHovered = true;
            Launch_pb.Invalidate();
        }

        private void Launch_pb_MouseLeave(object sender, EventArgs e)
        {
            _launchHovered = false;
            _launchPressed = false;
            Launch_pb.Invalidate();
        }

        private void Close_pb_MouseEnter(object sender, EventArgs e)
        {
            Close_pb.Image = Client.Resources.Images.Cross_Hover;
        }

        private void Close_pb_MouseLeave(object sender, EventArgs e)
        {
            Close_pb.Image = Client.Resources.Images.Cross_Base;
        }

        private void Launch_pb_MouseDown(object sender, MouseEventArgs e)
        {
            _launchPressed = true;
            Launch_pb.Invalidate();
        }

        private void Launch_pb_MouseUp(object sender, MouseEventArgs e)
        {
            _launchPressed = false;
            Launch_pb.Invalidate();
        }

        private void Close_pb_MouseDown(object sender, MouseEventArgs e)
        {
            Close_pb.Image = Client.Resources.Images.Cross_Pressed;
        }

        private void Close_pb_MouseUp(object sender, MouseEventArgs e)
        {
            Close_pb.Image = Client.Resources.Images.Cross_Base;
        }

        private void ProgressCurrent_pb_SizeChanged(object sender, EventArgs e)
        {
            ProgEnd_pb.Location = new Point((ProgressCurrent_pb.Location.X + ProgressCurrent_pb.Width), ProgressCurrent_pb.Location.Y);
            if (ProgressCurrent_pb.Width == 0) ProgEnd_pb.Visible = false;
            else ProgEnd_pb.Visible = true;
        }

        private void Config_pb_MouseDown(object sender, MouseEventArgs e)
        {
            Config_pb.Image = Client.Resources.Images.Config_Pressed;
        }

        private void Config_pb_MouseEnter(object sender, EventArgs e)
        {
            Config_pb.Image = Client.Resources.Images.Config_Hover;
        }

        private void Config_pb_MouseLeave(object sender, EventArgs e)
        {
            Config_pb.Image = Client.Resources.Images.Config_Base;
        }

        private void Config_pb_MouseUp(object sender, MouseEventArgs e)
        {
            Config_pb.Image = Client.Resources.Images.Config_Base;
        }

        private void Config_pb_Click(object sender, EventArgs e)
        {
            if (ConfigForm.Visible) ConfigForm.Hide();
            else ConfigForm.Show(Program.PForm);
            ConfigForm.Location = new Point(Location.X + Config_pb.Location.X - 183, Location.Y + 36);
        }

        private void TotalProg_pb_SizeChanged(object sender, EventArgs e)
        {
            ProgTotalEnd_pb.Location = new Point((TotalProg_pb.Location.X + TotalProg_pb.Width), TotalProg_pb.Location.Y);
            if (TotalProg_pb.Width == 0) ProgTotalEnd_pb.Visible = false;
            else ProgTotalEnd_pb.Visible = true;
        }

        private void InterfaceTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (Completed && ActiveDownloads.Count == 0)
                {
                    ActionLabel.Text = "";
                    CurrentFile_label.Text = "已是最新版本";
                    SpeedLabel.Text = "";
                    SetProgress(ProgressCurrent_pb, ProgressBarMaximum);
                    SetProgress(TotalProg_pb, ProgressBarMaximum);
                    CurrentFile_label.Visible = true;
                    CurrentPercent_label.Visible = true;
                    TotalPercent_label.Visible = true;
                    CurrentPercent_label.Text = "100%";
                    TotalPercent_label.Text = "100%";
                    InterfaceTimer.Enabled = false;
                    Launch_pb.Enabled = true;
                    _checkUpdateButton.Enabled = true;
                    if (ErrorFound) MessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.FilesDownloadFailed), GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.DownloadFailed));
                    ErrorFound = false;

                    if (CleanFiles)
                    {
                        CleanFiles = false;
                        MessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.YourFilesCleanedUp), GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.CleanFiles));
                    }

                    if (Restart)
                    {
                        Program.Restart = true;

                        Close();
                    }

                    if (Settings.P_AutoStart)
                    {
                        Launch();
                    }
                    return;
                }

                var currentBytes = 0L;
                FileInformation currentFile = null;

                // Remove completed downloads..
                for (var i = ActiveDownloads.Count - 1; i >= 0; i--)
                {
                    var dl = ActiveDownloads[i];

                    if (dl.Completed)
                    {
                        ActiveDownloads.RemoveAt(i);
                        continue;
                    }
                }

                for (var i = ActiveDownloads.Count - 1; i >= 0; i--)
                {
                    var dl = ActiveDownloads[i];
                    if (!dl.Completed)
                        currentBytes += dl.CurrentBytes;
                }

                if (Settings.P_Concurrency == 1)
                {
                    // Note: Just mimic old behaviour for now until a better UI is done.
                    if (ActiveDownloads.Count > 0)
                        currentFile = ActiveDownloads[0].Info;
                }

                ActionLabel.Visible = true;
                SpeedLabel.Visible = true;
                CurrentFile_label.Visible = true;
                CurrentPercent_label.Visible = true;
                TotalPercent_label.Visible = true;

                if (LabelSwitch) ActionLabel.Text = $"剩余 {_fileCount - _currentCount} 个文件";
                else ActionLabel.Text = $"剩余 {((_totalBytes) - (_completedBytes + currentBytes)) / 1024 / 1024:#,##0} MB";

                if (Settings.P_Concurrency > 1)
                {
                    CurrentFile_label.Text = string.Format("并发下载：{0} 个文件", ActiveDownloads.Count);
                    SpeedLabel.Text = ToSize(currentBytes / _stopwatch.Elapsed.TotalSeconds);
                }
                else
                {
                    if (currentFile != null)
                    {
                        CurrentFile_label.Text = string.Format("{0}", currentFile.FileName);
                        SpeedLabel.Text = ToSize(currentBytes / _stopwatch.Elapsed.TotalSeconds);
                        CurrentPercent_label.Text = ((int)(100 * currentBytes / currentFile.Length)).ToString() + "%";
                        SetProgress(ProgressCurrent_pb, (int)(ProgressBarMaximum * currentBytes / currentFile.Length));
                    }
                }

                if (!(_completedBytes is 0 && currentBytes is 0 && _totalBytes is 0))
                {
                    SetProgress(TotalProg_pb, (int)(ProgressBarMaximum * (_completedBytes + currentBytes) / _totalBytes));
                    TotalPercent_label.Text = ((int)(100 * (_completedBytes + currentBytes) / _totalBytes)).ToString() + "%";
                }

            }
            catch
            {
                //to-do 
            }

        }

        private void AMain_Click(object sender, EventArgs e)
        {
            if (ConfigForm.Visible) ConfigForm.Visible = false;
        }

        private void ActionLabel_Click(object sender, EventArgs e)
        {
            LabelSwitch = !LabelSwitch;
        }

        private void Credit_label_Click(object sender, EventArgs e)
        {
            if (Credit_label.Text == "基于 Crystal M2") Credit_label.Text = "界面设计：Breezer";
            else Credit_label.Text = "基于 Crystal M2";
        }

        private void AMain_FormClosed(object sender, FormClosedEventArgs e)
        {
            MoveOldFilesToCurrent();

            Launch_pb?.Dispose();
            Close_pb?.Dispose();
        }

        private static string[] suffixes = new[] { " B", " KB", " MB", " GB", " TB", " PB" };

        private string ToSize(double number, int precision = 2)
        {
            // unit's number of bytes
            const double unit = 1024;
            // suffix counter
            int i = 0;
            // as long as we're bigger than a unit, keep going
            while (number > unit)
            {
                number /= unit;
                i++;
            }
            // apply precision and current suffix
            return Math.Round(number, precision) + suffixes[i];
        }

        private void RepairOldFiles()
        {
            var files = Directory.GetFiles(Settings.P_Client, "*", SearchOption.AllDirectories).Where(x => Path.GetFileName(x).StartsWith("Old__"));

            foreach (var oldFilename in files)
            {
                if (!File.Exists(oldFilename.Replace("Old__", "")))
                {
                    File.Move(oldFilename, oldFilename.Replace("Old__", ""));
                }
                else
                {
                    File.Delete(oldFilename);
                }
            }
        }

        private void MoveOldFilesToCurrent()
        {
            var files = Directory.GetFiles(Settings.P_Client, "*", SearchOption.AllDirectories).Where(x => Path.GetFileName(x).StartsWith("Old__"));

            foreach (var oldFilename in files)
            {
                string originalFilename = Path.Combine(Path.GetDirectoryName(oldFilename), (Path.GetFileName(oldFilename).Replace("Old__", "")));

                if (!File.Exists(originalFilename) && File.Exists(oldFilename))
                    File.Move(oldFilename, originalFilename);
            }
        }
    }
}
