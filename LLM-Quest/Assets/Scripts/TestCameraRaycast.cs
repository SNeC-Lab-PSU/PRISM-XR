using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class TestCameraRaycast : MonoBehaviour
{
    public Camera testCamera;
    public int pixelX = 0;
    public int pixelY = 0;
    private GameObject _testObj;

    void Start()
    {
        if (testCamera == null)
        {
            testCamera = Camera.main; // Default to the main camera
        }

        _testObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _testObj.transform.localScale = new Vector3(0.1f, 0.1f, 0.1f);
    }

    private void Update()
    {
        Ray ray = Utils.GetRayFromPixel(
            testCamera.transform.position,
            testCamera.transform.rotation,
            testCamera.projectionMatrix,
            Screen.width,
            Screen.height,
            pixelX,
            pixelY
        );
        //Ray ray = Utils.GetRayFromPixel(
        //    testCamera.cameraToWorldMatrix,
        //    testCamera.projectionMatrix,
        //    Screen.width,
        //    Screen.height,
        //    pixelX,
        //    pixelY
        //);

        // Visualize the ray
        Debug.DrawRay(ray.origin, ray.direction * 1, Color.red, 1f);

        // Calculate the end position of the ray
        Vector3 endPosition = ray.origin + ray.direction * 1;
        _testObj.transform.position = endPosition;
    }
}
