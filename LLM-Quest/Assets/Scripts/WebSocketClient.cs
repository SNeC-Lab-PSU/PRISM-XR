using System.Text;
using UnityEngine;
using System;
using System.IO;
using Newtonsoft.Json;
using System.Threading.Tasks;

public class WebSocketClient : MonoBehaviour
{
    public int ClientID { get; private set; }
    [SerializeField]
    private string _serverIP = "localhost";
    [SerializeField]
    private int _serverPort = 48101;
    [SerializeField]
    private int _serverPortLowLevel = 48102;
    [SerializeField]
    private bool _vrMode = false;

    CaptureAudio _captureAudio;
    ObjectCreator _objectCreator;
    AnimationLibrary _animationLibrary;
    ContextLibrary _contextLibrary;
    UserFeedback _userFeedback;
    SyncManager _syncManager;
    private WebSocket _webSocket;
    private LowLevelSocketClient _lowLevelSocketClient;
    private string _delimiter = "<END_HEADER>";
    private string _serverUrl => $"ws://{_serverIP}:{_serverPort}";

    [Serializable]
    public class MessageHeader
    {
        public string type;       // e.g., "text", "image", "audio"
        public int size;          // Size of the body in bytes
        public string extraInfo;  // Optional: for additional information, e.g., filename for image/audio

        public MessageHeader(string type, int size, string extraInfo = null)
        {
            this.type = type;
            this.size = size;
            this.extraInfo = extraInfo;
        }
    }

    void Start()
    {
        _captureAudio = GetComponent<CaptureAudio>();
        _objectCreator = GetComponent<ObjectCreator>();
        _animationLibrary = GetComponent<AnimationLibrary>();
        _contextLibrary = GetComponent<ContextLibrary>();
        _userFeedback = GetComponent<UserFeedback>();
        _syncManager = GetComponent<SyncManager>();
        StartWebClient();
    }

    void Update()
    {
        // processes any socket events and ensures they are executed on Unity's main thread.
        if (_webSocket != null)
        {
            _webSocket.DispatchMessageQueue();
        }
        _lowLevelSocketClient?.DispatchData();

        // Send a message when pressing the T key
        if (Input.GetKeyDown(KeyCode.T))
        {
            SendTextToServer("Hello from Unity!");
        }

        // Send an image when pressing the I key
        if (Input.GetKeyDown(KeyCode.I))
        {
            string imagePath = Application.persistentDataPath + "/testimg.jpg";
            SendImageToServer(imagePath);
        }

        // Send an audio file when pressing the A key
        if (Input.GetKeyDown(KeyCode.A))
        {
            string audioPath = Application.persistentDataPath + "/recorded_audio.wav";
            SendAudioToServer(audioPath);
        }
    }

    async void StartWebClient()
    {
        // Connect to the low-level socket server
        _lowLevelSocketClient = new LowLevelSocketClient(_serverIP, _serverPortLowLevel);

        _lowLevelSocketClient.OnObjectSynced += (syncUpdates) =>
        {
            HandleSyncedObject(syncUpdates);
        };

        _lowLevelSocketClient.Connect();


        _webSocket = new WebSocket(_serverUrl);

        _webSocket.OnOpen += () =>
        {
            Debug.Log("Connected to WebSocket server!");
            // Send supported object prefab types to the server
            string objectPrefabTypes = _contextLibrary.GetAllPrefabNames();
            byte[] objectPrefabTypesBytes = Encoding.UTF8.GetBytes(objectPrefabTypes);
            SendSpecialTypeToServer("context_data", objectPrefabTypesBytes, "prefab");
            if (_vrMode)
            {
                // Register as VR user, send camera position and rotation to the server
                string poseInfo = "Camera to world Matrix:\n" + Matrix4x4.identity.ToString();
                SendSpecialTypeToServer("registration", Encoding.UTF8.GetBytes(poseInfo), "VR user");
            }
        };

        _webSocket.OnMessage += (bytes) =>
        {
            HandleServerMessage(bytes);
        };

        _webSocket.OnClose += (closeCode) =>
        {
            Debug.Log($"WebSocket closed with code: {closeCode}");
        };

        // Connect to the server
        await _webSocket.Connect();
    }

