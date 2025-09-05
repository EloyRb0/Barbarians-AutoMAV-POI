using UnityEngine;

public static class CameraStreamer
{
    // Captures a JPG from a Camera without changing game resolution.
    // NOTE: Do NOT call every frame at high resolutions; this allocates.
    public static byte[] CaptureJPG(Camera cam, int width = 640, int height = 360, int quality = 60)
    {
        if (cam == null) return null;

        RenderTexture prev = cam.targetTexture;
        RenderTexture rt = RenderTexture.GetTemporary(width, height, 16, RenderTextureFormat.ARGB32);
        cam.targetTexture = rt;
        cam.Render();

        RenderTexture.active = rt;
        Texture2D tex = new Texture2D(width, height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
        tex.Apply(false, false);

        byte[] jpg = tex.EncodeToJPG(Mathf.Clamp(quality, 1, 100));

        // cleanup
        cam.targetTexture = prev;
        RenderTexture.active = null;
        RenderTexture.ReleaseTemporary(rt);
        Object.Destroy(tex);

        return jpg;
    }
}

