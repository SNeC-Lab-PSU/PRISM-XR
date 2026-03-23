using Meta.XR;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Oculus.Interaction.HandGrab;
using Oculus.Interaction;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Text;
using UnityEngine;
using Meta.XR.MultiplayerBlocks.Shared;

public class AnimationLibrary : MonoBehaviour
{
    [SerializeField]
    private EnvironmentRaycastManager _raycastManager;
    [SerializeField]
    private GameObject _grabblePrefab;

    private ConcurrentQueue<Tuple<AnimationData, bool>> _animationPool;
    private WebSocketClient _webSocketClient;
    private ObjectCreator _objectCreator;
    private SyncManager _syncManager;

    void Awake()
    {
        _animationPool = new ConcurrentQueue<Tuple<AnimationData, bool>>();
    }

    private void Start()
    {
        _webSocketClient = GetComponent<WebSocketClient>();
        _objectCreator = GetComponent<ObjectCreator>();
        _syncManager = GetComponent<SyncManager>();
        StartCoroutine(ExecuteAnimation());
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum AnimationActionTypeEnum
    {
        attach,
        detach,
        scale,
        color,
        movetowards,
        rotatetowards,
        looktowards,
        selfrotate,
        orbit,
        gazing,
        stop,
        remove,
        grabbable
    }

    [Serializable]
    public class AnimationData
    {
        [JsonProperty("actionType")]
        public AnimationActionTypeEnum ActionType { get; set; }

        [JsonProperty("objectName")]
        public string ObjectName { get; set; }

        [JsonProperty("animationID")]
        public string AnimationID { get; set; }

        [JsonProperty("newobjectname")]
        public string NewObjectName { get; set; }

        [JsonProperty("duration")]
        public float? Duration { get; set; }

        [JsonProperty("target")]
        public string Target { get; set; }

        [JsonProperty("scale")]
        public float[] Scale { get; set; }

        [JsonProperty("color")]
        public float[] Color { get; set; }

        [JsonProperty("coordinateSpace")]
        public ObjectCreator.CoordinateSpaceEnum? CoordinateSpace { get; set; }

        [JsonProperty("centerCoordinates")]
        public int[] CenterCoordinates { get; set; }

        [JsonProperty("position")]
        public float[] Position { get; set; }

        [JsonProperty("localposition")]
        public float[] LocalPosition { get; set; }

        [JsonProperty("localdirection")]
        public float[] LocalDirection { get; set; }

        [JsonProperty("distance")]
        public float? Distance { get; set; }

        [JsonProperty("safebound")]
        public float? SafeBound { get; set; }

        [JsonProperty("orientation")]
        public float[] Orientation { get; set; }

        [JsonProperty("axis")]
        public float[] Axis { get; set; }

        [JsonProperty("speedRot")]
        public float? SpeedRot { get; set; }

        [JsonProperty("speedMov")]
        public float? SpeedMov { get; set; }
    }

    private LineRenderer SetupLineRenderer(GameObject obj)
    {
        LineRenderer lineRenderer = obj.GetComponent<LineRenderer>();
        if (lineRenderer == null)
        {
            lineRenderer = obj.AddComponent<LineRenderer>();
        }
        // set the width scalable to object's size
        Bounds bound = Utils.GetBoundObj(obj);
        float maxBound = Mathf.Max(bound.extents.x, bound.extents.y, bound.extents.z);
        lineRenderer.startWidth = maxBound * 0.1f;
        lineRenderer.endWidth = maxBound * 0.1f;
        lineRenderer.positionCount = 0;
        lineRenderer.material = Resources.Load<Material>("DottedLine");
        // adapt the texture scale to line width
        lineRenderer.material.mainTextureScale = new Vector2(10, 1f);
        lineRenderer.textureMode = LineTextureMode.Tile;
        return lineRenderer;
    }

    public (LineRenderer, Vector3) DrawTrajectoryOrbit(GameObject obj, GameObject target, float radius, int resolution = 100)
    {
        // calculate the normal vector of the plane between the object and the target
        Vector3 auxiliaryVector = Vector3.forward;
        Vector3 directionToTarget = target.transform.position - obj.transform.position;
        if (Mathf.Abs(Vector3.Dot(directionToTarget.normalized, Vector3.forward)) > 0.999)
        {
            auxiliaryVector = Vector3.right;  // Change auxiliary vector if needed
        }
        Vector3 normal = Vector3.Cross(directionToTarget, auxiliaryVector);
        // make the orbiting always clockwise
        if (Vector3.Dot(normal, Vector3.up) < 0)
            normal = -normal;

        LineRenderer lineRenderer = SetupLineRenderer(obj);
        Vector3 center = target.transform.position;
        Vector3[] points = new Vector3[resolution + 1];

        for (int i = 0; i <= resolution; i++)
        {
            float angle = 360f / resolution * i; // Angle in degrees
            Quaternion rotation = Quaternion.AngleAxis(angle, normal);
            Vector3 orbitalPoint = center + rotation * (auxiliaryVector * radius);
            points[i] = orbitalPoint;
        }

        lineRenderer.positionCount = points.Length;
        lineRenderer.SetPositions(points);
        return (lineRenderer, normal);
    }

    public void AddToAnimationPool(AnimationData data, bool synced = false)
    {
        if (data != null)
            _animationPool.Enqueue(new Tuple<AnimationData, bool>(data, synced));
    }

    public IEnumerator ExecuteAnimation()
    {
        while (true)
        {
            if (_animationPool.TryDequeue(out Tuple<AnimationData, bool> animationItem))
            {
                AnimationData jdata = animationItem.Item1;
                bool synced = animationItem.Item2;
                AnimationActionTypeEnum action = jdata.ActionType;
                string objName = jdata.ObjectName;
                GameObject obj = Utils.GetGameObject(objName);
                if (obj == null)
                {
                    Debug.LogWarning("Cannot find the object: " + objName);
                    continue;
                }
                string aniName = jdata.AnimationID;
                string newObjectName = jdata.NewObjectName;
                if (newObjectName != null)
                    obj.name = newObjectName;
                Vector3 targetPosition = GetPositionFromAnimationData(jdata, obj);
                if (!synced)
                {
                    // forward the animation to the other clients
                    SendAnimationToServer(jdata, targetPosition);
                }
                switch (action)
                {
                    case AnimationActionTypeEnum.attach:
                        string newparentName = jdata.Target;
                        GameObject newparent = Utils.GetGameObject(newparentName);
                        AttachObject(obj, newparent);
                        break;
                    case AnimationActionTypeEnum.detach:
                        DetachObject(obj);
                        break;
                    case AnimationActionTypeEnum.scale:
                        if (jdata.Scale == null || jdata.Scale.Length != 3)
                        {
                            Debug.LogError("Cannot find the scale for scaling: " + objName);
                            break;
                        }
                        Vector3 scale = new Vector3(jdata.Scale[0], jdata.Scale[1], jdata.Scale[2]);
                        float scaleTime = jdata.Duration.HasValue ? jdata.Duration.Value : 1f; // default 1 second
                        //Debug.Log("obj name: "+objName+" scale: "+scale+" time: "+scaleTime);
                        yield return StartCoroutine(ScaleOverTime(obj, scale, scaleTime, aniName));
                        break;
                    case AnimationActionTypeEnum.color:
                        if (jdata.Color == null || jdata.Color.Length != 3)
                        {
                            Debug.LogError("Cannot find the color for coloring: " + objName);
                            break;
                        }
                        Vector3 vColor = new Vector3(jdata.Color[0], jdata.Color[1], jdata.Color[2]);
                        Color color = new Color(vColor.x, vColor.y, vColor.z);
                        float colorTime = jdata.Duration.HasValue ? jdata.Duration.Value : 1f; // default 1 second
                        //Debug.Log("obj name: "+objName+" color: "+color+" time: "+colorTime);
                        if (obj.GetComponent<Renderer>() == null)
                        {
                            Debug.LogWarning("Cannot find the renderer component for the object: " + objName);
                            break;
                        }
                        Material material = obj.GetComponent<Renderer>().material;
                        yield return StartCoroutine(FadeColor(material, color, colorTime, aniName));
                        break;
                    case AnimationActionTypeEnum.movetowards:
                        float moveTowardsSpeed = jdata.SpeedMov.HasValue ? jdata.SpeedMov.Value : 1; // default 1 meter per second
                        //Debug.Log("obj name: "+objName+" position: "+position+" time: "+moveTime);
                        yield return StartCoroutine(MoveObjectTowards(obj, targetPosition, moveTowardsSpeed, aniName));
                        break;
                    case AnimationActionTypeEnum.rotatetowards:
                        if (jdata.Orientation == null || jdata.Orientation.Length != 4)
                        {
                            Debug.LogWarning("Cannot find the orientation for rotating: " + objName);
                            break;
                        }
                        Quaternion quaternion = new Quaternion(jdata.Orientation[0], jdata.Orientation[1], jdata.Orientation[2], jdata.Orientation[3]);
                        Vector3 orientation = quaternion.eulerAngles;
                        float rotateTowardsSpeed = jdata.SpeedRot.HasValue ? jdata.SpeedRot.Value : 90; // default 90 degrees per second
                        //Debug.Log("obj name: "+objName+" orientation: "+orientation+" time: "+rotateTime);
                        yield return StartCoroutine(RotateObjectTowards(obj, orientation, rotateTowardsSpeed, aniName));
                        break;
                    case AnimationActionTypeEnum.looktowards:
                        float lookSpeed = jdata.SpeedRot.HasValue ? jdata.SpeedRot.Value : 90; // default 90 degrees per second
                        //Debug.Log("obj name: "+objName+" target: "+targetName+" rotateSpeed: "+rotateSpeed);
                        yield return StartCoroutine(LookAtTarget(obj, targetPosition, lookSpeed, aniName));
                        break;
                    case AnimationActionTypeEnum.selfrotate:
                        Vector3 axis = Vector3.up;
                        if (jdata.Axis != null && jdata.Axis.Length == 3)
                        {
                            Vector3 newAxis = new Vector3(jdata.Axis[0], jdata.Axis[1], jdata.Axis[2]);
                            if (newAxis.magnitude > 0)
                                axis = newAxis;
                        }
                        float speed = jdata.SpeedRot.HasValue ? jdata.SpeedRot.Value : 20; // default 20 degrees per second
                        float rotatePersistTime = jdata.Duration.HasValue ? jdata.Duration.Value : -1; // default -1, infinite time
                        //Debug.Log("obj name: "+objName+" axis: "+axis+" speed: "+speed);
                        StartCoroutine(RotateObjectAxis(obj, axis, speed, rotatePersistTime, aniName));
                        break;
                    case AnimationActionTypeEnum.orbit:
                        string targetOrbitName = jdata.Target;
                        GameObject targetOrbit = Utils.GetGameObject(targetOrbitName);
                        if (targetOrbit == null)
                        {
                            Debug.LogWarning("Cannot find the orbit target object: " + targetOrbitName);
                            break;
                        }
                        float orbitSpeed = jdata.SpeedRot.HasValue ? jdata.SpeedRot.Value : 20; // default 20 degrees per second
                        float orbitPersistTime = jdata.Duration.HasValue ? jdata.Duration.Value : -1; // default -1, infinite time
                        StartCoroutine(OrbitObject(obj, targetOrbit, orbitSpeed, orbitPersistTime, aniName));
                        break;
                    case AnimationActionTypeEnum.gazing:
                        string targetName = jdata.Target;
                        GameObject target = Utils.GetGameObject(targetName);
                        if (target == null)
                        {
                            Debug.LogWarning("Cannot find the gazing target object: " + targetName);
                            break;
                        }
                        float gazePersistTime = jdata.Duration.HasValue ? jdata.Duration.Value : -1; // default -1, infinite time
                        StartCoroutine(Gazing(obj, target, gazePersistTime, aniName));
                        break;
                    case AnimationActionTypeEnum.stop:
                        StopAnimation(aniName);
                        break;
                    case AnimationActionTypeEnum.remove:
                        StartCoroutine(RemoveObject(obj));
                        break;
                    case AnimationActionTypeEnum.grabbable:
                        MakeGrabbable(obj, synced);
                        break;
                    default:
                        Debug.Log("Invalid animation type.");
                        break;
                }
            }
            yield return null;
        }
    }

    Vector3 GetPositionFromAnimationData(AnimationData data, GameObject obj)
    {
        Vector3 position = Vector3.zero;

        GameObject targetMove = null;
        if (data.Target != null)
        {
            string targetMoveName = data.Target;
            targetMove = Utils.GetGameObject(targetMoveName);
            if (targetMove != null)
            {
                if (data.LocalPosition != null && data.LocalPosition.Length == 3)
                {
                    Vector3 localposition = new Vector3(data.LocalPosition[0], data.LocalPosition[1], data.LocalPosition[2]);
                    position = targetMove.transform.TransformPoint(localposition);
                }
                else if (data.LocalDirection != null && data.LocalDirection.Length == 3)
                {
                    Vector3 localdirection = new Vector3(data.LocalDirection[0], data.LocalDirection[1], data.LocalDirection[2]);
                    float distance = data.Distance.HasValue ? data.Distance.Value : 1; // default 1 meter
                    position = obj.transform.position + targetMove.transform.TransformDirection(localdirection) * distance;
                }
                else
                    position = targetMove.transform.position;
            }
        }
        else
        {
            if (data.CoordinateSpace.HasValue && data.CoordinateSpace.Value == ObjectCreator.CoordinateSpaceEnum.pixel)
            {
                // Get the ray pointing from camera to the pixel coordinates in 3D world
                Ray ray = _objectCreator.GetRayFromPixelCoor(data.CenterCoordinates[0], data.CenterCoordinates[1]);
                // Send a raycast to detect the surface
                if (_raycastManager.Raycast(ray, out var hit))
                {
                    position = hit.point;
                }
                else
                {
                    position = ray.origin + ray.direction * 1;
                }
            }
            else if (data.CoordinateSpace.HasValue && data.CoordinateSpace.Value == ObjectCreator.CoordinateSpaceEnum.world)
                position = new Vector3(data.Position[0], data.Position[1], data.Position[2]);
            else if (data.LocalPosition != null && data.LocalPosition.Length == 3)
            {
                Vector3 localposition = new Vector3(data.LocalPosition[0], data.LocalPosition[1], data.LocalPosition[2]);
                position = obj.transform.TransformPoint(localposition);
            }
            else if (data.LocalDirection != null && data.LocalDirection.Length == 3)
            {
                Vector3 localdirection = new Vector3(data.LocalDirection[0], data.LocalDirection[1], data.LocalDirection[2]);
                float distance = data.Distance.HasValue ? data.Distance.Value : 1; // default 1 meter
                position = obj.transform.position + obj.transform.TransformDirection(localdirection) * distance;
            }
        }

        if (data.SafeBound.HasValue)
        {
            // calculate the position with safe distance
            float safebound = data.SafeBound.Value;
            if (safebound < 0 && targetMove != null)
                safebound = Utils.GetBoundObj(targetMove).extents.magnitude;
            position = position + (obj.transform.position - position).normalized * safebound;
        }
        return position;
    }

    void SendAnimationToServer(AnimationData data, Vector3 worldPosition)
    {
        string jsonData = JsonConvert.SerializeObject(data);
        // Update the coordinate space to world space for certain actions
        if (data.ActionType == AnimationActionTypeEnum.movetowards || data.ActionType == AnimationActionTypeEnum.looktowards)
        {
            AnimationData newData = JsonConvert.DeserializeObject<AnimationData>(jsonData);

            newData.CoordinateSpace = ObjectCreator.CoordinateSpaceEnum.world;
            newData.Position = new float[] { worldPosition.x, worldPosition.y, worldPosition.z };
            // Clear safe bound as it has already been applied
            if (data.SafeBound.HasValue)
                newData.SafeBound = null;
            jsonData = JsonConvert.SerializeObject(newData);
        }
        // send the animation data to the server
        Debug.Log("Send animation data to server: " + jsonData);
        _webSocketClient.SendSpecialTypeToServer("animation", Encoding.UTF8.GetBytes(jsonData));
    }

    // move to a specific position
    public IEnumerator MoveObjectTowards(GameObject obj, Vector3 target, float speed, string animationName)
    {
        Utils.AddAnimation(animationName);
        // Store the original position
        Vector3 originalPosition = obj.transform.position;

        // draw the preview trajectory line
        LineRenderer lineRenderer = SetupLineRenderer(obj);
        lineRenderer.positionCount = 2;
        lineRenderer.SetPosition(0, originalPosition);
        lineRenderer.SetPosition(1, target);

        float time = 0;
        float duration = Vector3.Distance(originalPosition, target) / speed;

        while (time < duration && Utils.GetAnimation(animationName))
        {
            // Stop the coroutine if the object is destroyed
            if (obj == null)
                break;
            // Increment time by the time elapsed since last frame
            time += Time.deltaTime;
            // Calculate the lerp factor, 0 means original position, 1 means target position
            float lerpFactor = time / duration;

            // Update the object's position smoothly from original position to target position
            obj.transform.position = Vector3.Lerp(originalPosition, target, lerpFactor);

            // Yield execution until the next frame
            yield return null;
        }

        if (Utils.GetAnimation(animationName) && obj != null)
            // Ensure the final position is set exactly to the target position
            obj.transform.position = target;

        // destroy the preview trajectory line
        Destroy(lineRenderer);
        Utils.RemoveAnimationFromList(animationName);
    }

    public IEnumerator RotateObjectTowards(GameObject obj, Vector3 target, float speed, string animationName)
    {
        Utils.AddAnimation(animationName);
        // rotate object to a specific angle (Euler angles)
        // Store the original rotation
        Quaternion originalRotation = obj.transform.rotation;
        float time = 0;

        float duration = Quaternion.Angle(originalRotation, Quaternion.Euler(target)) / speed;

        while (time < duration && Utils.GetAnimation(animationName))
        {
            // Stop the coroutine if the object is destroyed
            if (obj == null)
                break;
            // Increment time by the time elapsed since last frame
            time += Time.deltaTime;
            // Calculate the lerp factor, 0 means original rotation, 1 means target rotation
            float lerpFactor = time / duration;

            // Update the object's rotation smoothly from original rotation to target rotation
            obj.transform.rotation = Quaternion.Lerp(originalRotation, Quaternion.Euler(target), lerpFactor);

            // Yield execution until the next frame
            yield return null;
        }

        if (Utils.GetAnimation(animationName) && obj != null)
            // Ensure the final rotation is set exactly to the target rotation, when running normally to end
            obj.transform.rotation = Quaternion.Euler(target);

        Utils.RemoveAnimationFromList(animationName);
    }

    public IEnumerator RotateObjectAxis(GameObject obj, Vector3 axis, float speed, float time, string animationName)
    {
        Utils.AddAnimation(animationName);

        float duration = 0;
        while ((time < 0 || duration < time) && Utils.GetAnimation(animationName))
        {
            // Stop the coroutine if the object is destroyed
            if (obj == null)
                break;
            // Update the object's rotation for specified axis and speed
            obj.transform.Rotate(axis, speed * Time.deltaTime);

            // Yield execution until the next frame
            yield return null;
            duration += Time.deltaTime;
        }
        Utils.RemoveAnimationFromList(animationName);
    }

    public IEnumerator OrbitObject(GameObject obj, GameObject target, float speed, float time, string animationName)
    {
        // if the object has rigidbody, set it to kinematic
        if (obj.GetComponent<Rigidbody>() != null)
            obj.GetComponent<Rigidbody>().isKinematic = true;

        // draw the preview trajectory line for orbiting
        float radius = Vector3.Distance(obj.transform.position, target.transform.position);

        // get line renderer and normal vector of the plane
        (LineRenderer lineRenderer, Vector3 normal) = DrawTrajectoryOrbit(obj, target, radius);

        // record the original position of the target
        Vector3 originalTargetPosition = target.transform.position;
        // record the original scale of the object
        Vector3 originalScale = obj.transform.localScale;

        Utils.AddAnimation(animationName);

        // rotate object around a specific target
        float duration = 0;
        float start_time = Time.time;
        while (Utils.GetAnimation(animationName) && (time < 0 || duration < time))
        {
            // Stop the coroutine if the object or the target is destroyed
            if (obj == null || target == null)
                break;
            // update the trajectory once the target is moving
            if (Vector3.Distance(originalTargetPosition, target.transform.position) > 0.01f || obj.transform.localScale != originalScale)
            {
                // move the object towards the same direction as the target
                Vector3 offset = target.transform.position - originalTargetPosition;
                obj.transform.position += offset;
                (lineRenderer, normal) = DrawTrajectoryOrbit(obj, target, radius);
                originalTargetPosition = target.transform.position;
                originalScale = obj.transform.localScale;
            }
            obj.transform.RotateAround(target.transform.position, normal, speed * Time.deltaTime);
            // Yield execution until the next frame
            yield return null;
            duration = Time.time - start_time;
        }

        // destroy the preview trajectory line
        Destroy(lineRenderer);
        Utils.RemoveAnimationFromList(animationName);
    }

    public IEnumerator EmitDottedLine(GameObject obj, Vector3 targetPosition)
    {
        LineRenderer lineRenderer = SetupLineRenderer(obj);
        lineRenderer.positionCount = 2;
        lineRenderer.SetPosition(0, obj.transform.position);
        lineRenderer.SetPosition(1, targetPosition);
        // let the line disappear after 3 seconds
        yield return new WaitForSeconds(3);
        Destroy(lineRenderer);
    }

    public IEnumerator LookAtTarget(GameObject obj, Vector3 targetPosition, float speed, string animationName)
    {
        // create a dotted line to hint the user
        StartCoroutine(EmitDottedLine(obj, targetPosition));

        // rotate object to look at a specific target (position)
        // Store the original rotation
        Quaternion originalRotation = obj.transform.rotation;
        float time = 0;

        Vector3 directionToTarget = targetPosition - transform.position;
        directionToTarget.y = 0; // Keep rotation only on the Y axis, i.e., without tiling the head

        Quaternion targetRotation = Quaternion.LookRotation(directionToTarget);

        float duration = Quaternion.Angle(originalRotation, targetRotation) / speed;

        Utils.AddAnimation(animationName);

        while (time < duration && Utils.GetAnimation(animationName))
        {
            // Stop the coroutine if the object is destroyed
            if (obj == null)
                break;
            // Increment time by the time elapsed since last frame
            time += Time.deltaTime;
            // Calculate the lerp factor, 0 means original rotation, 1 means target rotation
            float lerpFactor = time / duration;

            // Update the object's rotation smoothly from original rotation to target rotation
            obj.transform.rotation = Quaternion.Lerp(originalRotation, targetRotation, lerpFactor);

            // Yield execution until the next frame
            yield return null;
        }

        if (Utils.GetAnimation(animationName) && obj != null)
            // Ensure the final rotation is set exactly to the target rotation
            obj.transform.rotation = targetRotation;

        Utils.RemoveAnimationFromList(animationName);
    }

    public IEnumerator Gazing(GameObject obj, GameObject target, float time, string animationName)
    {
        Utils.AddAnimation(animationName);
        float duration = 0;
        float start_time = Time.time;
        while ((time < 0 || duration < time) && Utils.GetAnimation(animationName))
        {
            // Stop the coroutine if the object or the target is destroyed
            if (obj == null || target == null)
                break;
            yield return StartCoroutine(LookAtTarget(obj, target.transform.position, 90, ""));
            // leave one frame to avoid the flickering
            yield return null;
            duration = Time.time - start_time;
        }
        Utils.RemoveAnimationFromList(animationName);
    }

    public IEnumerator ScaleOverTime(GameObject obj, Vector3 target, float duration, string animationName)
    {
        Utils.AddAnimation(animationName);
        // Store the original scale
        Vector3 originalScale = obj.transform.localScale;
        float time = 0;

        while (time < duration && Utils.GetAnimation(animationName))
        {
            // Stop the coroutine if the object is destroyed
            if (obj == null)
                break;
            // Increment time by the time elapsed since last frame
            time += Time.deltaTime;
            // Calculate the lerp factor, 0 means original scale, 1 means target scale
            float lerpFactor = time / duration;

            // Update the object's scale smoothly from original scale to target scale
            obj.transform.localScale = Vector3.Lerp(originalScale, target, lerpFactor);

            // Yield execution until the next frame
            yield return null;
        }

        if (Utils.GetAnimation(animationName) && obj != null)
            // Ensure the final scale is set exactly to the target scale
            obj.transform.localScale = target;

        Utils.RemoveAnimationFromList(animationName);
    }

    IEnumerator FadeColor(Material material, Color endColor, float duration, string animationName)
    {
        Utils.AddAnimation(animationName);
        Color startColor = material.color;
        float elapsed = 0;
        while (Utils.GetAnimation(animationName) && elapsed < duration)
        {
            // Stop the coroutine if the material is destroyed
            if (material == null)
                break;
            material.color = Color.Lerp(startColor, endColor, elapsed / duration);
            elapsed += Time.deltaTime;
            yield return null;
        }
        if (Utils.GetAnimation(animationName) && material != null)
            material.color = endColor;

        Utils.RemoveAnimationFromList(animationName);
    }

    public void AttachObject(GameObject target, GameObject newParent)
    {
        if (target == null || newParent == null)
        {
            Debug.LogWarning("cannot find some objects for attach animation!");
            return;
        }
        Transform targetTransform = target.transform;
        Transform parentTransform = newParent.transform;
        // store the previous parent before pick
        Utils.PrevParentsBeforeAttach[targetTransform] = targetTransform.parent;
        targetTransform.SetParent(parentTransform);
        // if having rigidbody, set it to kinematic
        if (targetTransform.GetComponent<Rigidbody>() != null)
            targetTransform.GetComponent<Rigidbody>().isKinematic = true;
        // adjust the local position and rotation to emulate the grab action
        Vector3 localPose = Vector3.zero;
        Bounds bounds = Utils.GetBoundObj(target);
        localPose.x = -bounds.extents.x;
        // TODO: consider using animations to finish the adjustment
        targetTransform.localPosition = localPose; // adjust the local position to like grabbing the object
        targetTransform.localRotation = Quaternion.identity; // adjust the local rotation
    }

    public void DetachObject(GameObject target)
    {
        Transform targetTransform = target.transform;
        // if having rigidbody, set it to non-kinematic
        if (targetTransform.GetComponent<Rigidbody>() != null)
            targetTransform.GetComponent<Rigidbody>().isKinematic = false;
        // restore the previous parent before pick
        if (Utils.PrevParentsBeforeAttach.TryGetValue(targetTransform, out Transform prevParent))
        {
            targetTransform.SetParent(prevParent);
            Utils.PrevParentsBeforeAttach.Remove(targetTransform);
        }
    }

    public void StopAnimation(string id)
    {
        Utils.IndicateAnimationStop(id);
    }

    public IEnumerator RemoveObject(GameObject obj)
    {
        // Destroy the object at the end of the frame to properly stop existing coroutines
        yield return new WaitForEndOfFrame();
        Destroy(obj);
    }

    public void MakeGrabbable(GameObject obj, bool isSynced = true)
    {
        if (obj == null) { return; }
        // create a rigidbody component for the object if it doesn't have one
        if (obj.GetComponent<Rigidbody>() == null)
        {
            obj.AddComponent<Rigidbody>();
        }
        // add collider to the object if it doesn't have one
        if (obj.GetComponentsInChildren<Collider>() == null)
        {
            obj.AddComponent<BoxCollider>();
        }
        Rigidbody rb = obj.GetComponent<Rigidbody>();
        // set properties of the rigidbody
        rb.useGravity = false;
        rb.isKinematic = true;
        GameObject grabbable = null;
        if (obj.GetComponentInChildren<Grabbable>() != null)
        {
            grabbable = obj.GetComponentInChildren<Grabbable>().gameObject;
            ControlGrabbableOwnership(obj, grabbable, isSynced);
            return;
        }
        // instantiate the grabble prefab as the child object of the object
        grabbable = Instantiate(_grabblePrefab, obj.transform);
        // set properties of the grabbable
        // Grabble -> TargetTransform
        Grabbable grabbableScript = grabbable.GetComponent<Grabbable>();
        grabbableScript.InjectOptionalTargetTransform(obj.transform);
        bool getOneGrab = grabbableScript.TryGetComponent(out OneGrabFreeTransformer grabbableTransformer);
        if (!getOneGrab) grabbableTransformer = grabbable.AddComponent<OneGrabFreeTransformer>();
        grabbableScript.InjectOptionalOneGrabTransformer(grabbableTransformer);
        grabbableTransformer.Initialize(grabbableScript);
        bool getTwoGrab = grabbableScript.TryGetComponent(out TwoGrabFreeTransformer grabbableTwoGrabTransformer);
        if (!getTwoGrab) grabbableTwoGrabTransformer = grabbable.AddComponent<TwoGrabFreeTransformer>();
        // the script will not automatically initialize the constraints unless the script is attached before running application, in which Unity automatically initializes the serialized fields
        var twoGrabConstraints = new TwoGrabFreeTransformer.TwoGrabFreeConstraints();
        twoGrabConstraints.MinScale = new FloatConstraint();
        twoGrabConstraints.MaxScale = new FloatConstraint();
        grabbableTwoGrabTransformer.InjectOptionalConstraints(twoGrabConstraints);
        grabbableScript.InjectOptionalTwoGrabTransformer(grabbableTwoGrabTransformer);
        grabbableTwoGrabTransformer.Initialize(grabbableScript);
        // HandGrabInteractable -> Rigidbody
        HandGrabInteractable handGrabInteractableScript = grabbable.GetComponent<HandGrabInteractable>();
        handGrabInteractableScript.InjectRigidbody(rb);
        // GrabInteractable -> Rigidbody
        GrabInteractable grabInteractableScript = grabbable.GetComponent<GrabInteractable>();
        grabInteractableScript.InjectRigidbody(rb);
        // PhysicsGrabbable -> Grabbable
        PhysicsGrabbable physicsGrabbableScript = grabbable.GetComponent<PhysicsGrabbable>();
        physicsGrabbableScript.InjectRigidbody(rb);

        ControlGrabbableOwnership(obj, grabbable, isSynced);
    }

    private void ControlGrabbableOwnership(GameObject obj, GameObject grabbable, bool isSynced)
    {
        var objectOwnership = grabbable.AddComponent<ObjectOwnership>();
        objectOwnership.AssignSyncManager(_syncManager);
        objectOwnership.SetTargetObj(obj);
        grabbable.AddComponent<TransferOwnershipOnSelect>();
        if (!isSynced)
            _syncManager.AddOwnedObject(obj);
    }

    void OnDestroy()
    {
        // Stop all coroutines on destroy to clean up
        StopAllCoroutines();
        // remove all animation from the dictionary
        Utils.RemoveAllAnimations();
    }
}
