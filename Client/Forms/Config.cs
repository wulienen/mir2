using Client;
using System.Resources;
using System.Reflection;
using Client.Resolution;

namespace Launcher
{

    public partial class Config : Form
    {
        private readonly PictureBox AutoUpdate_pb;
        private readonly Label AutoUpdate_label;

        public Config()
        {
            InitializeComponent();

            AutoScaleMode = AutoScaleMode.None;
            Text = "启动器设置";

            AutoUpdate_pb = new PictureBox
            {
                BackgroundImageLayout = ImageLayout.Center,
                Cursor = Cursors.Hand,
                Location = new Point(23, 160),
                Size = new Size(12, 12),
                TabStop = false
            };
            AutoUpdate_pb.Click += AutoUpdate_pb_Click;

            AutoUpdate_label = new Label
            {
                AutoSize = true,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                Font = new Font("Microsoft YaHei UI", 8.25F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.Gray,
                Location = new Point(40, 159),
                Text = "启动时自动检查更新"
            };
            AutoUpdate_label.Click += AutoUpdate_pb_Click;

            Controls.Add(AutoUpdate_pb);
            Controls.Add(AutoUpdate_label);
            ConfigureChineseLayout();
        }

        private void ConfigureChineseLayout()
        {
            Font optionFont = new Font("Microsoft YaHei UI", 8.25F, FontStyle.Regular, GraphicsUnit.Point);
            Font sectionFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            label9.Text = "画面设置";
            label9.Font = sectionFont;
            label10.Text = "分辨率";
            label10.Font = sectionFont;
            label11.Text = "启动设置";
            label11.Font = sectionFont;
            label12.Text = "账号信息";
            label12.Font = optionFont;
            Fullscreen_label.Text = "全屏模式";
            Fullscreen_label.Font = optionFont;
            FPScap_label.Text = "限制帧率";
            FPScap_label.Font = optionFont;
            OnTop_label.Text = "窗口置顶";
            OnTop_label.Font = optionFont;
            AutoStart_label.Text = "更新后自动进入游戏";
            AutoStart_label.Font = optionFont;

            AutoStart_pb.Location = new Point(23, 180);
            AutoStart_label.Location = new Point(40, 179);
            label12.Location = new Point(20, 204);
            pictureBox6.Location = new Point(23, 216);
            AccountLogin_txt.Location = new Point(29, 223);
            ID_l.Location = new Point(29, 222);
            AccountPass_txt.Location = new Point(29, 250);
            Password_l.Location = new Point(29, 250);
            CleanFiles_pb.Location = new Point(23, 280);
            CleanFiles_pb.Image = null;
            CleanFiles_pb.BackColor = Color.FromArgb(16, 20, 24);
            CleanFiles_pb.Cursor = Cursors.Hand;
            CleanFiles_pb.Paint += CleanFiles_pb_Paint;
        }

        private void Config_Load(object sender, EventArgs e)
        {
            label10.Text = "分辨率";
            AutoStart_label.Text = "更新后自动进入游戏";
            ID_l.Text = "用户名";
            Password_l.Text = "密码";

            DrawSupportedResolutions();
        }
                                   
        private void Res1_pb_Click(object sender, EventArgs e)
        {
            resolutionChoice(eSupportedResolution.w1024h768);

        }

        public void resolutionChoice(eSupportedResolution res)
        {
            Res2_pb.Image = Client.Resources.Images.Radio_Unactive;
            Res3_pb.Image = Client.Resources.Images.Radio_Unactive;
            Res4_pb.Image = Client.Resources.Images.Radio_Unactive;
            Res5_pb.Image = Client.Resources.Images.Radio_Unactive;

            switch (res)
            {
                case eSupportedResolution.w1024h768:
                    Res2_pb.Image = Client.Resources.Images.Config_Radio_On;
                    break;
                case eSupportedResolution.w1366h768:
                    Res3_pb.Image = Client.Resources.Images.Config_Radio_On;
                    break;
                case eSupportedResolution.w1280h720:
                    Res4_pb.Image = Client.Resources.Images.Config_Radio_On;
                    break;
                case eSupportedResolution.w1920h1080:
                    Res5_pb.Image = Client.Resources.Images.Config_Radio_On;
                    break;

            }

            Settings.Resolution = (int)res;
        }

        private void Res2_pb_Click(object sender, EventArgs e)
        {
            resolutionChoice(eSupportedResolution.w1024h768);
        }

        private void Res3_pb_Click(object sender, EventArgs e)
        {
            resolutionChoice(eSupportedResolution.w1366h768);
        }

        private void Config_VisibleChanged(object sender, EventArgs e)
        {
            if (Visible)
            {
                AccountLogin_txt.Text = Settings.AccountID;
                AccountPass_txt.Text = Settings.Password;
                resolutionChoice((eSupportedResolution)Settings.Resolution);

                Fullscreen_pb.Image = Settings.FullScreen
                    ? Client.Resources.Images.Config_Check_On
                    : Client.Resources.Images.Config_Check_Off1;

                FPScap_pb.Image = Settings.FPSCap
                    ? Client.Resources.Images.Config_Check_On
                    : Client.Resources.Images.Config_Check_Off1;

                OnTop_pb.Image = Settings.TopMost
                    ? Client.Resources.Images.Config_Check_On
                    : Client.Resources.Images.Config_Check_Off1;

                AutoStart_pb.Image = Settings.P_AutoStart
                    ? Client.Resources.Images.Config_Check_On
                    : Client.Resources.Images.Config_Check_Off1;

                AutoUpdate_pb.Image = Settings.P_AutoUpdate
                    ? Client.Resources.Images.Config_Check_On
                    : Client.Resources.Images.Config_Check_Off1;

                this.ActiveControl = label4;
            }
            else
            {             
                Settings.AccountID = AccountLogin_txt.Text;
                Settings.Password = AccountPass_txt.Text;
                Settings.Save();
            }
        }

        private void AccountLogin_txt_TextChanged(object sender, EventArgs e)
        {
            if (AccountLogin_txt.Text == string.Empty) ID_l.Visible = true;
            else ID_l.Visible = false;
        }

        private void AccountPass_txt_TextChanged(object sender, EventArgs e)
        {
            if (AccountPass_txt.Text == string.Empty) Password_l.Visible = true;
            else Password_l.Visible = false;
        }

        private void AccountLogin_txt_Click(object sender, EventArgs e)
        {
            ID_l.Visible = false;
            AccountLogin_txt.Focus();
        }

        private void AccountPass_txt_Click(object sender, EventArgs e)
        {
            Password_l.Visible = false;
            AccountPass_txt.Focus();
        }

        private void Config_Click(object sender, EventArgs e)
        {
            this.ActiveControl = label4;
        }

        private void Fullscreen_pb_Click(object sender, EventArgs e)
        {
            Settings.FullScreen = !Settings.FullScreen;

            Fullscreen_pb.Image = Settings.FullScreen
                    ? Client.Resources.Images.Config_Check_On
                    : Client.Resources.Images.Config_Check_Off1;
        }

        private void FPScap_pb_Click(object sender, EventArgs e)
        {
            Settings.FPSCap = !Settings.FPSCap;

            FPScap_pb.Image = Settings.FPSCap
                    ? Client.Resources.Images.Config_Check_On
                    : Client.Resources.Images.Config_Check_Off1;
        }

        private void OnTop_pb_Click(object sender, EventArgs e)
        {
            Settings.TopMost = !Settings.TopMost;

            OnTop_pb.Image = Settings.TopMost
                    ? Client.Resources.Images.Config_Check_On
                    : Client.Resources.Images.Config_Check_Off1;
        }

        private void AutoStart_pb_Click(object sender, EventArgs e)
        {
            Settings.P_AutoStart = !Settings.P_AutoStart;

            AutoStart_pb.Image = Settings.P_AutoStart
                    ? Client.Resources.Images.Config_Check_On
                    : Client.Resources.Images.Config_Check_Off1;
        }

        private void AutoUpdate_pb_Click(object sender, EventArgs e)
        {
            Settings.P_AutoUpdate = !Settings.P_AutoUpdate;

            AutoUpdate_pb.Image = Settings.P_AutoUpdate
                ? Client.Resources.Images.Config_Check_On
                : Client.Resources.Images.Config_Check_Off1;
        }

        private void CleanFiles_pb_MouseDown(object sender, MouseEventArgs e)
        {
            CleanFiles_pb.BackColor = Color.FromArgb(8, 12, 15);
            CleanFiles_pb.Invalidate();
        }

        private void CleanFiles_pb_MouseUp(object sender, MouseEventArgs e)
        {
            CleanFiles_pb.BackColor = Color.FromArgb(16, 20, 24);
            CleanFiles_pb.Invalidate();
        }

        private void CleanFiles_pb_MouseEnter(object sender, EventArgs e)
        {
            CleanFiles_pb.BackColor = Color.FromArgb(31, 42, 50);
            CleanFiles_pb.Invalidate();
        }

        private void CleanFiles_pb_MouseLeave(object sender, EventArgs e)
        {
            CleanFiles_pb.BackColor = Color.FromArgb(16, 20, 24);
            CleanFiles_pb.Invalidate();
        }

        private void CleanFiles_pb_Click(object sender, EventArgs e)
        {
            if (!Program.PForm.Launch_pb.Enabled) return;

            Program.PForm.BeginUpdate(true);
        }

        private void CleanFiles_pb_Paint(object sender, PaintEventArgs e)
        {
            Rectangle bounds = new Rectangle(0, 0, CleanFiles_pb.Width - 1, CleanFiles_pb.Height - 1);
            using Pen border = new(Color.FromArgb(54, 63, 70));
            using Font font = new("Microsoft YaHei UI", 8F, FontStyle.Regular, GraphicsUnit.Point);
            e.Graphics.DrawRectangle(border, bounds);
            TextRenderer.DrawText(e.Graphics, "清理文件", font, bounds, Color.LightGray,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private void Res4_pb_Click(object sender, EventArgs e)
        {
            resolutionChoice(eSupportedResolution.w1280h720);
        }

        private void Res5_pb_Click(object sender, EventArgs e)
        {
            resolutionChoice(eSupportedResolution.w1920h1080);
        }

        private void DrawSupportedResolutions()
        {
            Res2_pb.Enabled = false;
            label2.ForeColor = Color.Red;
            Res4_pb.Enabled = false;
            label5.ForeColor = Color.Red;
            Res3_pb.Enabled = false;
            label3.ForeColor = Color.Red;
            Res5_pb.Enabled = false;
            label1.ForeColor = Color.Red;

            foreach (eSupportedResolution supportedResolution in DisplayResolutions.DisplaySupportedResolutions)
            {
                switch (supportedResolution)
                {
                    case (eSupportedResolution.w1024h768):
                        Res2_pb.Enabled = true;
                        label2.ForeColor = Color.Gray;
                        break;
                    case (eSupportedResolution.w1280h720):
                        Res4_pb.Enabled = true;
                        label5.ForeColor = Color.Gray;
                        break;
                    case (eSupportedResolution.w1366h768):
                        Res3_pb.Enabled = true;
                        label3.ForeColor = Color.Gray;
                        break;
                    case (eSupportedResolution.w1920h1080):
                        Res5_pb.Enabled = true;
                        label1.ForeColor = Color.Gray;
                        break;
                }
            }
        }
    }
}
