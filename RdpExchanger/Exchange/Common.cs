using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

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

        public const byte OPCODE_PING = 0;
        public const byte OPCODE_CONNECT = 1;
        public const byte OPCODE_ERROR = 2;
    }

    public interface IExchangeWorker
    {
        bool IsRun { get; }
        Task Start();
        Task Stop();
    }
}
