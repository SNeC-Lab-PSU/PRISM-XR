using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public class LowLevelSocketClient
{
    private Socket _socket;
    private Thread _receiveThread;
    private volatile bool _running;
    private string _host = "localhost";
    private int _port;
    private readonly ConcurrentQueue<SyncObjectUpdate> _updateQueue = new ConcurrentQueue<SyncObjectUpdate>();
    private int _maxNumsPerFrame = 10;
    public event Action<SyncObjectUpdate> OnObjectSynced;

    public struct SyncObjectUpdate
    {
        public int ObjectId;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;
        public int ExtraState;
    }

    public LowLevelSocketClient(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public void Connect()
    {
        try
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _socket.Connect(_host, _port);
            _socket.NoDelay = true; // disable Nagle's algorithm for lower latency

            _running = true;
            _receiveThread = new Thread(ReceiveLoop);
            _receiveThread.IsBackground = true;
            _receiveThread.Start();

            Debug.Log("Connected to the low-level socket server.");
        }
        catch (Exception ex)
        {
            Debug.LogError("Socket Connect error: " + ex.Message);
        }
    }

    public void Disconnect()
    {
        _running = false;

        try
        {
            if (_socket != null && _socket.Connected)
            {
                _socket.Shutdown(SocketShutdown.Both);
                _socket.Close();
                _socket = null;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("Socket close error: " + ex.Message);
        }

        _receiveThread?.Join();
        Debug.Log("Low-level socket client disconnected.");
    }

    private void ReceiveLoop()
    {
        byte[] buffer = new byte[48];

        while (_running)
        {
            try
            {
                if (_socket != null && _socket.Available > 0)
                {
                    int bytesReceived = _socket.Receive(buffer);
                    if (bytesReceived > 0)
                    {
                        HandleSyncedObject(buffer);
                    }
                }
                else
                {
                    Thread.Sleep(1); // yield CPU but still poll fast
                }
            }
            catch (SocketException se)
            {
                Debug.LogError("SocketException: " + se.Message);
                break;
            }
            catch (Exception ex)
            {
                Debug.LogError("ReceiveLoop error: " + ex.Message);
                break;
            }
        }
        Debug.Log("Low-level socket client receiveLoop thread exited.");
    }

    private void HandleSyncedObject(byte[] bodyBytes)
    {
        int objectId = BitConverter.ToInt32(bodyBytes, 0);
        float px = BitConverter.ToSingle(bodyBytes, 4);
        float py = BitConverter.ToSingle(bodyBytes, 8);
        float pz = BitConverter.ToSingle(bodyBytes, 12);

        float rx = BitConverter.ToSingle(bodyBytes, 16);
        float ry = BitConverter.ToSingle(bodyBytes, 20);
        float rz = BitConverter.ToSingle(bodyBytes, 24);
        float rw = BitConverter.ToSingle(bodyBytes, 28);

        float sx = BitConverter.ToSingle(bodyBytes, 32);
        float sy = BitConverter.ToSingle(bodyBytes, 36);
        float sz = BitConverter.ToSingle(bodyBytes, 40);

        int extraState = BitConverter.ToInt32(bodyBytes, 44);

        _updateQueue.Enqueue(new SyncObjectUpdate
        {
            ObjectId = objectId,
            Position = new Vector3(px, py, pz),
            Rotation = new Quaternion(rx, ry, rz, rw),
            Scale = new Vector3(sx, sy, sz),
            ExtraState = extraState
        });
    }

    public void DispatchData()
    {
        int numProcessed = 0;
        while (_updateQueue.TryDequeue(out SyncObjectUpdate update) && numProcessed < _maxNumsPerFrame)
        {
            OnObjectSynced?.Invoke(update);
            numProcessed++;
        }
    }

    public void Send(string message)
    {
        if (_socket != null && _socket.Connected)
        {
            byte[] data = Encoding.UTF8.GetBytes(message);
            _socket.Send(data);
        }
    }

    public void Send(byte[] data)
    {
        if (_socket != null && _socket.Connected)
        {
            _socket.Send(data);
        }
    }
}
