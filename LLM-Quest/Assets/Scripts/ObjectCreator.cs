using Meta.XR;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using PassthroughCameraSamples;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

public class ObjectCreator : MonoBehaviour
{
    [SerializeField]
    private Material _rayMaterial;
    [SerializeField]
    float _rayDuration = 10f;
    [SerializeField]
    float _rayWidth = 0.01f;
    [SerializeField]
    LayerMask _envBaseLayerMask;
    [SerializeField]
    LayerMask _envMaxLayerMask;
    [SerializeField]
    List<GameObject> _createdObjects;
    [SerializeField]
    private WebCamTextureManager _webCamTextureManager;
    [SerializeField]
    private EnvironmentRaycastManager _raycastManager;

    private PassthroughCameraEye _cameraEye => _webCamTextureManager.Eye;
    CapturePhoto _capturePhoto;
    WebSocketClient _webSocketClient;
    AnimationLibrary _animationLibrary;
    ContextLibrary _contextLibrary;
    SyncManager _syncManager;
    int _envBaseLayer;
    int _envMaxLayer; // This layer is reserved for the whiteboard markers or other objects with specialized layermask exclusion requirements 

    [JsonConverter(typeof(StringEnumConverter))]
    public enum CoordinateSpaceEnum
    {
        pixel,
        world,
        local
    }

    [Serializable]
    public class ObjectData
    {
        [JsonProperty("objectName")]
        public string ObjectName { get; set; }

        [JsonProperty("prefabType")]
        public string PrefabType { get; set; }

        [JsonProperty("coordinateSpace")]
        public CoordinateSpaceEnum CoordinateSpace { get; set; }

        [JsonProperty("layer")]
        public int Layer { get; set; }

        [JsonProperty("centerCoordinates")]
        public int[] CenterCoordinates { get; set; } = null;

        [JsonProperty("position")]
        public float[] Position { get; set; } = null;

        [JsonProperty("orientation")]
        public float[] Orientation { get; set; } = null;

        [JsonProperty("parent")]
        public string Parent { get; set; } = null;

        [JsonProperty("scale")]
        public float[] Scale { get; set; } = null;

        [JsonProperty("color")]
        public float[] Color { get; set; } = null;
    }

    private void Start()
    {
        _capturePhoto = GetComponent<CapturePhoto>();
        _webSocketClient = GetComponent<WebSocketClient>();
        _animationLibrary = GetComponent<AnimationLibrary>();
        _contextLibrary = GetComponent<ContextLibrary>();
        _syncManager = GetComponent<SyncManager>();
        _envBaseLayer = _envBaseLayerMask == 0 ? 0 : Mathf.RoundToInt(Mathf.Log(_envBaseLayerMask.value, 2));
        _envMaxLayer = _envMaxLayerMask == 0 ? 0 : Mathf.RoundToInt(Mathf.Log(_envMaxLayerMask.value, 2));
    }

