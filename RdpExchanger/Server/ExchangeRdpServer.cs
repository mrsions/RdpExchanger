using log4net;
using log4net.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RdpExchanger
{
    public class ExchangeRdpServer : Common, IExchangeWorker
    {
        static ILog log = LogManager.GetLogger("RdpServer");

        ///////////////////////////////////////////////////////////////////////////////////////
        //
        //                    MEMBERS
        //
        ///////////////////////////////////////////////////////////////////////////////////////

        //-- Public

        //-- Private
        private Socket server;
        private Task task;
        private CancellationTokenSource canceller;
        private int port;

        //-- Properties
        public bool IsRun => task != null && canceller != null && !task.IsCompleted && !task.IsFaulted && !task.IsCanceled && !canceller.IsCancellationRequested;
        public List<ExchangeHostContainerServer.Connection> Connections { get; } = new List<ExchangeHostContainerServer.Connection>();

        ///////////////////////////////////////////////////////////////////////////////////////
        //
        //                    CONTROL
        //
        ///////////////////////////////////////////////////////////////////////////////////////

        public ExchangeRdpServer(int port)
        {
            this.port = port;
        }

        public async Task Start()
        {
            if (IsRun)
            {
                log.Error("Already Started. Please call after stop.");
                return;
            }

            log.Info("Request Start");
            log.Info($"Create Server (port:{port})");
            server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            server.Bind(new IPEndPoint(IPAddress.Any, port));
            server.Listen(-100);
            canceller = new CancellationTokenSource();

            task = Task.Run(Run, canceller.Token);
            log.Info("Request Start Complete");
            await Task.CompletedTask;
        }

        public async Task Stop()
        {
            log.Info("Request Stop");

            // 활동종료
            canceller.Cancel();
            // 서버종료
            try { server?.Shutdown(SocketShutdown.Both); } catch { }
            try { server?.Disconnect(false); } catch { }
            try { server?.Dispose(); } catch { }

            foreach (var c in Connections.ToArray())
            {
                await c.Stop();
            }
            // 대기
            while (IsRun) await Task.Delay(10);

            log.Info("Request Stop Complete");
        }

        private async Task Run()
        {
            while (IsRun)
            {
                try
                {
                    var acceptSocket = await server.AcceptAsync(canceller.Token);
                    if (acceptSocket == null) continue;

                    var st = Stopwatch.StartNew();

                    // 15초 이내에 대기중인 대상 할당
                    while (st.ElapsedMilliseconds < 15000)
                    {
                        if (Connections.Count == 0)
                        {
                            await Task.Delay(100);
                            continue;
                        }

                        lock (this)
                        {
                            if (Connections[0].IsRun)
                            {
                                Connections[0].TargetSocket = acceptSocket;
                                Connections.RemoveAt(0);
                                acceptSocket = null;
                                break;
                            }
                            else
                            {
                                Connections.RemoveAt(0);
                            }
                        }
                    }

                    // 대기시간동안 할당되지 않았다면 해제함
                    if (acceptSocket != null)
                    {
                        acceptSocket.Disconnect(false);
                    }
                }
                catch (Exception e)
                {
                    if (IsRun)
                    {
                        log.Error(e.Message, e);
                    }
                }
            }
        }

        ///////////////////////////////////////////////////////////////////////////////////////
        //
        //                    CONNECTION
        //
        ///////////////////////////////////////////////////////////////////////////////////////

        public void AddConnection(ExchangeHostContainerServer.Connection connection)
        {
            lock (this)
            {
                Connections.Add(connection);
            }
        }

        public void RemoveConnection(ExchangeHostContainerServer.Connection connection)
        {
            lock (this)
            {
                Connections.Remove(connection);
            }
        }
    }
}
