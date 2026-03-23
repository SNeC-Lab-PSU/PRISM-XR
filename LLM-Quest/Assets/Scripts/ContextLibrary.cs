using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

public class ContextLibrary : MonoBehaviour
{
    [SerializeField]
    LayerMask _envObjLayers;
    [SerializeField]
    float _raycastRadius = 5.0f;

    // associated with the object creation resources
    List<string> prefabNames;
    List<Vector3> prefabBoundSizes;
    WebSocketClient _webSocketClient;

    private void Awake()
    {
        LoadEnvResources();
    }

    private void Start()
    {
        _webSocketClient = GetComponent<WebSocketClient>();
    }

    #region Local resources for object creation
    private void LoadEnvResources()
    {
        prefabNames = new List<string>();
        prefabBoundSizes = new List<Vector3>();

        // Add Unity primitives
        AddPrimitiveInfo("Cube", PrimitiveType.Cube);
        AddPrimitiveInfo("Sphere", PrimitiveType.Sphere);
        AddPrimitiveInfo("Cylinder", PrimitiveType.Cylinder);
        AddPrimitiveInfo("Capsule", PrimitiveType.Capsule);
        AddPrimitiveInfo("Quad", PrimitiveType.Quad);

        // using resources.load to load prefabNames.txt and add local prefabs
        TextAsset textAsset = Resources.Load<TextAsset>("prefabNames");
        if (textAsset == null)
        {
            Debug.LogWarning("Prefab names file not found.");
            return;
        }

        // Split the text into lines
        string[] lines = textAsset.text.Split(new[] { "\r\n", "\r", "\n" }, System.StringSplitOptions.None);
        foreach (string line in lines)
        {
            if (line.Trim() == "") continue;
            prefabNames.Add(line);
            GameObject prefab = Resources.Load<GameObject>(line);
            if (!prefab)
            {
                Debug.LogWarning("Prefab not found: " + line);
            }
            else
            {
                // get the bound size of the prefab
                Bounds bounds = Utils.GetBoundObj(prefab);
                Vector3 boundSize = bounds.size;
                prefabBoundSizes.Add(boundSize);
            }
        }
        Debug.Log("All prefabs: \n" + GetAllPrefabNames());
    }

    void AddPrimitiveInfo(string name, PrimitiveType type)
    {
        GameObject primitive = GameObject.CreatePrimitive(type);
        primitive.SetActive(false); // Make it inactive to avoid rendering
        Bounds bounds = Utils.GetBoundObj(primitive);
        Vector3 boundSize = bounds.size;
        prefabNames.Add(name);
        prefabBoundSizes.Add(boundSize);
        GameObject.DestroyImmediate(primitive); // Clean up after getting bounds
    }

    public string GetAllPrefabNames()
    {
        // Using LINQ to combine names and sizes
        var formattedStrings = prefabBoundSizes.Zip(prefabNames, (size, name) => $"{name} ({size.x:F3},{size.y:F3},{size.z:F3})");
        return string.Join("\n", formattedStrings) + "\n";
    }

    #endregion

    #region Contextual information of existing virtual objects
    string objInfo(GameObject obj, bool reqPos, bool reqOri, bool reqScale, bool reqSize)
    {
        if (obj == null)
            return "";
        string objName = obj.name;
        string objPos = reqPos ? obj.transform.position.ToString() : "";
        string objOri = reqOri ? obj.transform.rotation.eulerAngles.ToString() : "";
        string objScale = reqScale ? obj.transform.localScale.ToString() : "";
        string objSize = reqSize ? Utils.GetBoundObj(obj).size.ToString() : "";
        return string.Join(" ", objName, objPos, objOri, objScale, objSize) + "\n";
    }

    string objInfoLocal(GameObject obj, bool reqPos, bool reqOri, bool reqScale, bool reqSize)
    {
        if (obj == null)
            return "";
        string objName = obj.name;
        string objPos = reqPos ? obj.transform.localPosition.ToString() : "";
        string objOri = reqOri ? obj.transform.localRotation.eulerAngles.ToString() : "";
        string objScale = reqScale ? obj.transform.localScale.ToString() : "";
        string objSize = reqSize ? Utils.GetBoundObj(obj).size.ToString() : "";
        return string.Join(" ", objName, objPos, objOri, objScale, objSize) + " (local)\n";
    }

