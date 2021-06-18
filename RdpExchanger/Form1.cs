using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RdpExchanger
{
    public partial class Form1 : Form
    {
        public IExchangeWorker worker;
        private bool first = true;

        public Form1()
        {
            InitializeComponent();
        }

        private void Form1_Shown(object sender, EventArgs e)
        {
            if (first)
            {
                if (Common.options.autoStart)
                {
                    StartRun();
                }
                if (Common.options.autoTray)
                        {
                    Tray();
                }
                first = false;
            }
        }

        private void StartRun()
        {
            runningButton.Text = "Stop";

            worker?.Stop();
            worker = (Common.options.server ? (IExchangeWorker)new ExchangeServer() : (IExchangeWorker)new ExchangeHostClient());
            worker.Start();

            //Common.options.client.domain = "127.0.0.1";
            //new ExchangeServer().Start();
            //new ExchangeHostClient().Start();
        }

        private void StopRun()
        {
            runningButton.Text = "Start";
            worker?.Stop();
            worker = null;
        }

        private void Tray()
        {
            Hide();
        }

        private void openToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Show();
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            notifyIcon1.Visible = false;
            Dispose();
        }

        private void runningButton_Click(object sender, EventArgs e)
        {
            if (worker != null && worker.IsRun)
            {
                StopRun();
            }
            else
            {
                StartRun();
            }
        }

        private void settingButton_Click(object sender, EventArgs e)
        {

            var proc = Process.Start("notepad.exe", Path.GetFullPath("config.json"));
            proc.WaitForExit();

            bool isRun = worker != null && worker.IsRun;
            if (isRun) StopRun();
            Program.LoadConfigs();
            if (isRun) StartRun();
            //Process.Start(new ProcessStartInfo
            //{
            //    UseShellExecute = true,
            //    FileName = Path.GetFullPath("config.json")
            //});
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            e.Cancel = true;
            Tray();
        }

        private void notifyIcon1_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            Show();
        }
    }
}
