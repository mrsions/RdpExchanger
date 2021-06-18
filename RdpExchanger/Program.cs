using LitJson;
using log4net;
using log4net.Config;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RdpExchanger
{
    public class ProgramOptions
    {
        public bool server = true;
        public bool autoStart = true;
        public bool autoTray = true;

        public int bufferSize = 1 * 1024 * 1024; // 1MB
        public int remotePort = 21001;
        public int hostPort = 21000;

        public int remotePortStart = 21001;
        public int remotePortEnd = 21999;


        public ProgramOptions_Client client = new ProgramOptions_Client();

        public class ProgramOptions_Client
        {
            public string domain = "api.realwith.com";
            public string name = "Unkown-" + Guid.NewGuid().ToString("N").Substring(0, 6); // 1MB
            public string[] connections; // ip address
        }

    }

    static class Program
    {
        static ILog log ;

        /// <summary>
        /// 해당 애플리케이션의 주 진입점입니다.
        /// </summary>
        [STAThread]
        static void Main()
        {
            XmlConfigurator.Configure(new FileInfo("log4net.xml"));
            log = LogManager.GetLogger(typeof(Program).Name);

            if (!File.Exists("config.json"))
            {
                log.Info("Create config.json.");
                JsonWriter writer = new JsonWriter();
                writer.PrettyPrint = true;
                JsonMapper.ToJson(Common.options, writer);
                File.WriteAllText("config.json", writer.ToString());
            }
            LoadConfigs();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Form1());
        }

        public static void LoadConfigs()
        {
            log.Info("Load config.json.");
            Common.options = JsonMapper.ToObject<ProgramOptions>(File.ReadAllText("config.json", Encoding.UTF8));

        }
    }
}
