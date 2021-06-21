using log4net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public static class Extensions
{
    public static async Task<Socket> AcceptAsync(this Socket socket, CancellationToken token = default, int timeoutMs = 0)
    {
        DateTime dt = DateTime.Now;
        var task = socket.BeginAccept(null, null);
        while (!task.IsCompleted)
        {
            if (timeoutMs > 0 && (DateTime.Now - dt).TotalMilliseconds > timeoutMs) return null;
            if (token.CanBeCanceled && token.IsCancellationRequested) return null;
            await Task.Delay(1);
        }
        return socket.EndAccept(task);
    }
    public static async Task ConnectAsync(this Socket socket, string host, int port, CancellationToken token = default, int timeoutMs = 0)
    {
        DateTime dt = DateTime.Now;
        var task = socket.BeginConnect(host, port, null, null);
        while (!task.IsCompleted)
        {
            if (timeoutMs > 0 && (DateTime.Now - dt).TotalMilliseconds > timeoutMs) return;
            if (token.CanBeCanceled && token.IsCancellationRequested) return;
            await Task.Delay(1);
        }
        socket.EndConnect(task);
    }
    public static async Task<int> ReceiveAsync(this Socket socket, byte[] data, int offset = 0, int length = -1, CancellationToken token = default, int timeoutMs = 0)
    {
        DateTime dt = DateTime.Now;
        if (length == -1) length = data.Length;
        var rst = socket.BeginReceive(data, offset, length, SocketFlags.None, null, null);
        while (!rst.IsCompleted)
        {
            if (timeoutMs > 0 && (DateTime.Now - dt).TotalMilliseconds > timeoutMs) return 0;
            if (token.CanBeCanceled && token.IsCancellationRequested) return 0;
            await Task.Delay(1);
        }
        return socket.EndReceive(rst);
    }
    public static async Task<byte[]> ReceiveBytesAsync(this Socket socket, int bytes, CancellationToken token = default)
    {
        byte[] data = new byte[bytes];
        int offset = 0;
        while (offset < data.Length)
        {
            offset += await socket.ReceiveAsync(data, offset, data.Length - offset, token);
        }
        return data;
    }
    public static async Task<int> WriteAsync(this Socket socket, byte[] data, int offset = 0, int length = -1, CancellationToken token = default, int timeoutMs = 0)
    {
        DateTime dt = DateTime.Now;
        if (length == -1) length = data.Length;
        var rst = socket.BeginSend(data, offset, length, SocketFlags.None, null, null);
        while (!rst.IsCompleted)
        {
            if (timeoutMs > 0 && (DateTime.Now - dt).TotalMilliseconds > timeoutMs) return 0;
            if (token.CanBeCanceled && token.IsCancellationRequested) return 0;
            await Task.Delay(1);
        }
        return socket.EndSend(rst);
    }

    public static string ToV4Ip(this Socket socket)
    {
        if (socket != null && socket.RemoteEndPoint != null)
        {
            return ((IPEndPoint)socket.RemoteEndPoint).Address.MapToIPv4().ToString();
        }
        return "__.__.__.__";
    }
    public static void Debug(this ILog log, Socket socket, object message) => log.Debug($"[{socket.ToV4Ip()}] {message}");
    public static void Info(this ILog log, Socket socket, object message) => log.Info($"[{socket.ToV4Ip()}] {message}");
    public static void Warn(this ILog log, Socket socket, object message) => log.Warn($"[{socket.ToV4Ip()}] {message}");
    public static void Error(this ILog log, Socket socket, object message) => log.Error($"[{socket.ToV4Ip()}] {message}");
}
