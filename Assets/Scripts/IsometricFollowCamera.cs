using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class IsometricFollowCamera : MonoBehaviour
{
    [Header("Coordinator / Target")]
    public SearchCoordinator coordinator;
    public bool followWinner = true;
    public Vector3 targetOffset = new Vector3(0f, 1.2f, 0f);

    [Header("View (top-down with slight angle)")]
    [Range(1f, 89f)] public float elevationFromTopDeg = 12f; // 0 = straight down, 12 = slight angle
    [Range(-180f, 180f)] public float yawDeg = 35f;
    public float distance = 55f;

    [Header("Motion")]
    public float followLerp = 5f;
    public float lookLerp = 10f;

    [Header("Manual Orbit (optional)")]
    public bool allowOrbitKeys = true;
    public float orbitSpeed = 60f;
    public float zoomSpeed = 30f;
    public float minDistance = 12f;
    public float maxDistance = 140f;

    Transform cam;
    Vector3 curPos;
    Quaternion curRot;

    void Awake()
    {
        cam = transform;
        curPos = cam.position;
        curRot = cam.rotation;
    }

    void Update()
    {
        if (!coordinator) return;

        // --- optional manual controls ---
        if (allowOrbitKeys)
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.qKey.isPressed) yawDeg -= orbitSpeed * Time.deltaTime;
                if (kb.eKey.isPressed) yawDeg += orbitSpeed * Time.deltaTime;
                if (kb.rKey.isPressed) elevationFromTopDeg = Mathf.Clamp(elevationFromTopDeg - orbitSpeed * Time.deltaTime * 0.25f, 1f, 89f); // more top-down
                if (kb.fKey.isPressed) elevationFromTopDeg = Mathf.Clamp(elevationFromTopDeg + orbitSpeed * Time.deltaTime * 0.25f, 1f, 89f); // more angled
                if ((kb.equalsKey != null && kb.equalsKey.isPressed) || (kb.numpadPlusKey != null && kb.numpadPlusKey.isPressed))
                    distance = Mathf.Max(minDistance, distance - zoomSpeed * Time.deltaTime);
                if ((kb.minusKey != null && kb.minusKey.isPressed) || (kb.numpadMinusKey != null && kb.numpadMinusKey.isPressed))
                    distance = Mathf.Min(maxDistance, distance + zoomSpeed * Time.deltaTime);
                var mouse = Mouse.current;
                if (mouse != null)
                {
                    float wheel = -mouse.scroll.ReadValue().y;
                    if (Mathf.Abs(wheel) > 0.01f)
                        distance = Mathf.Clamp(distance - wheel * (zoomSpeed * 0.02f), minDistance, maxDistance);
                }
            }
#else
            if (Input.GetKey(KeyCode.Q)) yawDeg -= orbitSpeed * Time.deltaTime;
            if (Input.GetKey(KeyCode.E)) yawDeg += orbitSpeed * Time.deltaTime;
            if (Input.GetKey(KeyCode.R)) elevationFromTopDeg = Mathf.Clamp(elevationFromTopDeg - orbitSpeed * Time.deltaTime * 0.25f, 1f, 89f);
            if (Input.GetKey(KeyCode.F)) elevationFromTopDeg = Mathf.Clamp(elevationFromTopDeg + orbitSpeed * Time.deltaTime * 0.25f, 1f, 89f);
            if (Input.GetKey(KeyCode.Equals) || Input.GetKey(KeyCode.KeypadPlus))
                distance = Mathf.Max(minDistance, distance - zoomSpeed * Time.deltaTime);
            if (Input.GetKey(KeyCode.Minus) || Input.GetKey(KeyCode.KeypadMinus))
                distance = Mathf.Min(maxDistance, distance + zoomSpeed * Time.deltaTime);
#endif
        }

        // focus = winner if exists, otherwise ROI
        Vector3 focus;
        var winner = coordinator.HasWinner ? coordinator.GetWinnerDrone() : null;
        if (followWinner && winner != null)
            focus = winner.transform.position + targetOffset;
        else
            focus = coordinator.GetROICenter() + targetOffset;

        // Build offset from "top" with a small tilt (elevationFromTopDeg)
        float tilt = Mathf.Deg2Rad * elevationFromTopDeg;  // small angle from straight down
        float yaw  = Mathf.Deg2Rad * yawDeg;

        // Start straight down, then rotate by yaw around Y, then tilt outward
        // Equivalent: point mostly downward (negative Y)
        Vector3 dir = new Vector3(
            Mathf.Sin(tilt) * Mathf.Cos(yaw),
            -Mathf.Cos(tilt),                     // negative => camera above the scene
            Mathf.Sin(tilt) * Mathf.Sin(yaw)
        );

        Vector3 desiredPos = focus - dir.normalized * distance;
        Quaternion desiredRot = Quaternion.LookRotation(focus - desiredPos, Vector3.up);

        curPos = Vector3.Lerp(curPos, desiredPos, 1f - Mathf.Exp(-followLerp * Time.deltaTime));
        curRot = Quaternion.Slerp(curRot, desiredRot, 1f - Mathf.Exp(-lookLerp * Time.deltaTime));

        cam.position = curPos;
        cam.rotation = curRot;
    }
}

