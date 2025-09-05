using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;


public class SearchCoordinator : MonoBehaviour
{
    public static SearchCoordinator Instance { get; private set; }

    [Header("UI (legacy)")]
    public InputField xInput;
    public InputField yInput;
    public InputField zInput;
    public Button startButton;

    [Header("Mission")]
    public float roiRadiusMeters = 20f;
    public Color ringColor = new Color(0f, 1f, 0f, 0.5f);
    public int ringSegments = 128;

    [Header("Spawning")]
    public GameObject dronePrefab;   // assign in Inspector
    public int numDrones = 3;
    public GameObject personPrefab;  // assign in Inspector
    public int numPeople = 3;

    [Header("Drones config")]
    public float baseAltitude = 24f;
    public float altitudeStep = 3f;
    public float spawnRing = 6f;     // spawn drones around ROI at this radius

    LineRenderer ringLR;
    Vector3 roiCenter;

    readonly List<DroneAgent> drones = new();
    readonly List<Transform> people = new();

    // Landing lock: ensure only one drone is landing at a time
    bool landingInProgress = false;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        ringLR = gameObject.AddComponent<LineRenderer>();
        ringLR.useWorldSpace = true;
        ringLR.loop = true;
        ringLR.positionCount = ringSegments;
        ringLR.widthMultiplier = 0.08f;
        ringLR.material = new Material(Shader.Find("Unlit/Color"));
        ringLR.material.color = ringColor;
        ringLR.enabled = false;
    }

    void Start()
    {
        if (startButton != null)
            startButton.onClick.AddListener(OnStartClicked);
    }

    void OnDestroy()
    {
        if (startButton != null)
            startButton.onClick.RemoveListener(OnStartClicked);
    }

    void Update()
    {
        // DEMO: press L to trigger a landing onto the nearest person
        if (Keyboard.current.lKey.wasPressedThisFrame)
        {
            TryLandingDemo();
        }
    }

    public void OnStartClicked()
    {
        if (!TryParseInputs(out roiCenter))
        {
            Debug.LogWarning("Invalid coordinates. Please enter numeric X/Y/Z.");
            return;
        }

        ringLR.enabled = true;
        UpdateRing(roiCenter, roiRadiusMeters);

        // Clean previous spawns if any
        CleanupSpawns();

        // Spawn people (at ground)
        for (int i = 0; i < numPeople; i++) SpawnOnePerson(i);

        // Spawn drones on a ring around ROI
        for (int i = 0; i < numDrones; i++) SpawnOneDrone(i);

        // Arm and search
        for (int i = 0; i < drones.Count; i++)
        {
            float alt = baseAltitude + altitudeStep * i;
            float startAngle = (360f / Mathf.Max(1, drones.Count)) * i;
            drones[i].ArmAndSearch(roiCenter, roiRadiusMeters, startAngle, alt);
        }
    }

    void CleanupSpawns()
    {
        foreach (var d in drones) if (d != null) Destroy(d.gameObject);
        drones.Clear();
        foreach (var p in people) if (p != null) Destroy(p.gameObject);
        people.Clear();
    }

    void SpawnOneDrone(int idx)
    {
        if (dronePrefab == null) { Debug.LogError("Assign dronePrefab in Inspector."); return; }
        float a = idx * Mathf.PI * 2f / Mathf.Max(1, numDrones);
        Vector3 spawn = new Vector3(
            roiCenter.x + spawnRing * Mathf.Cos(a),
            roiCenter.y + 0.5f,
            roiCenter.z + spawnRing * Mathf.Sin(a)
        );
        var go = Instantiate(dronePrefab, spawn, Quaternion.identity);
        go.name = $"Drone_{idx:D2}";
        var agent = go.GetComponent<DroneAgent>();
        if (agent == null) { Debug.LogError("Drone prefab must have a DroneAgent component."); return; }
        agent.droneId = go.name;
        drones.Add(agent);
    }

    void SpawnOnePerson(int idx)
    {
        if (personPrefab == null) { Debug.LogError("Assign personPrefab in Inspector."); return; }
        // random point inside ROI (leave margin from edges)
        float r = Random.Range(2f, roiRadiusMeters - 3f);
        float a = Random.Range(0f, Mathf.PI * 2f);
        Vector3 p = new Vector3(roiCenter.x + r * Mathf.Cos(a), roiCenter.y + 10f, roiCenter.z + r * Mathf.Sin(a));
        // place on ground
        if (Physics.Raycast(p, Vector3.down, out var hit, 100f))
        {
            p = hit.point;
        }
        else
        {
            p.y = roiCenter.y; // fallback
        }
        var go = Instantiate(personPrefab, p, Quaternion.identity);
        go.name = $"Person_{idx:D2}";
        people.Add(go.transform);
    }

    bool TryParseInputs(out Vector3 center)
    {
        center = Vector3.zero;
        if (!float.TryParse(xInput?.text, out float x)) return false;
        if (!float.TryParse(yInput?.text, out float y)) return false;
        if (!float.TryParse(zInput?.text, out float z)) return false;
        center = new Vector3(x, y, z);
        return true;
    }

    void UpdateRing(Vector3 center, float radius)
    {
        if (ringLR == null) return;
        if (ringLR.positionCount != ringSegments) ringLR.positionCount = ringSegments;

        float step = Mathf.PI * 2f / (ringSegments);
        float y = center.y + 0.05f;
        for (int i = 0; i < ringSegments; i++)
        {
            float a = step * i;
            float cx = center.x + radius * Mathf.Cos(a);
            float cz = center.z + radius * Mathf.Sin(a);
            ringLR.SetPosition(i, new Vector3(cx, y, cz));
        }
    }

    // === DEMO landing trigger ===
    void TryLandingDemo()
    {
        if (landingInProgress) return;
        if (drones.Count == 0 || people.Count == 0) return;

        // pick the nearest person to ROI center (demo policy)
        Transform target = null;
        float best = float.PositiveInfinity;
        foreach (var p in people)
        {
            float d = Vector3.Distance(new Vector3(p.position.x, 0, p.position.z), new Vector3(roiCenter.x, 0, roiCenter.z));
            if (d < best) { best = d; target = p; }
        }
        if (target == null) return;

        // pick first drone (or choose best by distance); enforce single-landing lock
        DroneAgent chosen = drones[0];
        landingInProgress = true;

        // subscribe to landing completion via coroutine
        StartCoroutine(LandingLock(chosen));
        chosen.BeginLanding(target.position, 1.6f, 2.0f);
    }

    System.Collections.IEnumerator LandingLock(DroneAgent d)
    {
        // wait until its LandingController reports completion
        var lander = d.GetComponent<LandingController>();
        while (lander != null && lander.IsInProgress)
            yield return null;
        landingInProgress = false;
    }
}