    public string GetSceneData(Vector3 target, float radius, bool reqPos, bool reqOri, bool reqScale, bool reqSize)
    {
        string neighborList = "";
        HashSet<GameObject> uniqueObjects = new HashSet<GameObject>();
        Debug.Log("start collecting scene data.");
        // find virtual objects created in the scene with specified layers
        Collider[] collidersVirtual = Physics.OverlapSphere(target, radius, _envObjLayers);
        if (collidersVirtual.Length > 0)
        {
            // sort the collider based on the distance to the target
            Array.Sort(collidersVirtual, (x, y) => Vector3.Distance(x.transform.position, target).CompareTo(Vector3.Distance(y.transform.position, target)));
            neighborList += "The following are the contextual data associated to the virtual objects:\n";
        }
        foreach (Collider collider in collidersVirtual)
        {
            // find the root parent of the collider
            Transform root = collider.transform;
            // Climb up the hierarchy until no more parents in the object layers are found
            while (root.parent != null)
            {
                if (!Utils.IsLayerInLayerMask(root.parent.gameObject.layer, _envObjLayers))
                {
                    break;
                }
                root = root.parent;
            }
            if (uniqueObjects.Contains(root.gameObject)) continue;
            uniqueObjects.Add(root.gameObject);
            // TODO: indicate the parent structure within description
            // add all children of the root to the uniqueObjects set, including children of children, etc.
            Transform[] children = root.GetComponentsInChildren<Transform>();
            foreach (Transform child in children)
            {
                // filter those without collider or rigidbody, except for the root object
                if (child != root && (child.GetComponent<Collider>() == null && child.GetComponent<Rigidbody>() == null))
                {
                    continue;
                }
                neighborList += objInfo(child.gameObject, reqPos, reqOri, reqScale, reqSize);
            }
        }
        return neighborList;
    }

    #endregion

    #region Contextual information associated to the user
    public Vector3 GetUserPosition()
    {
        Vector3 pos = Camera.main.transform.position;
        return pos;
    }

    public string GetUserInfo(bool req, bool reqOri, bool reqScale, bool reqSize)
    {
        string userInfo = "";
        // center eye anchor
        Transform centerEyeAnchor = Camera.main.transform;
        userInfo += objInfo(centerEyeAnchor.gameObject, req, reqOri, reqScale, reqSize);
        return userInfo;
    }
    #endregion

    #region Context Management

    [Serializable]
    public class ContextCategory
    {
        [JsonProperty("position")]
        public bool Position { get; set; } = false;

        [JsonProperty("orientation")]
        public bool Orientation { get; set; } = false;

        [JsonProperty("scale")]
        public bool Scale { get; set; } = false;

        [JsonProperty("size")]
        public bool Size { get; set; } = false;

        [JsonProperty("animationData")]
        public bool AnimationData { get; set; } = false;

        [JsonProperty("user")]
        public bool User { get; set; } = false;

        [JsonProperty("whiteboard")]
        public bool Whiteboard { get; set; } = false;
    }

    public string GetContextData(string jsonString)
    {
        ContextCategory contextCategory;
        try
        {
            contextCategory = JsonConvert.DeserializeObject<ContextCategory>(jsonString);
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to parse JSON of context category: {ex.Message}");
            return "";
        }
        string contextData = "";
        bool reqPos = contextCategory.Position;
        bool reqOri = contextCategory.Orientation;
        bool reqScale = contextCategory.Scale;
        bool reqSize = contextCategory.Size;
        bool reqScene = true;
        bool reqAnimationData = contextCategory.AnimationData;
        bool reqUser = contextCategory.User;
        bool reqWhiteboard = contextCategory.Whiteboard;
        if (reqScene)
        {
            contextData += "The following is the contextual data associated to the scene, including the object name";
            contextData += reqPos ? ", position" : "";
            contextData += reqOri ? ", orientation" : "";
            contextData += reqScale ? ", scale" : "";
            contextData += reqSize ? ", size" : "";
            contextData += ".\n";
            contextData += GetSceneData(GetUserPosition(), _raycastRadius, reqPos, reqOri, reqScale, reqSize);
        }
        if (reqAnimationData && Utils.NumActiveAnimations() > 0)
        {
            // get animation data
            contextData += "The following are id of active animations in current scene.\n";
            contextData += Utils.GetActiveAnimation();
        }
        if (reqUser)
        {
            contextData += "The following are contexual data associtated to the player, including the object name";
            contextData += reqPos ? ", position" : "";
            contextData += reqOri ? ", orientation" : "";
            contextData += ".\n";
            contextData += GetUserInfo(reqPos, reqOri, false, false);
        }
        else
        {
            // at least provide the user position
            contextData += "The following is the position of the user:\n" + GetUserPosition().ToString() + "\n";
        }
        if (reqWhiteboard)
        {
            string imagePath = Path.Combine(Application.persistentDataPath, "whiteboard.png");
            Utils.SaveWhiteboardImg(imagePath, GetUserPosition());
            _webSocketClient.SendImageToServer(imagePath);
        }
        return contextData;
    }

    #endregion
}
