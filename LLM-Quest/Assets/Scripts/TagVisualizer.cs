using Newtonsoft.Json;
using System;
using UnityEngine;

public class TagVisualizer : MonoBehaviour
{
    [Serializable]
    public class TagData
    {
        [JsonProperty("tagScale")]
        public float TagScale { get; set; }

        [JsonProperty("tagToWorldMatrix")]
        public float[] TagToWorldMatrix { get; set; } // Expecting a 4x4 matrix as a flat array of 16 floats

        public Matrix4x4 GetTagToWorldMatrix()
        {
            if (TagToWorldMatrix == null || TagToWorldMatrix.Length != 16)
            {
                Debug.LogError("TagToWorldMatrix must have exactly 16 elements.");
            }

            // Unity's Matrix4x4 stores elements in column-major order, so cannot directly use the flat array
            Matrix4x4 matrix = new Matrix4x4
            {
                m00 = TagToWorldMatrix[0],
                m01 = TagToWorldMatrix[1],
                m02 = TagToWorldMatrix[2],
                m03 = TagToWorldMatrix[3],
                m10 = TagToWorldMatrix[4],
                m11 = TagToWorldMatrix[5],
                m12 = TagToWorldMatrix[6],
                m13 = TagToWorldMatrix[7],
                m20 = TagToWorldMatrix[8],
                m21 = TagToWorldMatrix[9],
                m22 = TagToWorldMatrix[10],
                m23 = TagToWorldMatrix[11],
                m30 = TagToWorldMatrix[12],
                m31 = TagToWorldMatrix[13],
                m32 = TagToWorldMatrix[14],
                m33 = TagToWorldMatrix[15]
            };
            return matrix;
        }
    }

    private Mesh _edgeMesh;
    private Material _edgeMaterial;

    public TagVisualizer(Material material)
    {
        _edgeMaterial = material;
        _edgeMesh = CreateEdgeMesh();
    }

    public void Draw(Vector3 position, Quaternion rotation, float scale)
    {
        // Draw the mesh using the input position, rotation, and scale
        Graphics.DrawMesh(_edgeMesh, Matrix4x4.TRS(position, rotation, Vector3.one * scale), _edgeMaterial, 0);
    }

    private Mesh CreateEdgeMesh()
    {
        Mesh mesh = new Mesh();

        // Define the vertices for cube edges
        Vector3[] vertices = new Vector3[]
        {
            new Vector3(-0.5f, 0, -0.5f), // 0: Bottom-left-back
            new Vector3(0.5f, 0, -0.5f),  // 1: Bottom-right-back
            new Vector3(0.5f, 0, 0.5f),   // 2: Bottom-right-front
            new Vector3(-0.5f, 0, 0.5f),  // 3: Bottom-left-front

            new Vector3(-0.5f, 1, -0.5f), // 4: Top-left-back
            new Vector3(0.5f, 1, -0.5f),  // 5: Top-right-back
            new Vector3(0.5f, 1, 0.5f),   // 6: Top-right-front
            new Vector3(-0.5f, 1, 0.5f),   // 7: Top-left-front

            new Vector3(0, 0, 0),          // 8: Bottom-Center
            new Vector3(0, 0, 0.2f),          // 9: Bottom-forward-direction
            new Vector3(0.4f, 0, 0),          // 10: Bottom-right-direction
        };

        // Define the edges as lines (pairs of indices)
        int[] indices = new int[]
        {
            0, 1,  1, 2,  2, 3,  3, 0, // Bottom edges
            4, 5,  5, 6,  6, 7,  7, 4, // Top edges
            0, 4,  1, 5,  2, 6,  3, 7,  // Vertical edges
            8, 9,  8, 10 // Bottom center and direction lines
        };

        // Assign vertices and indices to the mesh
        mesh.vertices = vertices;
        mesh.SetIndices(indices, MeshTopology.Lines, 0);

        return mesh;
    }
}
