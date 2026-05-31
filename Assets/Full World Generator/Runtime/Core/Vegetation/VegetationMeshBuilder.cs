using System.Collections.Generic;
using UnityEngine;

namespace FullWorld
{
    public static class VegetationMeshBuilder
    {
        /// <summary>
        /// Builds a cone mesh representing a tree. Apex at (0, height, 0), base circle at y=0.
        /// </summary>
        public static Mesh BuildCone(float height, float radius, int segments = 8)
        {
            int vertCount = segments + 2; // base center + apex + ring
            int triCount = segments * 2;   // sides + base

            var vertices = new Vector3[vertCount];
            var normals = new Vector3[vertCount];
            var triangles = new int[triCount * 3];
            var uvs = new Vector2[vertCount];

            // Apex
            vertices[0] = new Vector3(0f, height, 0f);
            normals[0] = Vector3.up;
            uvs[0] = new Vector2(0.5f, 1f);

            // Base center
            vertices[1] = Vector3.zero;
            normals[1] = Vector3.down;
            uvs[1] = new Vector2(0.5f, 0f);

            // Base ring
            for (int i = 0; i < segments; i++)
            {
                float angle = (float)i / segments * Mathf.PI * 2f;
                float x = Mathf.Cos(angle) * radius;
                float z = Mathf.Sin(angle) * radius;

                int idx = i + 2;
                vertices[idx] = new Vector3(x, 0f, z);

                // Approximate side normal
                var sideNormal = new Vector3(x, radius * 0.5f, z).normalized;
                normals[idx] = sideNormal;

                float u = (float)i / segments;
                uvs[idx] = new Vector2(u, 0f);
            }

            int tri = 0;

            // Side triangles (apex → ring[i] → ring[i+1])
            for (int i = 0; i < segments; i++)
            {
                int next = (i + 1) % segments;
                triangles[tri++] = 0;
                triangles[tri++] = i + 2;
                triangles[tri++] = next + 2;
            }

            // Base triangles (center → ring[i+1] → ring[i])
            for (int i = 0; i < segments; i++)
            {
                int next = (i + 1) % segments;
                triangles[tri++] = 1;
                triangles[tri++] = next + 2;
                triangles[tri++] = i + 2;
            }

            var mesh = new Mesh
            {
                vertices = vertices,
                normals = normals,
                triangles = triangles,
                uv = uvs
            };
            mesh.RecalculateNormals();
            return mesh;
        }

        /// <summary>
        /// Builds a hemisphere mesh representing a bush. Flat side at y=0, dome goes up.
        /// </summary>
        public static Mesh BuildHemisphere(float height, float radius, int segments = 12, int rings = 6)
        {
            // Vertex layout: top vertex + rings of vertices + bottom ring
            int vertCount = 1 + rings * segments;
            var vertices = new Vector3[vertCount];
            var normals = new Vector3[vertCount];
            var uvs = new Vector2[vertCount];
            var triangles = new List<int>();

            // Top vertex
            vertices[0] = new Vector3(0f, height, 0f);
            normals[0] = Vector3.up;
            uvs[0] = new Vector2(0.5f, 1f);

            for (int r = 1; r <= rings; r++)
            {
                float ringT = (float)r / rings;
                // Parametric hemisphere: y = cos(theta) * height, horizontal = sin(theta) * radius
                float theta = ringT * Mathf.PI * 0.5f; // 0 to PI/2
                float y = Mathf.Cos(theta) * height;
                float ringRadius = Mathf.Sin(theta) * radius;

                for (int s = 0; s < segments; s++)
                {
                    float angle = (float)s / segments * Mathf.PI * 2f;
                    float x = Mathf.Cos(angle) * ringRadius;
                    float z = Mathf.Sin(angle) * ringRadius;

                    int idx = 1 + (r - 1) * segments + s;
                    vertices[idx] = new Vector3(x, y, z);
                    normals[idx] = new Vector3(x, y, z).normalized;
                    uvs[idx] = new Vector2((float)s / segments, 1f - ringT);
                }
            }

            // Top cap triangles
            for (int s = 0; s < segments; s++)
            {
                int next = (s + 1) % segments;
                triangles.Add(0);
                triangles.Add(1 + s);
                triangles.Add(1 + next);
            }

            // Ring-to-ring triangles
            for (int r = 1; r < rings; r++)
            {
                int ringStart = 1 + (r - 1) * segments;
                int nextRingStart = 1 + r * segments;

                for (int s = 0; s < segments; s++)
                {
                    int next = (s + 1) % segments;
                    int v0 = ringStart + s;
                    int v1 = ringStart + next;
                    int v2 = nextRingStart + s;
                    int v3 = nextRingStart + next;

                    triangles.Add(v0);
                    triangles.Add(v2);
                    triangles.Add(v1);

                    triangles.Add(v1);
                    triangles.Add(v2);
                    triangles.Add(v3);
                }
            }

            var mesh = new Mesh
            {
                vertices = vertices,
                normals = normals,
                triangles = triangles.ToArray(),
                uv = uvs
            };
            mesh.RecalculateNormals();
            return mesh;
        }
    }
}
