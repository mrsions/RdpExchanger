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
    public class ExchangeHostContainerServer : Common, IExchangeWorker
    {
        static ILog log = LogManager.GetLogger("HostContainer");

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

        //-- Properties
        public bool IsRun => task != null && canceller != null && !task.IsCompleted && !task.IsFaulted && !task.IsCanceled && !canceller.IsCancellationRequested;
        public readonly List<Connection> Connections = new List<Connection>();

        ///////////////////////////////////////////////////////////////////////////////////////
        //
        //                    CONTROL
        //
        ///////////////////////////////////////////////////////////////////////////////////////

        public async Task Start()
        {
            if (IsRun)
            {
                log.Error("Already Started. Please call after stop.");
                return;
            }

            log.Info("Request Start");
            log.Info($"Create Server (port:{CONTAINER_SERVER_PORT})");
            server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            server.Bind(new IPEndPoint(IPAddress.Any, CONTAINER_SERVER_PORT));
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

                    await new Connection(this, acceptSocket).Start();
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

        private void AddConnection(Connection connection)
        {
            Connections.Add(connection);
        }

        private void RemoveConnection(Connection connection)
        {
            Connections.Remove(connection);
        }

        private async Task<ExchangeRdpServer> OpenRdpServer(int receivePort, Connection connection)
        {
            return await ExchangeServer.Instance.OpenRdpServer(receivePort, connection);
        }

        public class Connection : Common, IExchangeWorker
        {
            ///////////////////////////////////////////////////////////////////////////////////////
            //
            //                    MEMBERS
            //
            ///////////////////////////////////////////////////////////////////////////////////////

            public Socket TargetSocket { get; set; }
            public string Name { get; private set; }
            public int ReceivePort { get; private set; }
            public int ClientVersion { get; private set; }
            public bool IsRun { get; private set; }//=> task != null && canceller != null && !task.IsCompleted && !task.IsFaulted && !task.IsCanceled && !canceller.IsCancellationRequested;

            private readonly ExchangeHostContainerServer server;
            private readonly Socket socket;
            private readonly ILog log;
            private readonly string ipAddress;

            private ExchangeRdpServer rdpServer;
            private CancellationTokenSource canceller;
            private Task task;

            ///////////////////////////////////////////////////////////////////////////////////////
            //
            //                    LIFECYCLE
            //
            ///////////////////////////////////////////////////////////////////////////////////////

            public Connection(ExchangeHostContainerServer server, Socket socket)
            {
                this.server = server;
                this.socket = socket;
                ipAddress = socket.RemoteEndPoint.ToString();
                log = LogManager.GetLogger($"Container({ipAddress})");
                log.Info("Connected");
            }

            public async Task Start()
            {
                if (IsRun)
                {
                    log.Error("Already Started. Please call after stop.");
                    return;
                }

                IsRun = true;
                canceller = new CancellationTokenSource();
                task = Task.Run(Run, canceller.Token);
                log.Info("Request Start Complete");
                await Task.CompletedTask;
            }

            public async Task Stop()
            {
                log.Info("Request Stop");

                // 활동종료
                IsRun = false;
                canceller.Cancel();
                // 서버종료
                try { socket?.Shutdown(SocketShutdown.Both); } catch { }
                try { socket?.Disconnect(false); } catch { }
                try { socket?.Dispose(); } catch { }
                // 대기
                while (IsRun) await Task.Delay(10);

                log.Info("Request Stop Complete");
            }

            ///////////////////////////////////////////////////////////////////////////////////////
            //
            //                    RUNNER
            //
            ///////////////////////////////////////////////////////////////////////////////////////

            private async Task Run()
            {
                try
                {
                    log.Info("Read Header");
                    await ReadHeaderPacket();
                    server.AddConnection(this);
                    rdpServer = await server.OpenRdpServer(ReceivePort, this);

                    log.Info("Wait for connection");
                    // wait for target
                    DateTime pingTiming = default;
                    while (TargetSocket == null)
                    {
                        if ((DateTime.Now - pingTiming).TotalSeconds > 1)
                        {
                            pingTiming = DateTime.Now;
                            await SendPing();
                        }
                        Thread.Sleep(10);
                    }

                    // connecting
                    log.Info("Connected Target!");
                    await SendConnecting();

                    // trasaction
                    log.Info("Exchange Connections!");
                    Task receive = Task.Factory.StartNew(RemoteToHost);
                    Task send = Task.Factory.StartNew(HostToRemote);

                    while (IsRun)
                    {
                        if (receive.IsFaulted)
                        {
                            throw receive.Exception;
                        }
                        if (send.IsFaulted)
                        {
                            throw receive.Exception;
                        }

                        if (receive.IsCanceled || receive.IsCompleted || send.IsCanceled || send.IsCompleted)
                        {
                            break;
                        }

                        await Task.Delay(1);
                    }
                }
                catch (Exception e)
                {
                    if (server.IsRun)
                    {
                        log.Error(e.Message, e);
                    }
                }
                finally
                {
                    log.Info("Disconnect!");
                    server.RemoveConnection(this);
                    rdpServer?.RemoveConnection(this);
                    IsRun = false;
                }
            }

            ///////////////////////////////////////////////////////////////////////////////////////
            //
            //                    WAITING METHODS
            //
            ///////////////////////////////////////////////////////////////////////////////////////

            private Task SendPing()
            {
                return socket.WriteAsync(new byte[] { OPCODE_PING });
            }

            private Task SendConnecting()
            {
                return socket.WriteAsync(new byte[] { OPCODE_CONNECT });
            }

            private async Task SendThrowException(string msg)
            {
                try
                {
                    using (var st = new MemoryStream())
                    using (var writer = new BinaryWriter(st, Encoding.UTF8))
                    {
                        writer.Write(OPCODE_ERROR);
                        byte[] msgBytes = Encoding.UTF8.GetBytes(msg);
                        writer.Write(msgBytes.Length);
                        writer.Write(msgBytes);

                        await socket.WriteAsync(st.ToArray());
                    }
                }
                catch { }

                throw new ApplicationException(msg);
            }

            private async Task ReadHeaderPacket()
            {
                int packetSize = BitConverter.ToInt32(await socket.ReceiveBytesAsync(4, canceller.Token), 0);
                byte[] data = await socket.ReceiveBytesAsync(packetSize - 4, canceller.Token);

                using (var st = new MemoryStream(data, false))
                using (var reader = new BinaryReader(st, Encoding.UTF8))
                {
                    // Read Data
                    ClientVersion = reader.ReadInt32();
                    Name = reader.ReadString();
                    ReceivePort = reader.ReadInt32();

                    // Validate
                    if (ClientVersion != VERSION)
                    {
                        await SendThrowException($"Missmatch version. ({ClientVersion} != {VERSION})");
                    }

                    if (options.remotePortEnd < ReceivePort || ReceivePort < options.remotePortStart)
                    {
                        await SendThrowException($"{ReceivePort} is NOT available port. Please use between {options.remotePortStart:n0}~{options.remotePortEnd:n0}");
                    }
                }
            }

            //private async Task<byte[]> ReadBytes(int bytes)
            //{
            //    byte[] data = new byte[bytes];
            //    int offset = 0;
            //    while (offset < data.Length)
            //    {
            //        offset += await socket.ReceiveAsync(data, offset, data.Length - offset, canceller.Token);
            //    }
            //    return data;
            //}


            ///////////////////////////////////////////////////////////////////////////////////////
            //
            //                    BROADCAST
            //
            ///////////////////////////////////////////////////////////////////////////////////////

            private void RemoteToHost() => WorkStream("Receive", TargetSocket, socket);
            private void HostToRemote() => WorkStream("Send", socket, TargetSocket);
            private void WorkStream(string tag, Socket read, Socket write)
            {
                log.Info($"[{tag}] Start");
                byte[] buffer = new byte[BUFFER_SIZE];
                try
                {
                    FailedDelayTimeLock delayLock = new FailedDelayTimeLock();

                    while (IsRun && read.Connected && write.Connected && !canceller.IsCancellationRequested)
                    {
                        int size = read.Receive(buffer, 0, buffer.Length, SocketFlags.None);
                        if (size > 0)
                        {
                            write.Send(buffer, 0, size, SocketFlags.None);
                            delayLock.Success();
                        }
                        else if (delayLock.FailedDuration > options.timeout)
                        {
                            log.Warn($"[{tag}] Timeout");
                            break;
                        }
                        else
                        {
                            delayLock.Wait();
                        }
                    }
                }
                catch (Exception e)
                {
                    log.Error($"[{tag}] {e.Message}", e);
                }
                finally
                {
                    log.Info($"[{tag}] End");
                }
            }
        }
    }
}