    public GameObject CreateObject(ObjectData data, int id, bool synced = false)
    {
        GameObject prefab = Resources.Load<GameObject>(data.PrefabType);
        GameObject instance = null;
        bool isPrimitive = prefab == null;
        if (isPrimitive)
        {
            instance = CreateObjPrimitive(data.PrefabType);
        }
        else
        {
            instance = Instantiate(prefab, new Vector3(0, 10, 0), Quaternion.identity);
            // Set the user name
            if (data.PrefabType == "User")
            {
                instance.GetComponent<UserInfo>().UpdateUserName(data.ObjectName);
            }
        }

        if (instance == null)
        {
            Debug.LogWarning("Failed to instantiate the object.");
            return null;
        }

        instance.name = data.ObjectName;
        instance.layer = _envBaseLayer + data.Layer;
        // update all its children layer
        foreach (Transform child in instance.GetComponentInChildren<Transform>())
        {
            child.gameObject.layer = Mathf.Min(instance.layer + 1, _envMaxLayer - 1);
        }
        string parent = data.Parent;
        GameObject parentObj = parent != null ? Utils.GetGameObject(parent) : null;
        if (parentObj != null)
        {
            instance.transform.SetParent(parentObj.transform);
        }

        float[] scale = data.Scale;
        if (scale != null && scale.Length == 3)
        {
            instance.transform.localScale = new Vector3(scale[0], scale[1], scale[2]);
        }
        float[] color = data.Color;
        if (color != null && color.Length >= 3 && instance.GetComponent<Renderer>() != null)
        {
            instance.GetComponent<Renderer>().material.color = new Color(color[0], color[1], color[2]);
        }
        // add to the list
        _createdObjects.Add(instance);

        if (data.CoordinateSpace == CoordinateSpaceEnum.pixel)
        {
            // Get the ray pointing from camera to the pixel coordinates in 3D world
            Ray ray = GetRayFromPixelCoor(data.CenterCoordinates[0], data.CenterCoordinates[1]);
            // Correct the object's position to avoid collision with nearby surfaces.
            AlignObject(instance, ray); // Must be done after setting the scale
        }
        else
        {
            if (data.CoordinateSpace == CoordinateSpaceEnum.local && parentObj != null)
            {
                // the localposition is generated in a custom based on the world coordinate system, i.e., x refers to right, y refers to up, z refers to forward
                // adjust the local position if the parent object has specified orientation
                Vector3 initLocalPosition = new Vector3(data.Position[0], data.Position[1], data.Position[2]);
                Vector3 rotateLocalPostion = parentObj.transform.InverseTransformDirection(initLocalPosition);
                instance.transform.localPosition = rotateLocalPostion;
            }
            else
            {
                instance.transform.position = new Vector3(data.Position[0], data.Position[1], data.Position[2]);
            }
            if (data.Orientation != null && data.Orientation.Length == 4)
            {
                instance.transform.rotation = new Quaternion(data.Orientation[0], data.Orientation[1], data.Orientation[2], data.Orientation[3]);
            }
            // Create a ray point from camera to the object
            Ray ray = new Ray(Camera.main.transform.position, instance.transform.position - Camera.main.transform.position);
            StartCoroutine(VisualizeRay(ray));
        }
        _syncManager.AddLLMObject(id, instance);
        if (data.PrefabType == "WhiteboardSet")
        {
            var marker = instance.GetComponentsInChildren<WhiteboardMarker>()[0].gameObject;
            Utils.SetLayerRecursively(marker, _envMaxLayer);
            _syncManager.AddLLMObject(id + 1, marker);
            _animationLibrary.MakeGrabbable(marker, synced);
            var eraser = instance.GetComponentsInChildren<WhiteboardEraser>()[0].gameObject;
            Utils.SetLayerRecursively(eraser, _envMaxLayer);
            _syncManager.AddLLMObject(id + 2, eraser);
            _animationLibrary.MakeGrabbable(eraser, synced);
            // If the whiteboard is opposite to the user, rotate it to face the user
            Vector3 directionToUser = (_contextLibrary.GetUserPosition() - instance.transform.position).normalized;
            Vector3 forward = instance.transform.forward;
            if (Vector3.Dot(directionToUser, forward) < 0)
            {
                instance.transform.LookAt(_contextLibrary.GetUserPosition());
            }
            // Lock the rotation of the whiteboard set on x and z axis, and lock scale
            instance.transform.rotation = Quaternion.Euler(0, instance.transform.rotation.eulerAngles.y, 0);
            instance.transform.localScale = new Vector3(1, 1, 1);
        }
        else if (data.PrefabType == "Eraser" || data.PrefabType == "Marker")
        {
            Utils.SetLayerRecursively(instance, _envMaxLayer);
        }
        if (!synced)
        {
            // After creating the object, send world coordinates to the server for synchronization
            SendObjectToServer(data, instance);
        }
        return instance;
    }

    private void SendObjectToServer(ObjectData objData, GameObject instance)
    {
        // Make a deep copy to avoid modifying the original object
        string json = JsonConvert.SerializeObject(objData);
        ObjectData newData = JsonConvert.DeserializeObject<ObjectData>(json);

        newData.CoordinateSpace = CoordinateSpaceEnum.world;
        newData.Position = new float[] { instance.transform.position.x, instance.transform.position.y, instance.transform.position.z };
        newData.Orientation = new float[] { instance.transform.rotation.x, instance.transform.rotation.y, instance.transform.rotation.z, instance.transform.rotation.w };

        string jsonData = JsonConvert.SerializeObject(newData);
        Debug.Log("Sending object data to server: " + jsonData);
        _webSocketClient.SendSpecialTypeToServer("object", Encoding.UTF8.GetBytes(jsonData), _syncManager.GetLLMObjectID(instance).ToString());
    }

