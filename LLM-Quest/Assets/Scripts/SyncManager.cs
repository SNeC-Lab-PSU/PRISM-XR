using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/**
 * This script manages the synchronization of some objects among users.
 * For instance, the grabbable objects that position and orientation can be changed by players
 * and the head pose of users.
 * It is responsible for sending and receiving messages to and from the server.
 */
public class SyncManager : MonoBehaviour
{

    public class BiDictionary<TKey, TValue>
    {
        private Dictionary<TKey, TValue> _keyToValue = new Dictionary<TKey, TValue>();
        private Dictionary<TValue, TKey> _valueToKey = new Dictionary<TValue, TKey>();

        public bool Add(TKey key, TValue value)
        {
            if (_keyToValue.ContainsKey(key) || _valueToKey.ContainsKey(value))
            {
                Debug.LogError("Duplicate key or value.");
                return false;
            }

            _keyToValue[key] = value;
            _valueToKey[value] = key;
            return true;
        }

        public TValue GetValue(TKey key)
        {
            return _keyToValue[key];
        }

        public TKey GetKey(TValue value)
        {
            return _valueToKey[value];
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            return _keyToValue.TryGetValue(key, out value);
        }

        public bool TryGetKey(TValue value, out TKey key)
        {
            return _valueToKey.TryGetValue(value, out key);
        }

        public bool RemoveByKey(TKey key)
        {
            if (!_keyToValue.TryGetValue(key, out var value))
                return false;

            _keyToValue.Remove(key);
            _valueToKey.Remove(value);
            return true;
        }

        public bool RemoveByValue(TValue value)
        {
            if (!_valueToKey.TryGetValue(value, out var key))
                return false;

            _valueToKey.Remove(value);
            _keyToValue.Remove(key);
            return true;
        }

        public void Clear()
        {
            _valueToKey.Clear();
            _keyToValue.Clear();
        }

        public int Count => _keyToValue.Count;
    }

    public class SyncObjectData
    {
        public SyncObjectData(int id, GameObject gameObject)
        {
            ID = id;
            GameObject = gameObject;
            Scale = gameObject.transform.localScale;
        }
        private object _syncDataLock = new object();
        public int ID;
        public GameObject GameObject;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;
        private bool _hasChanged = false;
        public bool HasChanged
        {
            get
            {
                lock (_syncDataLock)
                {
                    return _hasChanged;
                }
            }
            set
            {
                lock (_syncDataLock)
                {
                    _hasChanged = value;
                }
            }
        }
    }

    private Dictionary<int, SyncObjectData> _ownedObjects = new Dictionary<int, SyncObjectData>();
    private Dictionary<int, SyncObjectData> _syncObjects = new Dictionary<int, SyncObjectData>();
    private BiDictionary<int, GameObject> _llmObjects = new BiDictionary<int, GameObject>();
    private List<int> _objsToBeRemoved = new List<int>();
    private float _posThreshold = 0.005f;
    private float _rotThreshold = 0.5f;
    private float _scaleThreshold = 0.01f;
    private float _syncInterval = 1 / 60f; // 60fps
    private float _lastSyncTime = 0;

    private WebSocketClient _webSocketClient;
    private UserFeedback _userFeedback;

    // Start is called before the first frame update
    void Start()
    {
        _webSocketClient = GetComponent<WebSocketClient>();
        _userFeedback = GetComponent<UserFeedback>();
    }

    // Update is called once per frame
    void Update()
    {
        // iterate over all sync objects to update the object data
        foreach (var syncObject in _syncObjects.Values)
        {
            if (syncObject.GameObject == null || ReferenceEquals(syncObject.GameObject, null))
            {
                _objsToBeRemoved.Add(syncObject.ID);
                continue;
            }
            if (syncObject.HasChanged)
            {
                GameObject syncGameObject = syncObject.GameObject;
                if (syncGameObject.transform.parent != null && syncGameObject.tag != "Player")
                {
                    syncGameObject.transform.localPosition = syncObject.Position;
                    syncGameObject.transform.localRotation = syncObject.Rotation;
                }
                else
                {
                    syncGameObject.transform.position = _userFeedback.GetPosFromTagCoor(syncObject.Position);
                    syncGameObject.transform.rotation = _userFeedback.GetRotFromTagCoor(syncObject.Rotation);
                }
                syncGameObject.transform.localScale = syncObject.Scale;
                syncObject.HasChanged = false;
            }
        }
        if (_objsToBeRemoved.Count > 0)
        {
            foreach (var id in _objsToBeRemoved)
            {
                _syncObjects.Remove(id);
            }
            _objsToBeRemoved.Clear();
        }

        // send data in a fixed interval
        if (Time.time - _lastSyncTime < _syncInterval)
        {
            _lastSyncTime = Time.time;
            return;
        }

        // iterate over all owned objects to update sync data
        foreach (var syncObject in _ownedObjects.Values)
        {
            if (syncObject.GameObject == null || ReferenceEquals(syncObject.GameObject, null))
            {
                _objsToBeRemoved.Add(syncObject.ID);
                continue;
            }
            // check if the object has changed
            if (HasChanged(syncObject))
            {
                GameObject syncGameObject = syncObject.GameObject;
                // Use local pose relative to the parent if having parent, otherwise, use coordinate relative to tag
                if (syncGameObject.transform.parent != null && syncGameObject.tag != "Player")
                {
                    syncObject.Position = syncGameObject.transform.localPosition;
                    syncObject.Rotation = syncGameObject.transform.localRotation;
                }
                else
                {
                    syncObject.Position = _userFeedback.GetPosInTagCoor(syncGameObject.transform.position);
                    syncObject.Rotation = _userFeedback.GetRotInTagCoor(syncGameObject.transform.rotation);
                }
                syncObject.Scale = syncGameObject.transform.localScale;
                // send the object data to the server
                _webSocketClient.SendObjectDataToServer(syncObject);
            }
        }
        if (_objsToBeRemoved.Count > 0)
        {
            foreach (var id in _objsToBeRemoved)
            {
                _ownedObjects.Remove(id);
            }
            _objsToBeRemoved.Clear();
        }
    }

