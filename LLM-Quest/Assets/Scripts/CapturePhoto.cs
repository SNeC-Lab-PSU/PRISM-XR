using UnityEngine;
using System.IO;
using PassthroughCameraSamples;

public class CapturePhoto : MonoBehaviour
{
    public bool UseFakePhoto = false;
    public bool VisualizePhoto = false;
    public string ImagePath;
    public int TargetWidth = 1280;
    public int TargetHeight = 720;
    public Vector3 CamPosition;
    public Quaternion CamRotation;
    public Matrix4x4 CameraToWorldMatrix;
    public Matrix4x4 CamProjectionMatrix;

    [SerializeField]
    private WebCamTextureManager _webCamTextureManager;

    private PassthroughCameraEye _cameraEye => _webCamTextureManager.Eye;
    private bool _isReadyToCapturePhoto = false;
    private bool _isCapturePhoto = false;
    private bool _isRegistration = false;
    private Texture2D _capturedFov;
    private Color32[] _pixelsBuffer;
    WebSocketClient _webSocketClient;
    ObjectCreator _objectCreator;

    private void Start()
    {
        _webSocketClient = GetComponent<WebSocketClient>();
        _objectCreator = GetComponent<ObjectCreator>();
    }

    // Update is called once per frame
    void Update()
    {
        _isReadyToCapturePhoto = UseFakePhoto ? true : _webCamTextureManager.WebCamTexture != null;
        if (Input.GetKeyDown(KeyCode.P))
        {
            Debug.Log("trying to capture photo");
            Capture();
        }
    }

    public void Capture(bool useAsRegistry = false)
    {
        if (!_isCapturePhoto && _isReadyToCapturePhoto)
        {
            _isCapturePhoto = true;
            _isRegistration = useAsRegistry;
            if (UseFakePhoto)
            {
                ImagePath = System.IO.Path.Combine(Application.persistentDataPath, "testimg.jpg");
                _webSocketClient.SendImageToServer(ImagePath);
                _isCapturePhoto = false;
            }
            else
            {
                string filename = string.Format(@"CapturedImage{0}_n.jpg", Time.time);
                string filePath = Application.persistentDataPath + "/" + filename;
                ImagePath = filePath;

                Debug.Log("Trying to save photo to: " + filePath);
                // Capture the photo to memory
                CapturePhotoToMemory();
            }
        }
        else
        {
            Debug.Log("Is capturing a photo or photo capture initialization has not been finished.");
        }
    }

    void CapturePhotoToMemory()
    {
        var webCamTexture = _webCamTextureManager.WebCamTexture;
        if (webCamTexture == null || !webCamTexture.isPlaying)
            return;

        if (_capturedFov == null)
        {
            _capturedFov = new Texture2D(webCamTexture.width, webCamTexture.height, TextureFormat.RGBA32, false);
        }

        _pixelsBuffer ??= new Color32[webCamTexture.width * webCamTexture.height];
        _webCamTextureManager.WebCamTexture.GetPixels32(_pixelsBuffer);
        _capturedFov.SetPixels32(_pixelsBuffer);
        _capturedFov.Apply();

        // Encode the image and upload it to server
        byte[] imageBytes = _capturedFov.EncodeToJPG();
        var cameraPose = PassthroughCameraUtils.GetCameraPoseInWorld(_cameraEye);
        CamPosition = cameraPose.position;
        CamRotation = cameraPose.rotation;
        CameraToWorldMatrix = Matrix4x4.TRS(CamPosition, CamRotation, Vector3.one);
        CamProjectionMatrix = Camera.main.projectionMatrix;
        // Send position and rotation to server
        _webSocketClient.SendTextToServer("Camera Pose: " + cameraPose.position.ToString() + " " + cameraPose.rotation.eulerAngles.ToString() + "\nProjection Matrix:\n" + CamProjectionMatrix.ToString() + "\nCamera to world Matrix:\n" + CameraToWorldMatrix.ToString());
        // Create rays to visualize the boundary of captured photo
        if (VisualizePhoto)
        {
            Vector2Int cameraResolution = new Vector2Int(webCamTexture.width, webCamTexture.height);
            _objectCreator.GetRayFromPixelCoor(0, 0);
            _objectCreator.GetRayFromPixelCoor(0, cameraResolution.y);
            _objectCreator.GetRayFromPixelCoor(cameraResolution.x, 0);
            _objectCreator.GetRayFromPixelCoor(cameraResolution.x, cameraResolution.y);
        }
        if (!_isRegistration)
        {
            _webSocketClient.SendImageBytesToServer(imageBytes, Path.GetFileName(ImagePath));
        }
        else
        {
            // Get camera intrinsics
            var cameraIntrinsics = PassthroughCameraUtils.GetCameraIntrinsics(_cameraEye);
            // Send position and rotation to server
            string poseInfo = "Quest\nProjection Matrix:\n" + CamProjectionMatrix.ToString() + "\nCamera to world Matrix:\n" + CameraToWorldMatrix.ToString() +
                "\nCamera Intrinsics:\n" +
                cameraIntrinsics.FocalLength.x + " " + cameraIntrinsics.FocalLength.y + " " +
                cameraIntrinsics.PrincipalPoint.x + " " + cameraIntrinsics.PrincipalPoint.y + "\n";
            _webSocketClient.SendSpecialTypeToServer("registration", imageBytes, poseInfo);
        }
        _isRegistration = false;
        _isCapturePhoto = false;
    }

    private void OnDestroy()
    {
        _isReadyToCapturePhoto = false;

        if (_capturedFov != null)
            Destroy(_capturedFov);
    }
}
