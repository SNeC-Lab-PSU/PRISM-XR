using System.Collections;
using UnityEngine;
using UnityEngine.UI;

public class UserFeedback : MonoBehaviour
{
    [SerializeField]
    private Shader _textureShader;
    [SerializeField]
    private Material _tagMaterial;
    [SerializeField]
    private GameObject _userFeedbackCanvas;
    [SerializeField]
    private RawImage _dialogImg;


    bool _isRegistered = false;
    float _tagSize = 0.1f;
    Vector3 _tagPosition = new Vector3(0, 0, 0);
    Quaternion _tagRotation = Quaternion.identity;
    GameObject _emptyTag;
    TagVisualizer _tagVisualizer;
    WebSocketClient _websocketClient;
    AspectRatioFitter _fitter;

    private void Start()
    {
        _tagVisualizer = new TagVisualizer(_tagMaterial);
        _websocketClient = GetComponent<WebSocketClient>();
        _emptyTag = new GameObject("TagPlaceholder");
        _fitter = _dialogImg.gameObject.GetComponent<AspectRatioFitter>();
    }

    private void LateUpdate()
    {
        if (_isRegistered)
        {
            DisplayTag(_tagPosition, _tagRotation, _tagSize);
        }
    }

    public void RegisterTag(Vector3 position, Quaternion rotation, float size)
    {
        _tagPosition = position;
        _tagRotation = rotation;
        _tagSize = size;
        _isRegistered = true;
    }

    void DisplayTag(Vector3 position, Quaternion rotation, float scale)
    {
        _emptyTag.transform.position = position;
        _emptyTag.transform.rotation = rotation;
        _tagVisualizer.Draw(position, rotation, scale);
    }

    public Vector3 GetPosInTagCoor(Vector3 pos)
    {
        return _emptyTag.transform.InverseTransformPoint(pos);
    }

    public Quaternion GetRotInTagCoor(Quaternion rot)
    {
        return Quaternion.Inverse(_emptyTag.transform.rotation) * rot;
    }

    public Vector3 GetPosFromTagCoor(Vector3 pos)
    {
        return _emptyTag.transform.TransformPoint(pos);
    }

    public Quaternion GetRotFromTagCoor(Quaternion rot)
    {
        return _emptyTag.transform.rotation * rot;
    }

    public void ShowDialogWithImg(string path)
    {
        _dialogImg.texture = Utils.LoadImgAsTexture(path);
        _fitter.aspectRatio = (float)_dialogImg.texture.width / _dialogImg.texture.height;
        _userFeedbackCanvas.SetActive(true);
    }

    public void UploadConfirmedByUser()
    {
        if (_userFeedbackCanvas.activeSelf == false)
            return;
        _websocketClient.SendSpecialTypeToServer("confirm_crop_image", new byte[0], "Yes");
        _userFeedbackCanvas.SetActive(false);
    }

    public void CancelUploadByUser()
    {
        if (_userFeedbackCanvas.activeSelf == false)
            return;
        _websocketClient.SendSpecialTypeToServer("confirm_crop_image", new byte[0], "No");
        _userFeedbackCanvas.SetActive(false);
    }

    // Display a quad with a texture from local image file
    public void DisplayImage(string path)
    {
        Texture2D targetTexture = Utils.LoadImgAsTexture(path);
        GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        float ratio = targetTexture.height / (float)targetTexture.width;
        quad.transform.localScale = new Vector3(quad.transform.localScale.x, quad.transform.localScale.x * ratio, quad.transform.localScale.z);
        Renderer quadRenderer = quad.GetComponent<Renderer>();
        Material material = new Material(_textureShader);
        material.mainTexture = targetTexture;
        quadRenderer.material = material;
        Matrix4x4 cameraToWorldMatrix = Camera.main.cameraToWorldMatrix;
        // Place the quad in front of the camera
        Vector3 position = cameraToWorldMatrix.GetColumn(3) - cameraToWorldMatrix.GetColumn(2);
        Quaternion rotation = Quaternion.LookRotation(-cameraToWorldMatrix.GetColumn(2), cameraToWorldMatrix.GetColumn(1));
        targetTexture.wrapMode = TextureWrapMode.Clamp;
        quad.transform.position = position;
        quad.transform.rotation = rotation;
        // Destroy the quad after 5 seconds
        StartCoroutine(DestroyQuad(quad, 5f));
    }

    IEnumerator DestroyQuad(GameObject quad, float delay)
    {
        yield return new WaitForSeconds(delay);
        Destroy(quad);
    }
}
