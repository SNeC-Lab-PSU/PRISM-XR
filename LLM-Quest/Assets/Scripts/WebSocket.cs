using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class WebSocket
{
    private ClientWebSocket _client;
    private CancellationTokenSource _cancellationTokenSource;
    private readonly ConcurrentQueue<Action> _mainThreadActions = new ConcurrentQueue<Action>();
    private readonly int _receiveBufferSize;
    private readonly int _maxActionsPerFrame;
    private bool _isConnected = false;
    private Task _receiveTask;
    // Limit max actions per frame to avoid freezing

    // Events
    public event Action OnOpen;
    public event Action<byte[]> OnMessage;
    public event Action<int> OnClose;
    public event Action<string> OnError;

    public WebSocket(string url, int receiveBufferSize = 8192, int maxActionsPerFrame = 10)
    {
        Url = url;
        _receiveBufferSize = receiveBufferSize;
        _maxActionsPerFrame = maxActionsPerFrame;
        _client = new ClientWebSocket();
        _cancellationTokenSource = new CancellationTokenSource();
    }

    public string Url { get; private set; }
    public bool IsConnected => _isConnected;

    /// <summary>
    /// Connects to the WebSocket server
    /// </summary>
    /// <returns>A task representing the connection operation</returns>
    public async Task Connect()
    {
        if (_isConnected)
        {
            Debug.LogWarning("WebSocket is already connected");
            return;
        }

        try
        {
            await _client.ConnectAsync(new Uri(Url), _cancellationTokenSource.Token);
            _isConnected = true;

            // Queue the OnOpen event to be executed on the main thread
            _mainThreadActions.Enqueue(() => OnOpen?.Invoke());

            // Start the receive loop
            _receiveTask = ReceiveLoop();
        }
        catch (Exception e)
        {
            _mainThreadActions.Enqueue(() => OnError?.Invoke(e.Message));
            Debug.LogError($"WebSocket connection error: {e.Message}");
            CloseInternal(1006); // Abnormal closure
        }
    }

    /// <summary>
    /// Sends binary data to the server
    /// </summary>
    /// <param name="data">The binary data to send</param>
    /// <returns>A task representing the send operation</returns>
    public async Task Send(byte[] data)
    {
        if (!_isConnected)
        {
            Debug.LogError("Cannot send data, WebSocket is not connected");
            return;
        }

        try
        {
            await _client.SendAsync(
                new ArraySegment<byte>(data),
                WebSocketMessageType.Binary,
                true,
                _cancellationTokenSource.Token);
        }
        catch (Exception e)
        {
            _mainThreadActions.Enqueue(() => OnError?.Invoke(e.Message));
            Debug.LogError($"WebSocket send error: {e.Message}");
        }
    }

    /// <summary>
    /// Closes the WebSocket connection
    /// </summary>
    /// <param name="code">The close status code</param>
    /// <param name="reason">The reason for closing</param>
    /// <returns>A task representing the close operation</returns>
    public async Task Close(int code = 1000, string reason = "Normal closure")
    {
        if (!_isConnected)
            return;
        _isConnected = false;

        try
        {
            await _client.CloseAsync(
                (WebSocketCloseStatus)code,
                reason,
                _cancellationTokenSource.Token);

            CloseInternal(code);
        }
        catch (Exception e)
        {
            _mainThreadActions.Enqueue(() => OnError?.Invoke(e.Message));
            Debug.LogError($"WebSocket close error: {e.Message}");
        }
    }

    /// <summary>
    /// Processes any WebSocket events and ensures they are executed on Unity's main thread.
    /// Call this method from your MonoBehaviour's Update method.
    /// </summary>
    public void DispatchMessageQueue()
    {
        int actionsProcessed = 0;

        while (_mainThreadActions.TryDequeue(out Action action) && actionsProcessed < _maxActionsPerFrame)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Debug.LogError($"Exception when executing WebSocket callback: {e.Message}");
            }
            actionsProcessed++;
        }
    }

    private async Task ReceiveLoop()
    {
        byte[] buffer = new byte[_receiveBufferSize];

        try
        {
            while (_isConnected && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                WebSocketReceiveResult result = null;
                List<byte> messageData = new List<byte>();

                do
                {
                    result = await _client.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        _cancellationTokenSource.Token);

                    // Add received data to our message
                    if (result.Count > 0)
                    {
                        byte[] data = new byte[result.Count];
                        Array.Copy(buffer, data, result.Count);
                        messageData.AddRange(data);
                    }
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // Server initiated close
                    await _client.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Server closed connection",
                        CancellationToken.None);

                    int closeCode = result.CloseStatus.HasValue ? (int)result.CloseStatus.Value : 1000;
                    CloseInternal(closeCode);
                }
                else
                {
                    // We have a complete message, queue it for processing on the main thread
                    byte[] finalMessage = messageData.ToArray();
                    _mainThreadActions.Enqueue(() => OnMessage?.Invoke(finalMessage));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the token is canceled
        }
        catch (Exception e)
        {
            if (_isConnected)
            {
                _mainThreadActions.Enqueue(() => OnError?.Invoke(e.Message));
                Debug.LogError($"WebSocket receive error: {e.Message}");
                CloseInternal(1006); // Abnormal closure
            }
        }
    }

    private void CloseInternal(int closeCode)
    {
        if (!_isConnected)
            return;

        _isConnected = false;
        _cancellationTokenSource.Cancel();
        _mainThreadActions.Enqueue(() => OnClose?.Invoke(closeCode));

        // Clean up resources
        _client.Dispose();
        _client = new ClientWebSocket();
        _cancellationTokenSource = new CancellationTokenSource();
    }

    /// <summary>
    /// Disposes the WebSocket resources
    /// </summary>
    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        _client.Dispose();
        _isConnected = false;
    }
}