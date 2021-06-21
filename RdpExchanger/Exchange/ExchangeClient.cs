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
    /// <summary>
    /// RDP를 전달받을 원격지 컴퓨터에서 실행되며,
    /// 언제나 서버에 접속하려고한다.
    /// 서버에서 신호가 전달되면 곧바로 내부 RDP서비스에 접속하여 중계한다.
    /// </summary>
    public class ExchangeHostClient : Common, IExchangeWorker
    {
        static ILog log = LogManager.GetLogger("HostClient");

        private CancellationTokenSource canceller;
        private List<Connection> connections = new List<Connection>();

        public bool IsRun { get; private set; }

        public async Task Start()
        {
            if (IsRun) return;
            IsRun = true;
            canceller = new CancellationTokenSource();
            Task.Run(Run, canceller.Token);
        }

        public async Task Stop()
        {
            IsRun = false;
            canceller?.Cancel();
        }

        private async Task Run()
        {
            while (IsRun)
            {
                Socket remoteSck = null;
                try
                {
                    remoteSck = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

                    log.Info($"Connecting to {options.client.domain}:{CONTAINER_SERVER_PORT}");
                    await remoteSck.ConnectAsync(options.client.domain, CONTAINER_SERVER_PORT, canceller.Token);
                    if (!remoteSck.Connected)
                    {
                        await Task.Delay(1000);
                        continue;
                    }
                    remoteSck.ReceiveTimeout = 3000;
                    remoteSck.SendTimeout = 3000;

                    // 헤더 데이터 전송
                    log.Info("Send RdpServerHeader");
                    byte[] data = MakeHeader();
                    await remoteSck.WriteAsync(data, token: canceller.Token);

                    // 접속까지 대기
                    log.Info(remoteSck, "Wait for connection");
                    if (await WaitForConnection(remoteSck))
                    {
                        var conn = new Connection(remoteSck);
                        await conn.Start();
                        connections.Add(conn);
                        remoteSck = null;
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
                finally
                {
                    if (remoteSck != null)
                    {
                        try { remoteSck.Dispose(); } catch { }
                    }
                }
            }
        }

        private async Task<bool> WaitForConnection(Socket socket)
        {
            byte[] data = new byte[1];
            while (IsRun)
            {
                int readBytes = await socket.ReceiveAsync(data, 0, 1, canceller.Token, 3000);
                if (readBytes > 0)
                {
                    if (data[0] == OPCODE_CONNECT) // 데이터가 1이 넘어오면
                    {
                        return true;
                    }
                    else if (data[0] == OPCODE_ERROR)
                    {
                        try
                        {
                            int size = BitConverter.ToInt32(await socket.ReceiveBytesAsync(4, canceller.Token), 0);
                            string msg = Encoding.UTF8.GetString(await socket.ReceiveBytesAsync(size, canceller.Token));
                            log.Error(msg);
                            return false;
                        }
                        catch (Exception e)
                        {
                            throw e;
                        }
                    }
                }
                else
                {
                    return false;
                }
            }
            return false;
        }

        private byte[] MakeHeader()
        {
            if (string.IsNullOrWhiteSpace(options.client.name) || options.client.name.Length > 32)
            {
                log.Error("Client name can NOT whitespace or more than 32.");
                throw new ArgumentException("Client name is NOT whitespace or more than 32.");
            }

            using (var m = new MemoryStream())
            using (var w = new BinaryWriter(m, Encoding.UTF8))
            {
                w.Write((int)0);
                w.Write((int)VERSION);
                w.Write(options.client.name);
                w.Write(options.client.port);
                w.Seek(0, SeekOrigin.Begin);
                w.Write((int)m.Length);
                w.Flush();

                return m.ToArray();
            }
        }
        public class Connection : IExchangeWorker
        {
            private Socket remoteSck;
            private Socket hostSck;
            private ILog log;
            private CancellationTokenSource canceller;
            private Task task;

            public bool IsRun => task != null && !task.IsCompleted && !task.IsFaulted && !task.IsCanceled && !canceller.IsCancellationRequested;

            public Connection(Socket remoteSck)
            {
                this.remoteSck = remoteSck;
                this.canceller = new CancellationTokenSource();
                this.log = LogManager.GetLogger($"Connection");
                this.log.Info("Connected");
            }

            public async Task Start()
            {
                task = Task.Run(Run);
                await Task.CompletedTask;
            }

            public async Task Stop()
            {
                canceller.Cancel();
                while (remoteSck != null) await Task.Delay(10);
                task = null;
            }

            private async Task Run()
            {
                try
                {
                    // 원격용 소켓 접속
                    await ConnectLocalRdp();
                    log.Info("connecting");

                    // 데이터 송수신
                    Task receive = Task.Factory.StartNew(RemoteToHost);
                    Task send = Task.Factory.StartNew(HostToRemote);
                    await CancelStream(receive, send);
                }
                finally
                {
                    try { hostSck?.Disconnect(false); } catch { }
                    try { hostSck?.Close(); } catch { }
                    try { hostSck?.Dispose(); } catch { }
                    hostSck = null;
                    try { remoteSck?.Disconnect(false); } catch { }
                    try { remoteSck?.Close(); } catch { }
                    try { remoteSck?.Dispose(); } catch { }
                    remoteSck = null;
                }
            }

            private async Task ConnectLocalRdp()
            {
                hostSck = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await hostSck.ConnectAsync(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 3389));
            }

            private async Task CancelStream(Task a, Task b)
            {
                while (IsRun)
                {
                    while (a.IsCompleted || a.IsFaulted || b.IsCompleted || b.IsFaulted)
                    {
                        break;
                    }
                    await Task.Delay(100);
                }
            }

            private void RemoteToHost() => WorkStream("Receive", remoteSck, hostSck);
            private void HostToRemote() => WorkStream("Send", hostSck, remoteSck);
            private void WorkStream(string tag, Socket read, Socket write)
            {
                byte[] buffer = new byte[BUFFER_SIZE];
                try
                {
                    while (IsRun && read.Connected && write.Connected && !canceller.IsCancellationRequested)
                    {
                        int size = read.Receive(buffer, 0, buffer.Length, SocketFlags.None);
                        if (size > 0)
                        {
                            write.Send(buffer, 0, size, SocketFlags.None);
                            //log.Debug($"{tag} => {size}");
                            //byte[] data = new byte[size];
                            //Buffer.BlockCopy(buffer, 0, data, 0, size);
                            //write.WriteAsync(data, 0, data.Length);
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
