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

        private Socket remoteSck;
        private Socket hostSck;
        private CancellationTokenSource canceller;
        private DateTime lastNetworkTime;

        public bool IsRun { get; private set; }

        public void Start()
        {
            if (IsRun) return;
            IsRun = true;
            canceller = new CancellationTokenSource();
            Run();
        }

        public void Stop()
        {
            IsRun = false;
            canceller?.Cancel();
            try { remoteSck?.Shutdown(SocketShutdown.Both); remoteSck?.Disconnect(false); } catch { } finally { remoteSck = null; }
            try { hostSck?.Shutdown(SocketShutdown.Both); remoteSck?.Disconnect(false); } catch { } finally { hostSck = null; }
        }

        private async void Run()
        {
            while (IsRun)
            {
                try { remoteSck?.Shutdown(SocketShutdown.Both); remoteSck?.Disconnect(false); } catch { } finally { remoteSck = null; }
                try { hostSck?.Shutdown(SocketShutdown.Both); remoteSck?.Disconnect(false); } catch { } finally { hostSck = null; }
                try
                {
                    using (remoteSck = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                    {
                        log.Info($"Connecting to {options.client.domain}:{HOST_SERVER_PORT}");
                        await remoteSck.ConnectAsync(options.client.domain, HOST_SERVER_PORT, canceller.Token);
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
                            // 원격용 소켓 접속
                            await ConnectLocalRdp();
                            log.Info(hostSck, "connecting");

                            // 데이터 송수신
                            Task receive = WorkStream("Receive", remoteSck, hostSck);
                            Task send = WorkStream("Send", hostSck, remoteSck);
                            await CancelStream(receive, send);
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
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
                    if (data[0] == 1) // 데이터가 1이 넘어오면
                    {
                        return true;
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

            if (options.client.connections == null || options.client.connections.Length == 0 || string.IsNullOrWhiteSpace(options.client.connections[0]))
            {
                log.Error("Client connection can NOT empty.");
                throw new ArgumentException("Client connection can NOT empty.");
            }

            using (var m = new MemoryStream())
            using (var w = new BinaryWriter(m, Encoding.UTF8))
            {
                w.Write((int)0);
                w.Write((int)VERSION);
                w.Write(options.client.name);
                w.Write((int)options.client.connections.Length);
                foreach (var address in options.client.connections)
                {
                    w.Write(address);
                }
                w.Flush();
                w.Seek(0, SeekOrigin.Begin);
                w.Write(m.Length);

                return m.ToArray();
            }
        }

        private async Task ConnectLocalRdp()
        {
            hostSck = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await hostSck.ConnectAsync(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 3389));
        }

        private async Task CancelStream(Task a, Task b)
        {
            lastNetworkTime = DateTime.Now;
            while (IsRun)
            {
                while (a.IsCompleted || a.IsFaulted || b.IsCompleted || b.IsFaulted)
                {
                    break;
                }
                if ((DateTime.Now - lastNetworkTime).TotalSeconds > 2)
                {
                    break;
                }
                await Task.Delay(100);
            }
        }

        private async Task WorkStream(string tag, Socket read, Socket write)
        {
            byte[] buffer = new byte[BUFFER_SIZE];
            try
            {
                while (IsRun || read.Connected || write.Connected || !canceller.IsCancellationRequested)
                {
                    int size = await read.ReceiveAsync(buffer, token: canceller.Token);
                    if (size > 0)
                    {
                        //log.Debug($"{tag} => {size}");
                        await write.WriteAsync(buffer, 0, size);
                        lastNetworkTime = DateTime.Now;
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
