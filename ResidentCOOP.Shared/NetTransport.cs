// ResidentCOOP.Shared - NetTransport.cs
// TCP server and client transports with non-blocking I/O.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace ResidentCOOP.Shared
{
    public class NetMessage
    {
        public MessageType Type;
        public byte[] Payload;
    }

    /// <summary>
    /// TCP server that accepts exactly one client (2-player co-op).
    /// All I/O is non-blocking, designed to be polled from the game thread.
    /// </summary>
    public class NetServer : IDisposable
    {
        private TcpListener _listener;
        private TcpClient _client;
        private NetworkStream _stream;
        private byte[] _recvBuffer = new byte[NetProtocol.MAX_MESSAGE_SIZE];
        private MemoryStream _pendingData = new MemoryStream();
        private bool _disposed;

        public bool IsListening { get { return _listener != null; } }
        public bool HasClient { get { return _client != null && _client.Connected; } }

        public void Start(int port)
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
        }

        public bool TryAcceptClient()
        {
            if (_listener == null || _client != null) return false;
            if (!_listener.Pending()) return false;

            _client = _listener.AcceptTcpClient();
            _client.NoDelay = true;
            _client.ReceiveBufferSize = NetProtocol.MAX_MESSAGE_SIZE;
            _client.SendBufferSize = NetProtocol.MAX_MESSAGE_SIZE;
            _stream = _client.GetStream();
            _stream.ReadTimeout = 1;
            _stream.WriteTimeout = 1000;
            return true;
        }

        public bool Send(byte[] framedMessage)
        {
            if (_stream == null || !HasClient) return false;
            try
            {
                _stream.Write(framedMessage, 0, framedMessage.Length);
                return true;
            }
            catch (Exception)
            {
                DisconnectClient();
                return false;
            }
        }

        public List<NetMessage> ReceiveAll()
        {
            List<NetMessage> messages = new List<NetMessage>();
            if (_stream == null || !HasClient) return messages;

            try
            {
                while (_stream.DataAvailable)
                {
                    int bytesRead = _stream.Read(_recvBuffer, 0, _recvBuffer.Length);
                    if (bytesRead == 0)
                    {
                        DisconnectClient();
                        return messages;
                    }
                    _pendingData.Write(_recvBuffer, 0, bytesRead);
                }
            }
            catch (IOException) { }
            catch (SocketException) { DisconnectClient(); return messages; }

            ParseMessages(_pendingData, messages);
            return messages;
        }

        public void DisconnectClient()
        {
            try { if (_stream != null) _stream.Close(); } catch { }
            try { if (_client != null) _client.Close(); } catch { }
            _stream = null;
            _client = null;
            _pendingData.SetLength(0);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            DisconnectClient();
            try { if (_listener != null) _listener.Stop(); } catch { }
            _listener = null;
            _pendingData.Dispose();
        }

        private static void ParseMessages(MemoryStream buffer, List<NetMessage> output)
        {
            while (true)
            {
                byte[] data = buffer.ToArray();
                if (data.Length < 4) break;

                int msgLen = data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24);
                if (msgLen <= 0 || msgLen > NetProtocol.MAX_MESSAGE_SIZE)
                {
                    buffer.SetLength(0);
                    break;
                }

                int totalNeeded = 4 + msgLen;
                if (data.Length < totalNeeded) break;

                NetMessage msg = new NetMessage();
                msg.Type = (MessageType)data[4];
                msg.Payload = new byte[msgLen - 1];
                Buffer.BlockCopy(data, 5, msg.Payload, 0, msg.Payload.Length);
                output.Add(msg);

                int remaining = data.Length - totalNeeded;
                buffer.SetLength(0);
                if (remaining > 0)
                {
                    buffer.Write(data, totalNeeded, remaining);
                }
            }
        }
    }

    /// <summary>
    /// TCP client that connects to a host. Non-blocking polling.
    /// </summary>
    public class NetClient : IDisposable
    {
        private TcpClient _client;
        private NetworkStream _stream;
        private byte[] _recvBuffer = new byte[NetProtocol.MAX_MESSAGE_SIZE];
        private MemoryStream _pendingData = new MemoryStream();
        private bool _disposed;
        private volatile bool _connectFailed;

        public bool IsConnected { get { return _client != null && _client.Connected && _stream != null; } }
        public bool ConnectFailed { get { return _connectFailed; } }

        public void Connect(string host, int port)
        {
            _connectFailed = false;
            _client = new TcpClient();
            _client.NoDelay = true;
            _client.ReceiveBufferSize = NetProtocol.MAX_MESSAGE_SIZE;
            _client.SendBufferSize = NetProtocol.MAX_MESSAGE_SIZE;
            _client.BeginConnect(host, port, OnConnected, null);
        }

        private void OnConnected(IAsyncResult ar)
        {
            try
            {
                _client.EndConnect(ar);
                _stream = _client.GetStream();
                _stream.ReadTimeout = 1;
                _stream.WriteTimeout = 1000;
            }
            catch (Exception)
            {
                _connectFailed = true;
                Disconnect();
            }
        }

        public bool Send(byte[] framedMessage)
        {
            if (_stream == null || !IsConnected) return false;
            try
            {
                _stream.Write(framedMessage, 0, framedMessage.Length);
                return true;
            }
            catch (Exception)
            {
                Disconnect();
                return false;
            }
        }

        public List<NetMessage> ReceiveAll()
        {
            List<NetMessage> messages = new List<NetMessage>();
            if (_stream == null || !IsConnected) return messages;

            try
            {
                while (_stream.DataAvailable)
                {
                    int bytesRead = _stream.Read(_recvBuffer, 0, _recvBuffer.Length);
                    if (bytesRead == 0)
                    {
                        Disconnect();
                        return messages;
                    }
                    _pendingData.Write(_recvBuffer, 0, bytesRead);
                }
            }
            catch (IOException) { }
            catch (SocketException) { Disconnect(); return messages; }

            ParseMessages(_pendingData, messages);
            return messages;
        }

        public void Disconnect()
        {
            try { if (_stream != null) _stream.Close(); } catch { }
            try { if (_client != null) _client.Close(); } catch { }
            _stream = null;
            _client = null;
            _pendingData.SetLength(0);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
            _pendingData.Dispose();
        }

        private static void ParseMessages(MemoryStream buffer, List<NetMessage> output)
        {
            while (true)
            {
                byte[] data = buffer.ToArray();
                if (data.Length < 4) break;

                int msgLen = data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24);
                if (msgLen <= 0 || msgLen > NetProtocol.MAX_MESSAGE_SIZE)
                {
                    buffer.SetLength(0);
                    break;
                }

                int totalNeeded = 4 + msgLen;
                if (data.Length < totalNeeded) break;

                NetMessage msg = new NetMessage();
                msg.Type = (MessageType)data[4];
                msg.Payload = new byte[msgLen - 1];
                Buffer.BlockCopy(data, 5, msg.Payload, 0, msg.Payload.Length);
                output.Add(msg);

                int remaining = data.Length - totalNeeded;
                buffer.SetLength(0);
                if (remaining > 0)
                {
                    buffer.Write(data, totalNeeded, remaining);
                }
            }
        }
    }
}