    #region Send Data to Server
    private async Task SendDataToServer(string dataType, byte[] data, string extraInfo = null)
    {
        if (_webSocket.IsConnected)
        {
            try
            {
                // Create JSON header
                var header = new MessageHeader(dataType, data.Length, extraInfo);
                string headerJson = JsonConvert.SerializeObject(header);

                // Combine header and data
                byte[] headerBytes = Encoding.UTF8.GetBytes(headerJson + _delimiter);
                byte[] combinedData = new byte[headerBytes.Length + data.Length];
                Buffer.BlockCopy(headerBytes, 0, combinedData, 0, headerBytes.Length);
                Buffer.BlockCopy(data, 0, combinedData, headerBytes.Length, data.Length);

                // Send data
                await _webSocket.Send(combinedData);
                Debug.Log($"{dataType} sent successfully!");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error sending {dataType}: {ex.Message}");
            }
        }
        else
        {
            Debug.LogWarning("WebSocket is not open. Unable to send data.");
        }
    }

    public async void SendSpecialTypeToServer(string dataType, byte[] data, string extraInfo = null)
    {
        await SendDataToServer(dataType, data, extraInfo);
    }

    public async void SendTextToServer(string text)
    {
        byte[] textBytes = Encoding.UTF8.GetBytes(text);
        await SendDataToServer("text", textBytes);
    }

    public async void SendImageToServer(string imagePath)
    {
        byte[] imageData = File.ReadAllBytes(imagePath);
        await SendDataToServer("image", imageData, Path.GetFileName(imagePath));
    }

    public async void SendImageBytesToServer(byte[] bytes, string imageFilename)
    {
        await SendDataToServer("image", bytes, imageFilename);
    }

    public async void SendAudioToServer(string audioFilePath)
    {
        byte[] audioData = File.ReadAllBytes(audioFilePath);
        await SendDataToServer("audio", audioData, Path.GetFileName(audioFilePath));
    }

    public void SendObjectDataToServer(SyncManager.SyncObjectData objectData, string extraInfo = null)
    {
        int objectId = objectData.ID;
        Vector3 pos = objectData.Position;
        Quaternion rot = objectData.Rotation;
        Vector3 scl = objectData.Scale;
        int extraState = 0;
        if (extraInfo == "transferOwner")
        {
            extraState = 1;
        }

        // Pack i10fi => 4 + 10*4 + 4 = 48 bytes in network byte order
        byte[] data = new byte[48];
        Array.Copy(BitConverter.GetBytes(objectId), 0, data, 0, 4);

        Array.Copy(BitConverter.GetBytes(pos.x), 0, data, 4, 4);
        Array.Copy(BitConverter.GetBytes(pos.y), 0, data, 8, 4);
        Array.Copy(BitConverter.GetBytes(pos.z), 0, data, 12, 4);

        Array.Copy(BitConverter.GetBytes(rot.x), 0, data, 16, 4);
        Array.Copy(BitConverter.GetBytes(rot.y), 0, data, 20, 4);
        Array.Copy(BitConverter.GetBytes(rot.z), 0, data, 24, 4);
        Array.Copy(BitConverter.GetBytes(rot.w), 0, data, 28, 4);

        Array.Copy(BitConverter.GetBytes(scl.x), 0, data, 32, 4);
        Array.Copy(BitConverter.GetBytes(scl.y), 0, data, 36, 4);
        Array.Copy(BitConverter.GetBytes(scl.z), 0, data, 40, 4);

        Array.Copy(BitConverter.GetBytes(extraState), 0, data, 44, 4);

        _lowLevelSocketClient.Send(data);
    }
    #endregion

    #region Receive Data from Server