    void AlignObject(GameObject obj, Ray ray)
    {
        // Send a raycast to detect the surface
        if (_raycastManager.Raycast(ray, out var hit))
        {
            Bounds bounds = Utils.GetBoundObj(obj);
            // Decide which axis to align based on the surface normal
            float dotProduct = Vector3.Dot(hit.normal, Vector3.up);
            int i = 0; // Align the forward direction to the surface normal by default
            if (dotProduct < -0.7f)
            {
                // Ceiling, align the downward direction to the surface normal
                i = 5;
            }
            else if (dotProduct > 0.7f)
            {
                // Floor, align the upward direction to the surface normal
                i = 4;
            }
            // Align the rotation to match to the surface normal
            Quaternion targetRotation = Quaternion.FromToRotation(Utils.GetDirection(i, obj), hit.normal);
            obj.transform.rotation = targetRotation * obj.transform.rotation;
            // Move the object accordingly based on the ray direction
            obj.transform.position = hit.point + Utils.GetDirection(i, obj) * Utils.GetSize(i, bounds) / 2;
        }
        else
        {
            Debug.Log("No object was hit, place the object to 1 meter ahead of the ray direction.");
            obj.transform.position = ray.origin + ray.direction * 1;
        }
    }

    public Ray GetRayFromPixelCoor(int coorX, int coorY)
    {
        Vector3 camPosition = _capturePhoto.CamPosition;
        Quaternion camRotation = _capturePhoto.CamRotation;
        Matrix4x4 camProjectionMatrix = _capturePhoto.CamProjectionMatrix;
        if (camProjectionMatrix == Matrix4x4.zero)
        {
            Debug.Log("Camera projection matrix is not set.");
            camPosition = Camera.main.transform.position;
            camRotation = Camera.main.transform.rotation;
            camProjectionMatrix = Camera.main.projectionMatrix;
        }
        Ray ray;
        if (_webCamTextureManager.WebCamTexture == null)
        {
            Debug.Log("Webcam texture is not set.");
            ray = new Ray(camPosition, Vector3.forward);
        }
        else
        {
            // Note that the Y coordinate is flipped to use Meta's passthrough camera coordinate system
            coorY = _webCamTextureManager.WebCamTexture.height - coorY;
            var rayInCamera = PassthroughCameraUtils.ScreenPointToRayInCamera(_cameraEye, new Vector2Int(coorX, coorY));
            var rayDirectionInWorld = camRotation * rayInCamera.direction;
            ray = new Ray(camPosition, rayDirectionInWorld);
        }
        // Visualize the ray using line renderer
        StartCoroutine(VisualizeRay(ray));
        return ray;
    }

    IEnumerator VisualizeRay(Ray ray)
    {
        // Visualize the ray using line renderer
        GameObject rayVisualizer = RayVisualizer.Create(ray, 10f, _rayMaterial, _rayWidth);
        yield return new WaitForSeconds(_rayDuration);
        // Destroy the line renderer after a certain duration
        Destroy(rayVisualizer);
    }

    public GameObject CreateObjPrimitive(string type)
    {
        GameObject instance = null;
        switch (type.ToLower())
        {
            case "cube":
                instance = GameObject.CreatePrimitive(PrimitiveType.Cube);
                break;
            case "sphere":
                instance = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                break;
            case "cylinder":
                instance = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                break;
            case "capsule":
                instance = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                break;
            case "plane":
                instance = GameObject.CreatePrimitive(PrimitiveType.Plane);
                break;
            case "quad":
                instance = GameObject.CreatePrimitive(PrimitiveType.Quad);
                break;
            case "empty":
                instance = new GameObject();
                break;
            default:
                Debug.LogWarning("Invalid primitive type.");
                break;
        }
        return instance;
    }

    // destroy previous objects
    public void DestroyAllCreatedObjects()
    {
        foreach (GameObject obj in _createdObjects)
        {
            Destroy(obj);
        }
        _createdObjects.Clear();
    }
}
