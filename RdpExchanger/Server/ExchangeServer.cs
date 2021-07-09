using log4net;
using log4net.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RdpExchanger
{
    public class ExchangeServer : Common, IExchangeWorker
    {
        private static ILog log = LogManager.GetLogger(nameof(ExchangeServer));

        private static ExchangeServer instance;
        public static ExchangeServer Instance => instance != null ? instance : (instance = new ExchangeServer());

        public bool IsRun => HostServer != null && HostServer.IsRun;
        public Dictionary<int, ExchangeRdpServer> RdpServers { get; } = new Dictionary<int, ExchangeRdpServer>();

        public ExchangeHostContainerServer HostServer { get; private set; }

        public async Task Start()
        {
            if (IsRun)
            {
                log.Error("Already Started. Please call after stop.");
                return;
            }

            log.Info("Request Start");
            HostServer = new ExchangeHostContainerServer();
            await HostServer.Start();

            Task.Run(Run);
            log.Info("Request Start Complete");
        }

        public async Task Stop()
        {
            log.Info("Request Stop");
            await HostServer?.Stop();
            HostServer = null;
            foreach (var rdp in RdpServers.Values.ToArray())
            {
                await rdp.Stop();
            }
            log.Info("Request Stop Complete");
        }

        private async Task Run()
        {
            while (IsRun)
            {
                await Task.Delay(1000);
            }
            await Stop();
        }

        public async Task<ExchangeRdpServer> OpenRdpServer(int port, ExchangeHostContainerServer.Connection connection)
        {
            if (!RdpServers.TryGetValue(port, out var server))
            {
                server = new ExchangeRdpServer(port);
                await server.Start();
                RdpServers[port] = server;
            }
            server.AddConnection(connection);
            return server;
        }
    }
}
