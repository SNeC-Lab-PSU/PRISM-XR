using UnityEngine;

public class RayVisualizer : MonoBehaviour
{
    private LineRenderer lineRenderer;

    public static GameObject Create(Ray ray, float length, Material material, float width = 0.01f)
    {
        // Create a new GameObject
        GameObject visualizerObject = new GameObject("RayVisualizer");

        // Add the RayVisualizer script to it
        RayVisualizer visualizer = visualizerObject.AddComponent<RayVisualizer>();

        // Initialize the LineRenderer
        visualizer.Initialize(ray, length, width, material);

        return visualizerObject;
    }

    private void Initialize(Ray ray, float length, float width, Material material)
    {
        // Add and configure the LineRenderer
        lineRenderer = gameObject.AddComponent<LineRenderer>();
        lineRenderer.material = material;
        lineRenderer.startWidth = width;
        lineRenderer.endWidth = width;
        lineRenderer.positionCount = 2; // Start and end points

        // Calculate start and end points
        Vector3 startPoint = ray.origin;
        Vector3 endPoint = ray.origin + ray.direction.normalized * length;

        // Set the LineRenderer positions
        lineRenderer.SetPosition(0, startPoint);
        lineRenderer.SetPosition(1, endPoint);
    }
}
