using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace FineDining;

// Three fixed content icons; returned sprite/texture ownership belongs to the registry.
internal static class GeneratedIconRenderer
{
    private const int IconSize = 128;
    private const int IconLayer = 30;
    internal static Sprite? Render(GameObject sourcePrefab, string iconName)
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
        {
            return null;
        }

        Texture2D? outputTexture = null;
        bool succeeded = false;
        GameObject? renderObject = null;
        Camera? camera = null;
        Light? light = null;
        RenderTexture? renderTexture = null;
        RenderTexture? previousActive = RenderTexture.active;

        try
        {
            renderObject = SpawnIconRenderClone(sourcePrefab, out List<Renderer> renderers);
            if ((object?)renderObject == null || renderers.Count == 0)
            {
                return null;
            }

            Bounds bounds = renderers[0].bounds;
            foreach (Renderer renderer in renderers.Skip(1))
            {
                bounds.Encapsulate(renderer.bounds);
            }

            Vector3 renderSize = bounds.size;
            renderObject.transform.position -= bounds.center;

            camera = new GameObject("FineDining Icon Camera", typeof(Camera)).GetComponent<Camera>();
            camera.enabled = false;
            camera.backgroundColor = Color.clear;
            camera.clearFlags = CameraClearFlags.Color;
            camera.fieldOfView = 0.5f;
            camera.farClipPlane = 10000000f;
            camera.cullingMask = 1 << IconLayer;
            camera.transform.rotation = Quaternion.Euler(0f, 180f, 0f);

            light = new GameObject("FineDining Icon Light", typeof(Light)).GetComponent<Light>();
            light.transform.position = Vector3.zero;
            light.transform.rotation = Quaternion.Euler(5f, 180f, 5f);
            light.type = LightType.Directional;
            light.cullingMask = 1 << IconLayer;
            light.intensity = 1.3f;

            float framedSize = Mathf.Max(renderSize.x, renderSize.y) + 0.1f;
            float distance = framedSize / Mathf.Tan(camera.fieldOfView * ((float)Math.PI / 180f));
            if (float.IsNaN(distance) || float.IsInfinity(distance) || distance <= 0f)
            {
                distance = Mathf.Max(renderSize.x, Mathf.Max(renderSize.y, renderSize.z)) + 1f;
            }

            camera.transform.position = new Vector3(0f, 0f, distance);

            renderTexture = RenderTexture.GetTemporary(IconSize, IconSize, 24, RenderTextureFormat.ARGB32);
            camera.targetTexture = renderTexture;
            RenderTexture.active = renderTexture;
            GL.Clear(clearDepth: true, clearColor: true, Color.clear);
            camera.Render();

            Texture2D texture = new(IconSize, IconSize, TextureFormat.RGBA32, mipChain: false)
            {
                name = iconName
            };
            outputTexture = texture;
            Rect rect = new(0f, 0f, IconSize, IconSize);
            texture.ReadPixels(rect, 0, 0);
            texture.Apply();

            Sprite sprite = Sprite.Create(texture, rect, new Vector2(0.5f, 0.5f), 100f);
            sprite.name = texture.name;
            succeeded = true;
            return sprite;
        }
        catch (Exception ex)
        {
            FineDiningPlugin.Log.LogWarning($"FineDining icon render failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (!succeeded && outputTexture != null) UnityEngine.Object.Destroy(outputTexture);
            RenderTexture.active = previousActive;
            if ((object?)camera != null)
            {
                camera.targetTexture = null;
                DestroyTemporaryObject(camera.gameObject);
            }

            if ((object?)light != null)
            {
                DestroyTemporaryObject(light.gameObject);
            }

            if (renderTexture != null)
            {
                RenderTexture.ReleaseTemporary(renderTexture);
            }

            if ((object?)renderObject != null)
            {
                DestroyTemporaryObject(renderObject);
            }
        }
    }

    private static GameObject? SpawnIconRenderClone(GameObject sourcePrefab, out List<Renderer> renderers)
    {
        renderers = new List<Renderer>();
        GameObject? inactiveRoot = null;
        GameObject? renderObject = null;
        try
        {
            inactiveRoot = new GameObject("FineDining_IconRoot");
            inactiveRoot.SetActive(false);

            renderObject = UnityEngine.Object.Instantiate(sourcePrefab, inactiveRoot.transform, worldPositionStays: false);
            renderObject.name = "FineDining_IconRender";
            StripIconRenderClone(renderObject);
            renderObject.transform.SetParent(null, worldPositionStays: false);
            UnityEngine.Object.DestroyImmediate(inactiveRoot);
            inactiveRoot = null;

            renderObject.transform.position = Vector3.zero;
            renderObject.transform.rotation = Quaternion.Euler(23f, 51f, 25.8f);
            SetLayerRecursive(renderObject, IconLayer);
            renderObject.SetActive(true);

            renderers = GetVisualRenderers(renderObject)
                .Where(renderer => renderer.gameObject.activeInHierarchy)
                .ToList();
            if (renderers.Count == 0)
            {
                DestroyTemporaryObject(renderObject);
                return null;
            }

            HashSet<Renderer> selected = new(renderers);
            foreach (Renderer renderer in renderObject.GetComponentsInChildren<Renderer>(includeInactive: true))
            {
                if ((object?)renderer != null && !selected.Contains(renderer))
                {
                    renderer.enabled = false;
                }
            }

            return renderObject;
        }
        catch
        {
            DestroyTemporaryObject(renderObject);
            if ((object?)inactiveRoot != null)
            {
                UnityEngine.Object.DestroyImmediate(inactiveRoot);
            }

            throw;
        }
    }

    private static void StripIconRenderClone(GameObject renderObject)
    {
        foreach (Transform transform in renderObject.GetComponentsInChildren<Transform>(includeInactive: true))
        {
            Component[] components = transform.GetComponents<Component>();
            for (int index = components.Length - 1; index >= 0; index--)
            {
                Component component = components[index];
                if (component == null ||
                    component is Transform ||
                    component is MeshFilter ||
                    component is MeshRenderer ||
                    component is SkinnedMeshRenderer)
                {
                    continue;
                }

                try
                {
                    UnityEngine.Object.DestroyImmediate(component);
                    if (component != null)
                        throw new InvalidOperationException("A gameplay component remained on the icon clone.");
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("Could not safely strip the icon clone before activation.", ex);
                }
            }
        }
    }

    private static List<Renderer> GetVisualRenderers(GameObject prefab)
    {
        return prefab
            .GetComponentsInChildren<Renderer>(includeInactive: true)
            .Where(renderer => renderer != null && renderer.enabled && !renderer.GetType().Name.Equals("ParticleSystemRenderer", StringComparison.Ordinal))
            .ToList();
    }

    private static void SetLayerRecursive(GameObject gameObject, int layer)
    {
        gameObject.layer = layer;
        foreach (Transform child in gameObject.transform)
        {
            SetLayerRecursive(child.gameObject, layer);
        }
    }

    private static void DestroyTemporaryObject(GameObject? gameObject)
    {
        if ((object?)gameObject == null)
        {
            return;
        }

        try
        {
            gameObject.SetActive(false);
            UnityEngine.Object.DestroyImmediate(gameObject);
        }
        catch (Exception ex)
        {
            FineDiningPlugin.Log.LogDebug($"Could not immediately destroy icon render object '{gameObject.name}': {ex.Message}");
            UnityEngine.Object.Destroy(gameObject);
        }
    }

}
