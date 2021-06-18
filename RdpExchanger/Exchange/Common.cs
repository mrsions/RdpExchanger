using System;
using System.Net;
using System.Net.Sockets;

namespace RdpExchanger
{

    public class Common
    {
        public const int VERSION = 1;
        public const int HEADER_SIZE = 256;
        public static ProgramOptions options = new ProgramOptions();

        public static int BUFFER_SIZE => options.bufferSize;
        public static int HOST_REMOTE_PORT => options.remotePort;
        public static int HOST_SERVER_PORT => options.hostPort;
    }

    public interface IExchangeWorker
    {
        bool IsRun { get; }
        void Start();
        void Stop();
    }
}