    private void HandleServerMessage(byte[] bytes)
    {
        try
        {
            // Locate the delimiter within the byte array
            byte[] delimiterBytes = Encoding.UTF8.GetBytes(_delimiter);
            int delimiterIndex = FindDelimiterIndex(bytes, delimiterBytes);

            if (delimiterIndex == -1)
            {
                Debug.LogError("Invalid message format: Missing delimiter.");
                return;
            }

            // Extract the header and body from the byte array
            byte[] headerBytes = new byte[delimiterIndex];
            byte[] bodyBytes = new byte[bytes.Length - delimiterIndex - delimiterBytes.Length];
            Buffer.BlockCopy(bytes, 0, headerBytes, 0, delimiterIndex);
            Buffer.BlockCopy(bytes, delimiterIndex + delimiterBytes.Length, bodyBytes, 0, bodyBytes.Length);

            // Parse the JSON header
            string headerJson = Encoding.UTF8.GetString(headerBytes);
            var header = JsonUtility.FromJson<MessageHeader>(headerJson);

            // Handle based on type
            switch (header.type)
            {
                case "text":
                    string message = Encoding.UTF8.GetString(bodyBytes);
                    Debug.Log($"Get text message from server:\n{message}");
                    break;

                case "image":
                    HandleImage(header, bodyBytes);
                    break;

                case "audio":
                    HandleAudio(header, bodyBytes);
                    break;

                case "sync_object":
                    HandleSyncedObject(header, bodyBytes);
                    break;

                case "object":
                    HandleObjectCreation(header, bodyBytes);
                    break;

                case "animation":
                    HandleAnimationCreation(header, bodyBytes);
                    break;

                case "registration":
                    HandleRegistrationResults(header, bodyBytes);
                    break;

                case "context_request":
                    HandleContextRequest(header, bodyBytes);
                    break;

                default:
                    Debug.LogError($"Unknown message type: {header.type}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error processing server message: {ex.Message}");
        }
    }

    private void HandleImage(MessageHeader header, byte[] bodyBytes)
    {
        try
        {
            // Save or process the image
            string savePath = $"{Application.persistentDataPath}/{header.extraInfo}";
            File.WriteAllBytes(savePath, bodyBytes);
            Debug.Log($"Image saved to: {savePath}");
            _userFeedback.ShowDialogWithImg(savePath);
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error processing image: {ex.Message}");
        }
    }

    private void HandleAudio(MessageHeader header, byte[] bodyBytes)
    {
        try
        {
            // Save or process the audio file
            string savePath = $"{Application.persistentDataPath}/{header.extraInfo}";
            File.WriteAllBytes(savePath, bodyBytes);
            Debug.Log($"Audio saved to: {savePath}");
            // Play the audio
            AudioType type = header.extraInfo.EndsWith(".wav") ? AudioType.WAV : AudioType.MPEG;
            StartCoroutine(_captureAudio.PlayAudioClipFromFile(savePath, type));
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error processing audio: {ex.Message}");
        }
    }

    private void HandleSyncedObject(MessageHeader header, byte[] bodyBytes)
    {
        // i10f => 4 + 10*4 = 44 bytes
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

        string extraInfo = header.extraInfo;
        if (extraInfo == "transferOwner")
        {
            _syncManager.AddSyncObject(objectId);
        }
        _syncManager.SyncObject(objectId, px, py, pz, rx, ry, rz, rw, sx, sy, sz);
    }

    private void HandleSyncedObject(LowLevelSocketClient.SyncObjectUpdate syncObjectUpdate)
    {
        int id = syncObjectUpdate.ObjectId;
        int extraState = syncObjectUpdate.ExtraState;
        //Debug.Log("Synced object update received: " + id + "extraState: " + extraState);
        if (extraState == 1) // Transfer owner
        {
            _syncManager.AddSyncObject(id);
        }
        Vector3 pos = syncObjectUpdate.Position;
        Quaternion rot = syncObjectUpdate.Rotation;
        _syncManager.SyncObject(id, pos, rot, syncObjectUpdate.Scale);
    }

    private void HandleObjectCreation(MessageHeader header, byte[] bodyBytes)
    {
        try
        {
            // Deserialize the object
            string objectJson = Encoding.UTF8.GetString(bodyBytes);
            Debug.Log($"Object to be created: {objectJson}");
            string extraInfo = header.extraInfo;
            bool isSynced = extraInfo.Contains("sync");
            var objectData = JsonConvert.DeserializeObject<ObjectCreator.ObjectData>(objectJson);
            if (extraInfo.Contains(" ") && int.TryParse(extraInfo.Split(' ')[1], out int objectId))
            {
                _objectCreator.CreateObject(objectData, objectId, isSynced);
            }
            else if (int.TryParse(extraInfo, out int regObjectId))
            {
                _objectCreator.CreateObject(objectData, regObjectId, isSynced);
            }
            else
            {
                Debug.LogError("Invalid object ID.");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error creating object: {ex.Message}");
        }
    }

    private void HandleAnimationCreation(MessageHeader header, byte[] bodyBytes)
    {
        try
        {
            // Deserialize the animation
            string animationJson = Encoding.UTF8.GetString(bodyBytes);
            Debug.Log($"Animation to be created: {animationJson}");
            var animationData = JsonConvert.DeserializeObject<AnimationLibrary.AnimationData>(animationJson);
            string extraInfo = header.extraInfo;
            bool isSynced = extraInfo == "sync";
            _animationLibrary.AddToAnimationPool(animationData, isSynced);
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error creating animation: {ex.Message}");
        }
    }

    private void HandleRegistrationResults(MessageHeader header, byte[] bodyBytes)
    {
        Debug.Log("Registration results received: " + header.extraInfo);
        Debug.Log(Encoding.UTF8.GetString(bodyBytes));
        if (header.extraInfo == "failure")
        {
            // At the same time, an audio with failure reminder will be sent from the server
            Debug.LogWarning("Tag registration failed.");
        }
        else
        {
            // If registration is successful, the user ID is sent back through the extraInfo field
            ClientID = int.Parse(header.extraInfo);
            // Deserialize the tag registration results
            string tagJson = Encoding.UTF8.GetString(bodyBytes);
            var tagData = JsonConvert.DeserializeObject<TagVisualizer.TagData>(tagJson);
            var tagToWorldMatrix = tagData.GetTagToWorldMatrix();
            Vector3 tagPosition = tagToWorldMatrix.GetColumn(3); // Extract position from cameraToWorldMatrix
            Quaternion tagRotation = tagToWorldMatrix.rotation;
            _userFeedback.RegisterTag(tagPosition, tagRotation, tagData.TagScale);

            // Add the attached object to the list of owned objects, as this script should be attached to the main camera
            // This aims to synchronize user's head pose
            _syncManager.AddOwnedObject(ClientID * (1 << 24), gameObject);
        }
    }

    void HandleContextRequest(MessageHeader header, byte[] bodyBytes)
    {
        // Deserialize the context request
        string contextJson = Encoding.UTF8.GetString(bodyBytes);
        Debug.Log($"Context request received: {contextJson}");
        string contextData = _contextLibrary.GetContextData(contextJson);
        Debug.Log($"Context data to be sent: {contextData}");
        byte[] contextDataBytes = Encoding.UTF8.GetBytes(contextData);
        // Send the context data back to the server
        SendSpecialTypeToServer("context_data", contextDataBytes);
    }

    // Find the index of the delimiter from byte array
    private int FindDelimiterIndex(byte[] data, byte[] delimiter)
    {
        for (int i = 0; i < data.Length - delimiter.Length + 1; i++)
        {
            bool match = true;
            for (int j = 0; j < delimiter.Length; j++)
            {
                if (data[i + j] != delimiter[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
            {
                return i;
            }
        }
        return -1; // Delimiter not found
    }
    #endregion

    private async void OnApplicationQuit()
    {
        Debug.Log("Application quit. Disconnecting from server...");
        _lowLevelSocketClient?.Disconnect();
        await _webSocket?.Close();
    }
}
