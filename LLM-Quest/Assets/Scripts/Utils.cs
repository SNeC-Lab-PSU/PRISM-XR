using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

public class Utils : MonoBehaviour
{
    // Provide the access key to use Porcupine, obtained from (https://console.picovoice.ai/)
    public static string PORCUPINE_ACCESS_KEY = "${YOUR_ACCESS_KEY_HERE}";

    #region Whiteboard Related Utilities
    // Get whiteboard object
    public static GameObject GetWhiteboard(Vector3 targetPos)
    {
        GameObject[] whiteboards = GameObject.FindGameObjectsWithTag("Whiteboard");
        if (whiteboards.Length == 0)
        {
            Debug.Log("No whiteboards found in the scene.");
            return null;
        }
        else if (whiteboards.Length == 1)
        {
            return whiteboards[0];
        }
        else
        {
            Debug.Log("Multiple whiteboards found in the scene.");
            // if there are multiple whiteboards, use the one closest to the target position
            GameObject whiteboard = whiteboards[0];
            float minDist = Vector3.Distance(targetPos, whiteboard.transform.position);
            foreach (GameObject wb in whiteboards)
            {
                float dist = Vector3.Distance(targetPos, wb.transform.position);
                if (dist < minDist)
                {
                    whiteboard = wb;
                    minDist = dist;
                }
            }
            return whiteboard;
        }
    }

    public static bool SaveWhiteboardImg(string imagePath, Vector3 targetPos)
    {
        // get whiteboard in the scene
        GameObject whiteboard = GetWhiteboard(targetPos);
        if (whiteboard == null) return false;
        Texture2D tex = whiteboard.GetComponent<Whiteboard>().texture;
        if (tex == null) return false;

        // save texture to file
        byte[] bytes = tex.EncodeToPNG();
        File.WriteAllBytes(imagePath, bytes);

        return true;
    }

    public static bool ClearWhiteboardImg(Vector3 targetPos)
    {
        // get whiteboard in the scene
        GameObject whiteboard = GetWhiteboard(targetPos);
        if (whiteboard == null)
        {
            Debug.Log("No whiteboard found.");
            return false;
        }
        Vector2 textureSize = whiteboard.GetComponent<Whiteboard>().textureSize;
        Destroy(whiteboard.GetComponent<Whiteboard>().texture);
        Texture2D texture = new Texture2D((int)textureSize.x, (int)textureSize.y);
        whiteboard.GetComponent<Whiteboard>().texture = texture;
        whiteboard.GetComponent<Renderer>().material.mainTexture = texture;
        return true;
    }
    #endregion

    // Helper function to get the direction vector of an object
    public static Vector3 GetDirection(int i, GameObject obj)
    {
        switch (i)
        {
            case 0: return obj.transform.forward;  // Forward
            case 1: return -obj.transform.forward; // Backward
            case 2: return obj.transform.right;    // Right
            case 3: return -obj.transform.right;   // Left
            case 4: return obj.transform.up;       // Up
            case 5: return -obj.transform.up;      // Down
            default: return Vector3.zero; // Default case (although this should never happen)
        }
    }

    // Helper function to get the size based on bounding box
    public static float GetSize(int i, Bounds bounds)
    {
        switch (i)
        {
            case 0:
            case 1: return bounds.size.z;
            case 2:
            case 3: return bounds.size.x;
            case 4:
            case 5: return bounds.size.y;
            default: return 0; // Default case (although this should never happen)
        }
    }

    // Get the bounding box of a GameObject
    public static Bounds GetBoundObj(GameObject obj)
    {
        Bounds bounds;
        if (obj.GetComponent<Renderer>() == null)
        {
            bounds = GetCombinedBoundingBox(obj);
        }
        else
            bounds = obj.GetComponent<Renderer>().bounds;
        return bounds;
    }

    static Bounds GetCombinedBoundingBox(GameObject obj)
    {
        Renderer[] renderers = obj.GetComponentsInChildren<Renderer>();

        if (renderers.Length == 0) return new Bounds(obj.transform.position, Vector3.zero); // No renderer found, create a centered bounds

        Bounds combinedBounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            combinedBounds.Encapsulate(renderers[i].bounds);
        }

