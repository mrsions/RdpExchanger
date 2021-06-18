using log4net;
using log4net.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RdpExchanger
{
    public class ExchangeServer : Common, IExchangeWorker
    {
        static ILog log = LogManager.GetLogger(nameof(ExchangeServer));

        public bool IsRun => HostServer != null && RemoteServer != null && HostServer.IsRun && RemoteServer.IsRun;
        public Dictionary<string, string> MatchSources { get; private set; } = new Dictionary<string, string>();

        public HostServerManager HostServer { get; private set; }
        public RemoteServerManager RemoteServer { get; private set; }

        public void Start()
        {
            log.Info("Request Start");

            if (IsRun) Stop();

            RemoteServer = new RemoteServerManager(this);
            HostServer = new HostServerManager(this);
            RemoteServer.Start();
            HostServer.Start();

            Run();
            log.Info("Request Start Complete");
        }

        public void Stop()
        {
            log.Info("Request Stop");
            HostServer?.Stop();
            RemoteServer?.Stop();
            HostServer = null;
            RemoteServer = null;
            log.Info("Request Stop Complete");
        }

        private async void Run()
        {
            while (IsRun)
            {
                await Task.Delay(1000);
            }
            HostServer?.Stop();
            RemoteServer?.Stop();
            HostServer = null;
            RemoteServer = null;
        }

        private bool TryGetHost(string name, out ExchangeHost server)
        {
            return HostServer.HostList.TryGetValue(name, out server);
        }

        private void RemoveHost(string name)
        {
            HostServer.HostList.Remove(name);
        }

        //--------------------------------------------------------------------------------------
        //
        //     Rdp Host Server
        //
        //--------------------------------------------------------------------------------------
        public class HostServerManager
        {
            static ILog log = LogManager.GetLogger(nameof(HostServerManager));


            public HostServerManager(ExchangeServer master)
            {
                this.master = master;
            }

            public void Start()
            {
                Stop();

                log.Info("Request Start");
                log.Info($"Create Server (port:{HOST_SERVER_PORT})");
                server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                server.Bind(new IPEndPoint(IPAddress.Any, HOST_SERVER_PORT));
                server.Listen(-100);
                canceller = new CancellationTokenSource();

                runningTask = Run();
                log.Info("Request Start Complete");
            }

            public void Stop()
            {
                log.Info("Request Stop");
                canceller?.Cancel();
                try { server?.Shutdown(SocketShutdown.Both); } catch { }
                try { server?.Disconnect(false); } catch { }
                try { server?.Dispose(); } catch { }
                log.Info("Request Stop Complete");
            }

            public async Task Run()
            {
                log.Info("Start");
                while (IsRun)
                {
                    var hostSocket = await server.AcceptAsync(canceller.Token);
                    if (hostSocket == null) continue;

                    var hostLog = LogManager.GetLogger("Host:" + hostSocket.ToV4Ip());
                    try
                    {
                        hostLog.Info("Accept");

                        // 데이터 읽기
                        var result = await ReadHostData(hostSocket);
                        if (result.version != VERSION)
                        {
                            hostLog.Error($"Version missmatch version ({result.version} != {VERSION})");
                        }
                        hostLog.Debug($"Read Complete");


                        // 이전 소켓 제거
                        if (HostList.ContainsKey(result.name))
                        {
                            hostLog.Debug($"Dispose old");
                            HostList[result.name].Dispose();
                        }

                        // 새로운 소켓 삽입
                        HostList[result.name] = new ExchangeHost
                        {
                            name = result.name,
                            socket = hostSocket,
                            log = hostLog
                        };
                        HostList[result.name].StartIdle();
                        hostLog.Info($"Add Host(version:{result.version}, name:{result.name})");
                    }
                    catch (Exception e)
                    {
                        hostLog.Error(e.Message);
                    }
                }
                log.Info("End");
            }

            private async Task<(int version, string name)> ReadHostData(Socket acceptSocket)
            {
                // 길이 읽기
                byte[] dataReader = new byte[4];
                int offset = 0;
                while (offset < dataReader.Length)
                {
                    offset += await acceptSocket.ReceiveAsync(dataReader, offset, dataReader.Length - offset, canceller.Token);
                }
                int packetSize = BitConverter.ToInt32(dataReader, 0);

                // 패킷 읽기
                dataReader = new byte[packetSize - 4];
                offset = 0;
                while (offset < dataReader.Length)
                {
                    offset += await acceptSocket.ReceiveAsync(dataReader, offset, dataReader.Length - offset, canceller.Token);
                }

                // 패킷 파싱
                using (var st = new MemoryStream(dataReader, false))
                using (var reader = new BinaryReader(st, Encoding.UTF8))
                {
                    int version = reader.ReadInt32();
                    string name = reader.ReadString();

                    int connectionSize = reader.ReadInt32();
                    for (int i = 0; i < connectionSize; i++)
                    {
                        string connectIp = reader.ReadString();
                        lock (master.MatchSources)
                        {
                            master.MatchSources[connectIp] = name;
                        }
                    }

                    return (version, name);
                }
            }
        }

        public class ExchangeHost
        {
            public string name;
            public Socket socket;
            internal ILog log;
            private int idleStatus = 0;

            internal void Dispose()
            {
                idleStatus = 1;
                try
                {
                    socket.Shutdown(SocketShutdown.Both); socket.Dispose();
                }
                catch { }
            }

            internal async void StartIdle()
            {
                var idleData = new byte[1];
                while (idleStatus == 0)
                {
                    await socket.WriteAsync(idleData, timeoutMs: 3000);
                }
                idleStatus = 2;
            }

            internal void StopIdle()
            {
                idleStatus = 1;
                while (idleStatus == 1) Thread.Sleep(1);
            }
            internal async Task StopIdleAsync()
            {
                idleStatus = 1;
                while (idleStatus == 1) await Task.Delay(1);
            }
        }

        //--------------------------------------------------------------------------------------
        //
        //     Accept Server
        //
        //--------------------------------------------------------------------------------------
        public class RemoteServerManager
        {
            static ILog log = LogManager.GetLogger(nameof(RemoteServerManager));

            public Socket server;
            public bool IsRun => canceller != null && !canceller.IsCancellationRequested;

            private CancellationTokenSource canceller;
            private Task runningTask;
            private ExchangeServer master;

            public RemoteServerManager(ExchangeServer master)
            {
                this.master = master;
            }

            public void Start()
            {
                Stop();

                log.Info("Request Start");
                log.Info($"Create Server (port:{HOST_REMOTE_PORT})");
                server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                server.Bind(new IPEndPoint(IPAddress.Any, HOST_REMOTE_PORT));
                server.Listen(-100);
                canceller = new CancellationTokenSource();

                runningTask = Run();
                log.Info("Request Start Complete");
            }

            public void Stop()
            {
                log.Info("Request Stop");
                canceller?.Cancel();
                try { server?.Shutdown(SocketShutdown.Both); } catch { }
                try { server?.Disconnect(false); } catch { }
                try { server?.Dispose(); } catch { }
                log.Info("Request Stop Complete");
            }

            private async Task Run()
            {
                log.Info("Start");
                while (IsRun)
                {
                    var client = await server.AcceptAsync(canceller.Token);
                    if (client == null) continue;

                    new ExchangeConnection(master, client).Start();
                }
                log.Info("End");
            }

        }

        public class ExchangeConnection
        {
            public Socket socket;
            public bool IsRun => canceller != null && !canceller.IsCancellationRequested;

            private string ip;
            private ILog log;
            private CancellationTokenSource canceller;
            private Task runningTask;
            private ExchangeServer master;

            public ExchangeConnection(ExchangeServer master, Socket socket)
            {
                this.master = master;
                this.socket = socket;
                ip = socket.ToV4Ip();
                log = LogManager.GetLogger("Remote:" + ip);
                log.Info("Accept");
            }

            public void Start()
            {
                if (IsRun || canceller != null) return;
                log.Info("Request Start");
                canceller = new CancellationTokenSource();
                runningTask = Run();
                log.Info("Request Start Complete");
            }

            public void Stop()
            {
                log.Info("Request Stop");
                canceller?.Cancel();
                try { socket?.Shutdown(SocketShutdown.Both); } catch { }
                try { socket?.Disconnect(false); } catch { }
                try { socket?.Dispose(); } catch { }
                log.Info("Request Stop Complete");
            }

            private async Task Run()
            {
                // Wait or match connection
                ExchangeHost host = await GetHost();
                if (host == null) return;

                log.Debug("Found Host");

                // 데이터 송수신
                Task receive = WorkStream("Recv", socket, host.socket);
                Task send = WorkStream("Send", host.socket, socket);

                while (IsRun && !receive.IsCompleted && !receive.IsFaulted && !send.IsCompleted && !send.IsFaulted)
                {
                    await Task.Delay(1);
                }
            }

            private async Task<ExchangeHost> GetHost()
            {
                string name;
                while (true)
                {
                    lock (master.MatchSources)
                    {
                        if (!master.MatchSources.TryGetValue(ip, out name))
                        {
                            log.Debug($"Not available ip address. {ip}");
                            socket.Shutdown(SocketShutdown.Both);
                            return null;
                        }
                    }

                    if (master.TryGetHost(name, out var server))
                    {
                        await server.StopIdleAsync();
                        await server.socket.WriteAsync(new byte[] { 1 }, token: canceller.Token); // 데이터 전송용 데이터
                        master.RemoveHost(name);
                        return server;
                    }
                    else
                    {
                        log.Debug($"Wait for host. (name={name})");
                        await Task.Delay(1000);
                    }
                }
            }

            private async Task WorkStream(string tag, Socket read, Socket write)
            {
                byte[] buffer = new byte[BUFFER_SIZE];
                try
                {
                    while (IsRun && read.Connected && write.Connected && !canceller.IsCancellationRequested)
                    {
                        int size = await read.ReceiveAsync(buffer, token: canceller.Token);
                        if (size > 0)
                        {
                            //log.Debug($"{tag} => {size}");
                            await write.WriteAsync(buffer, 0, size);
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(tag + " / " + e);
                }
            }
        }
    }
}
