using System.Collections.Generic;
using UnityEngine;

public class TopDownCamera : MonoBehaviour
{
    [Header("Targets")]
    public SearchCoordinator coordinator; // assign in Inspector

    [Header("Camera")]
    public float height = 50f;       // how far above the swarm
    public float followLerp = 2f;    // smooth movement
    public float minSize = 25f;      // zoom in
    public float maxSize = 80f;      // zoom out based on spread

    Camera cam;

    void Awake()
    {
        cam = GetComponent<Camera>();
        if (cam.orthographic == false)
        {
            cam.orthographic = true; // easier for top-down overview
        }
    }

    void LateUpdate()
    {
        if (coordinator == null) return;

        // Get all active drone positions
        List<Vector3> dronePositions = coordinator.GetDronePositions();
        if (dronePositions.Count == 0) return;

        // Compute centroid of swarm
        Vector3 centroid = Vector3.zero;
        foreach (var p in dronePositions) centroid += p;
        centroid /= dronePositions.Count;

        // Place camera above centroid
        Vector3 targetPos = new Vector3(centroid.x, centroid.y + height, centroid.z);
        transform.position = Vector3.Lerp(transform.position, targetPos, Time.deltaTime * followLerp);

        // Always look straight down
        transform.rotation = Quaternion.Euler(90f, 0f, 0f);

        // Optional: zoom out if swarm spread is large
        float maxDist = 0f;
        foreach (var p in dronePositions)
            maxDist = Mathf.Max(maxDist, Vector3.Distance(centroid, p));

        float desiredSize = Mathf.Clamp(maxDist * 1.5f, minSize, maxSize);
        cam.orthographicSize = Mathf.Lerp(cam.orthographicSize, desiredSize, Time.deltaTime * 2f);
    }
}