        return combinedBounds;
    }

    // Parse a Vector3 from a string in the format "x y z"
    public static Vector3 ParseVector3(string s)
    {
        var parts = s.Split(' ');
        float x, y, z;
        if (parts.Length >= 3 && float.TryParse(parts[0], out x) && float.TryParse(parts[1], out y) && float.TryParse(parts[2], out z))
        {
            return new Vector3(x, y, z);
        }
        else
        {
            Debug.LogWarning("Failed to parse Vector3: " + s);
            return Vector3.zero;
        }
    }

    // Find a GameObject by name
    public static GameObject GetGameObject(string name)
    {
        GameObject[] namedObjects = FindObjectsOfType<GameObject>()
                    .Where(obj => obj.name == name).ToArray();
        if (namedObjects.Length == 0) return null;
        else if (namedObjects.Length == 1) return namedObjects[0];
        else
        {
            Debug.Log("Multiple objects found with name: " + name);
            // return objects closest to the main camera
            Vector3 userPos = Camera.main.transform.position;
            GameObject closestObj = namedObjects[0];
            float minDist = Vector3.Distance(userPos, closestObj.transform.position);
            foreach (GameObject obj in namedObjects)
            {
                float dist = Vector3.Distance(userPos, obj.transform.position);
                if (dist < minDist)
                {
                    closestObj = obj;
                    minDist = dist;
                }
            }
            return closestObj;
        }
    }


    // For a given pixel coordinate, return a ray in world space
    // The pixel coordinate is assumed to be in the range [0, imageWidth] x [0, imageHeight], where (0, 0) is the top-left corner of the screen
    public static Ray GetRayFromPixel(
        Vector3 cameraPosition,      // World position of the camera
        Quaternion cameraRotation,  // World rotation of the camera
        Matrix4x4 projectionMatrix, // Camera projection matrix
        int imageWidth, int imageHeight,
        int pixelX, int pixelY)
    {
        // Step 1: Convert pixel to Normalized Device Coordinates (NDC), range from (-1, -1) at the bottom-left of the screen to (1, 1) at the top-right. 
        float xNDC = (pixelX / (float)imageWidth) * 2f - 1f;
        float yNDC = 1f - (pixelY / (float)imageHeight) * 2f;

        // Step 2: Unproject to Camera Space
        Vector4 ndc = new Vector4(xNDC, yNDC, 1f, 1f); // Assume z=1 for direction
        Matrix4x4 inverseProjectionMatrix = projectionMatrix.inverse;
        Vector4 cameraSpace = inverseProjectionMatrix * ndc;
        cameraSpace /= cameraSpace.w; // Normalize


        // Flip the z-axis for Unity's coordinate system
        cameraSpace.z = -cameraSpace.z;

        // Step 3: Transform to World Space
        Vector3 origin = cameraPosition;
        Vector3 direction = cameraRotation * new Vector3(cameraSpace.x, cameraSpace.y, cameraSpace.z);
        if (direction == Vector3.zero)
        {
            Debug.LogError("Invalid ray: Direction cannot be a zero vector.");
            return new Ray(origin, cameraRotation * Vector3.forward);
        }
        direction.Normalize(); // Normalize direction

        // Step 4: Return the ray
        return new Ray(origin, direction);
    }

    public static Ray GetRayFromPixel(
    Matrix4x4 cameraToWorldMatrix, // Camera-to-world transformation matrix
    Matrix4x4 projectionMatrix,    // Camera projection matrix
    int imageWidth, int imageHeight,
    int pixelX, int pixelY)
    {
        // Step 1: Convert pixel to Normalized Device Coordinates (NDC)
        float xNDC = (pixelX / (float)imageWidth) * 2f - 1f;
        float yNDC = 1f - (pixelY / (float)imageHeight) * 2f;

        // Step 2: Unproject to Camera Space
        Vector4 ndc = new Vector4(xNDC, yNDC, 1f, 1f); // Assume z=1 for direction
        Matrix4x4 inverseProjectionMatrix = projectionMatrix.inverse;
        Vector4 cameraSpace = inverseProjectionMatrix * ndc;
        cameraSpace /= cameraSpace.w; // Normalize

        // Flip the z-axis for Unity's coordinate system
        cameraSpace.z = -cameraSpace.z;

        // Step 3: Transform to World Space using cameraToWorldMatrix
        Vector3 origin = cameraToWorldMatrix.GetColumn(3); // Extract position from cameraToWorldMatrix
        Quaternion cameraRotation = cameraToWorldMatrix.rotation;
        Vector3 direction = cameraRotation * new Vector3(cameraSpace.x, cameraSpace.y, cameraSpace.z);
        if (direction == Vector3.zero)
        {
            Debug.LogError("Invalid ray: Direction cannot be a zero vector.");
            return new Ray(origin, cameraRotation * Vector3.forward);
        }
        direction.Normalize(); // Normalize direction
        // Step 4: Return the ray
        return new Ray(origin, direction);
    }

    public static Texture2D LoadImgAsTexture(string filePath)
    {
        byte[] imageData = File.ReadAllBytes(filePath);
        Texture2D textureData = new Texture2D(2, 2); // Dummy size, real size will be loaded
        if (textureData.LoadImage(imageData)) // Load image data into the texture
        {
            Debug.Log($"Image Resolution: {textureData.width} x {textureData.height}");
        }
        else
        {
            Debug.LogError("Failed to load image.");
        }
        return textureData;
    }

    public static bool IsLayerInLayerMask(int layerIndex, LayerMask layerMask)
    {
        return (layerMask.value & (1 << layerIndex)) != 0;
    }

    #region Animation Related Utilities
    static Dictionary<string, bool> _animationControl = new Dictionary<string, bool>();
    public static Dictionary<Transform, Transform> PrevParentsBeforeAttach = new Dictionary<Transform, Transform>();

    public static int NumActiveAnimations()
    {
        return _animationControl.Count;
    }

    public static string GetActiveAnimation()
    {
        return string.Join(",", _animationControl.Keys) + "\n";
    }

    public static bool GetAnimation(string name)
    {
        _animationControl.TryGetValue(name, out bool running);
        return running;
    }

    public static void IndicateAnimationStop(string name)
    {
        _animationControl[name] = false;
    }

    public static void SetLayerRecursively(GameObject obj, int newLayer)
    {
        if (obj == null) return;

        // Set the layer of the current object
        obj.layer = newLayer;

        // Recursively set the layer of all children
        foreach (Transform child in obj.transform)
        {
            SetLayerRecursively(child.gameObject, newLayer);
        }
    }

    public static void AddAnimation(string name)
    {
        _animationControl[name] = true;
    }

    public static void RemoveAllAnimations()
    {
        if (_animationControl.Count > 0)
            Debug.Log("Remaining animations: " + string.Join(",", _animationControl.Keys));
        _animationControl.Clear();
    }

    public static void RemoveAnimationFromList(string name)
    {
        if (_animationControl.ContainsKey(name))
        {
            if (name.Length > 0)
                Debug.Log("Removing animation: " + name);
            _animationControl.Remove(name);
        }
    }
    #endregion
}