    bool HasChanged(SyncObjectData syncObject)
    {
        GameObject syncGameObject = syncObject.GameObject;
        // check if the object has changed
        if (syncGameObject.transform.parent != null && syncGameObject.tag != "Player")
        {
            // check local position and rotation
            if (Vector3.Distance(syncObject.Position, syncGameObject.transform.localPosition) > _posThreshold ||
                Quaternion.Angle(syncObject.Rotation, syncGameObject.transform.localRotation) > _rotThreshold ||
                Vector3.Distance(syncObject.Scale, syncGameObject.transform.localScale) > _scaleThreshold)
            {
                return true;
            }
        }
        else
        {
            if (Vector3.Distance(syncObject.Position, _userFeedback.GetPosInTagCoor(syncGameObject.transform.position)) > _posThreshold ||
                Quaternion.Angle(syncObject.Rotation, _userFeedback.GetRotInTagCoor(syncGameObject.transform.rotation)) > _rotThreshold ||
                Vector3.Distance(syncObject.Scale, syncGameObject.transform.localScale) > _scaleThreshold)
            {
                return true;
            }
        }
        return false;
    }

    public void AddSyncObject(int id)
    {
        if (_llmObjects.TryGetValue(id, out var gameObject))
        {
            AddSyncObject(id, gameObject);
        }
    }

    public void AddSyncObject(int id, GameObject gameObject)
    {
        // Remove from OwnedObjects if it exists
        if (_ownedObjects.ContainsKey(id))
        {
            _ownedObjects.Remove(id);
        }
        var objectOwnership = gameObject.GetComponentInChildren<ObjectOwnership>();
        if (objectOwnership != null)
        {
            objectOwnership.SetOwnerShip(false);
        }
        SyncObjectData syncObject = new SyncObjectData(id, gameObject);
        UpdateSyncObjectData(syncObject);
        if (_syncObjects.ContainsKey(id))
        {
            _syncObjects[id] = syncObject;
        }
        else
        {
            _syncObjects.Add(id, syncObject);
        }
    }

    public void AddOwnedObject(GameObject gameObject, string extraInfo = null)
    {
        if (_llmObjects.TryGetKey(gameObject, out int id))
        {
            AddOwnedObject(id, gameObject, extraInfo);
        }
    }

    public void AddOwnedObject(int id, GameObject gameObject, string extraInfo = null)
    {
        // Remove from SyncObjects if it exists
        if (_syncObjects.ContainsKey(id))
        {
            _syncObjects.Remove(id);
        }
        var objectOwnership = gameObject.GetComponentInChildren<ObjectOwnership>();
        if (objectOwnership != null)
        {
            objectOwnership.SetOwnerShip(true);
        }
        SyncObjectData syncObject = new SyncObjectData(id, gameObject);
        UpdateSyncObjectData(syncObject);
        if (_ownedObjects.ContainsKey(id))
        {
            _ownedObjects[id] = syncObject;
        }
        else
        {
            _ownedObjects.Add(id, syncObject);
        }
        _webSocketClient.SendObjectDataToServer(syncObject, extraInfo);
    }

    public bool AddLLMObject(int id, GameObject gameObject)
    {
        return _llmObjects.Add(id, gameObject);
    }

    public int GetLLMObjectID(GameObject gameObject)
    {
        return _llmObjects.GetKey(gameObject);
    }

    private void UpdateSyncObjectData(SyncObjectData syncObject)
    {
        GameObject obj = syncObject.GameObject;
        if (obj.transform.parent == null || obj.tag == "Player")
        {
            syncObject.Position = _userFeedback.GetPosInTagCoor(obj.transform.position);
            syncObject.Rotation = _userFeedback.GetRotInTagCoor(obj.transform.rotation);
        }
        else
        {
            syncObject.Position = obj.transform.localPosition;
            syncObject.Rotation = obj.transform.localRotation;
        }
    }

    public void SyncObject(int id, float px, float py, float pz, float rx, float ry, float rz, float rw, float sx, float sy, float sz)
    {
        // Try to find the object in the LLM objects
        if (!_syncObjects.ContainsKey(id) && !_ownedObjects.ContainsKey(id) && _llmObjects.TryGetValue(id, out var gameObject))
        {
            AddSyncObject(id, gameObject);
        }
        if (_syncObjects.ContainsKey(id))
        {
            SyncObjectData syncObject = _syncObjects[id];
            syncObject.Position = new Vector3(px, py, pz);
            syncObject.Rotation = new Quaternion(rx, ry, rz, rw);
            syncObject.Scale = new Vector3(sx, sy, sz);
            syncObject.HasChanged = true;
        }
    }

    public void SyncObject(int id, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        // Try to find the object in the LLM objects
        if (!_syncObjects.ContainsKey(id) && !_ownedObjects.ContainsKey(id) && _llmObjects.TryGetValue(id, out var gameObject))
        {
            AddSyncObject(id, gameObject);
        }
        if (_syncObjects.ContainsKey(id))
        {
            SyncObjectData syncObject = _syncObjects[id];
            syncObject.Position = position;
            syncObject.Rotation = rotation;
            syncObject.Scale = scale;
            syncObject.HasChanged = true;
        }
    }
}
