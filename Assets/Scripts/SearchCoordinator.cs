using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using System.Globalization;

public class SearchCoordinator : MonoBehaviour
{
    [Header("Local LLM (Ollama)")]
    public string ollamaUrl = "http://127.0.0.1:11434/api/chat";
    public string ollamaModel = "llama3.2:3b-instruct";
    [Range(0f,1f)] public float acceptConfidence = 0.70f;

    [Header("People Prefabs — one of each will spawn inside ROI")]
    public GameObject[] personPrefabs = new GameObject[6];   // assign the SIX different prefabs here

    [Header("Grounding")]
    public LayerMask groundMask; // set this in Inspector to your Ground/Terrain layer(s)

    [Header("Mission UI")]
    public InputField missionInput;   // ← drag your mission InputField here

    public string defaultMissionText = "Find an adult person in work jacket and yellow helmet";

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

    [Header("Drones config")]
    public float baseAltitude = 24f;
    public float altitudeStep = 3f;
    public float spawnRing = 6f;     // spawn drones around ROI at this radius

    [Header("UI Root")]
    public GameObject uiRoot;        // assign in Inspector
    public bool fadeOutUI = true;
    public float uiFadeDuration = 0.5f;

    LineRenderer ringLR;
    Vector3 roiCenter;

    readonly List<DroneAgent> drones = new();
    readonly List<Transform> people = new();

    // Mission lifecycle
    bool landingInProgress = false;      // ensure only one landing at a time
    bool missionEnded = false;           // once true, no more LLM calls or motion on other drones
    DroneAgent winnerDrone = null;       // the drone chosen to land

    // LLM evaluation bookkeeping
    readonly HashSet<int> _evaluatingPeople = new();
    string _currentMissionText = "";

    public string GetMissionText() => _currentMissionText;

    public List<Vector3> GetDronePositions()
    {
        List<Vector3> positions = new();
        foreach (var d in drones)
            if (d != null) positions.Add(d.transform.position);
        return positions;
    }

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
#if ENABLE_INPUT_SYSTEM
        if (UnityEngine.InputSystem.Keyboard.current != null &&
            UnityEngine.InputSystem.Keyboard.current.lKey.wasPressedThisFrame)
        {
            TryLandingDemo();
        }
#else
        if (Input.GetKeyDown(KeyCode.L))
        {
            TryLandingDemo();
        }
#endif
    }

    // === Proximity callback from PersonDescriptor / DroneProximitySensor ===
    public void OnPersonProximity(DroneAgent drone, PersonDescriptor person)
    {
        if (missionEnded || landingInProgress || drone == null || person == null) return;
        int id = person.GetInstanceID();
        if (_evaluatingPeople.Contains(id)) return;
        _evaluatingPeople.Add(id);
        StartCoroutine(EvaluateAndMaybeLand(drone, person));
    }

    System.Collections.IEnumerator EvaluateAndMaybeLand(DroneAgent drone, PersonDescriptor person)
    {
        bool done = false; OllamaComparer.MatchObj verdict = null; string err = null;

        yield return OllamaComparer.Compare(
            ollamaUrl, ollamaModel,
            _currentMissionText, person.description,
            v => { verdict = v; done = true; },
            e => { err = e; done = true; },
            true // verbose logging ON
        );

        _evaluatingPeople.Remove(person.GetInstanceID());

        // If mission already ended while we were waiting, ignore results unless we're the winner
        if (missionEnded && drone != winnerDrone) yield break;

        if (!done || verdict == null)
        {
            if (!string.IsNullOrEmpty(err)) Debug.LogWarning(err);
            yield break;
        }

        Debug.Log($"[LLM] match={verdict.match} conf={verdict.confidence:0.00} person={person.name} reason={verdict.reason}");

        if (verdict.match && verdict.confidence >= acceptConfidence && !landingInProgress)
        {
            // Commit the mission to this drone and stop the rest.
            missionEnded = true;
            winnerDrone = drone;
            StopOtherDronesAndSensors(winnerDrone);

            landingInProgress = true;
            var lander = drone.GetComponent<LandingController>();
            if (lander != null)
            {
                StartCoroutine(LandingLock(drone)); // wait for touchdown
                drone.BeginLanding(person.transform.position, 1.6f, 2.0f); // safe ring landing
            }
            else
            {
                landingInProgress = false; // couldn't land, allow future attempts if needed
            }
        }
    }

    void StopOtherDronesAndSensors(DroneAgent winner)
    {
        // Hide the 20 m ring once we commit to a target
        if (ringLR) ringLR.enabled = false;

        foreach (var d in drones)
        {
            if (d == null) continue;

            // Stop proximity sensors (so no new LLM requests are triggered)
            var sensor = d.GetComponent<DroneProximitySensor>();
            if (sensor) sensor.enabled = false;

            // Freeze movement on non-winner drones
            if (d != winner)
            {
                // stop any running coroutines inside the drone
                d.StopAllCoroutines();

                // disable drone logic updates
                d.enabled = false;

                // zero velocities to hover/stop
                var rb = d.GetComponent<Rigidbody>();
                if (rb)
                {
                    rb.linearVelocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }

                // (optional) disable their colliders entirely:
                // foreach (var col in d.GetComponentsInChildren<Collider>()) col.enabled = false;
            }
        }
    }

    public void OnStartClicked()
    {
        if (!TryParseInputs(out roiCenter))
        {
            Debug.LogWarning("Invalid coordinates. Please enter numeric X/Y/Z.");
            return;
        }

        // Reset mission state
        missionEnded = false;
        landingInProgress = false;
        winnerDrone = null;
        _evaluatingPeople.Clear();

        _currentMissionText = (missionInput != null && !string.IsNullOrWhiteSpace(missionInput.text))
        ? missionInput.text.Trim()
        : defaultMissionText;

        ringLR.enabled = true;
        UpdateRing(roiCenter, roiRadiusMeters);

        // Clean previous spawns if any
        CleanupSpawns();

        HideUI();

        // Spawn people (at ground)
        SpawnAllPeopleOnce();

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

    public Vector3 GetROICenter() => roiCenter;
    public DroneAgent GetWinnerDrone() => winnerDrone;   // null until matched
    public bool HasWinner => winnerDrone != null;

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

    bool TryParseInputs(out Vector3 center)
    {
        center = Vector3.zero;

        if (xInput == null || yInput == null || zInput == null)
        {
            Debug.LogError("SearchCoordinator: assign xInput, yInput, zInput in the Inspector.");
            return false;
        }

        // Normalize: trim and accept comma or dot decimals
        string xs = NormalizeNum(xInput.text);
        string ys = NormalizeNum(yInput.text);
        string zs = NormalizeNum(zInput.text);

        bool okX = float.TryParse(xs, NumberStyles.Float, CultureInfo.InvariantCulture, out float x);
        bool okY = float.TryParse(ys, NumberStyles.Float, CultureInfo.InvariantCulture, out float y);
        bool okZ = float.TryParse(zs, NumberStyles.Float, CultureInfo.InvariantCulture, out float z);

        if (!(okX && okY && okZ))
        {
            Debug.LogWarning($"Invalid coordinates. Please enter numeric X/Y/Z. Got X='{xInput.text}', Y='{yInput.text}', Z='{zInput.text}'");
            return false;
        }

        center = new Vector3(x, y, z);
        return true;

        static string NormalizeNum(string s)
            => string.IsNullOrWhiteSpace(s) ? "" : s.Trim().Replace(',', '.');
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

    void HideUI()
    {
        if (!uiRoot) return;

        // If we can fade, do it; otherwise just deactivate
        if (fadeOutUI && uiRoot.TryGetComponent<CanvasGroup>(out var cg))
            StartCoroutine(FadeOutCanvasGroup(cg, uiFadeDuration, deactivate:true));
        else
            uiRoot.SetActive(false);
    }

    public void ShowUI() // optional, if you ever need to bring it back
    {
        if (!uiRoot) return;
        uiRoot.SetActive(true);
        if (uiRoot.TryGetComponent<CanvasGroup>(out var cg))
        {
            cg.alpha = 1f;
            cg.interactable = true;
            cg.blocksRaycasts = true;
        }
    }

    System.Collections.IEnumerator FadeOutCanvasGroup(CanvasGroup cg, float dur, bool deactivate)
    {
        // Non-blocking fade that doesn’t pause the sim (uses unscaled time)
        float a0 = cg.alpha;
        cg.interactable = false;
        cg.blocksRaycasts = false;

        float t = 0f;
        while (t < dur)
        {
            t += Time.unscaledDeltaTime;
            cg.alpha = Mathf.Lerp(a0, 0f, Mathf.Clamp01(t / dur));
            yield return null;
        }
        cg.alpha = 0f;
        if (deactivate) cg.gameObject.SetActive(false);
    }

    // === DEMO landing trigger ===
    void TryLandingDemo()
    {
        if (landingInProgress || missionEnded) return;
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

        missionEnded = true;         // stop further evaluations
        winnerDrone = chosen;
        StopOtherDronesAndSensors(winnerDrone);

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
        Debug.Log($"[Mission] Complete: {d.name} landed safely near the target.");
        // keep missionEnded = true so nothing restarts
    }

    void SpawnAllPeopleOnce()
    {
        // Clean previous spawns
        foreach (var p in people) if (p != null) Destroy(p.gameObject);
        people.Clear();

        if (personPrefabs == null || personPrefabs.Length == 0)
        {
            Debug.LogError("personPrefabs is empty. Assign the six prefabs in the Inspector.");
            return;
        }

        // Instantiate ONCE per unique prefab
        var used = new HashSet<GameObject>();
        int spawned = 0;

        foreach (var prefab in personPrefabs)
        {
            if (prefab == null) { Debug.LogWarning("Null entry in personPrefabs; skipping."); continue; }
            if (!used.Add(prefab)) { Debug.LogWarning($"Duplicate prefab reference '{prefab.name}' skipped."); continue; }

            Vector3 pos = RandomPointInCircleOnGround(roiCenter, roiRadiusMeters - 2f);
            var go = Instantiate(prefab, pos, Quaternion.identity);
            go.name = prefab.name;

            go.transform.rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f); // optional random facing
            PlaceOnGround(go.transform); // ensure feet on ground regardless of pivot

            // Safety net: ensure PersonDescriptor + Trigger
            var pd = go.GetComponent<PersonDescriptor>() ?? go.AddComponent<PersonDescriptor>();
            if (pd.triggerRadius < 1f) pd.triggerRadius = 5f;
            var sc = go.GetComponent<SphereCollider>() ?? go.AddComponent<SphereCollider>();
            sc.isTrigger = true;
            sc.radius = pd.triggerRadius;

            people.Add(go.transform);
            spawned++;
        }

        Debug.Log($"SpawnAllPeopleOnce(): spawned {spawned} unique people.");
    }

    Vector3 RandomPointInCircleOnGround(Vector3 center, float radius)
    {
        // Uniform point in disc
        float t = 2f * Mathf.PI * Random.value;
        float u = Random.value + Random.value;
        float r = (u > 1f ? 2f - u : u) * Mathf.Max(0.1f, radius);

        Vector3 probe = new Vector3(center.x + r * Mathf.Cos(t), center.y + 50f, center.z + r * Mathf.Sin(t));

        // Raycast to GROUND ONLY (ignore triggers)
        if (Physics.Raycast(probe, Vector3.down, out var hit, 300f, groundMask, QueryTriggerInteraction.Ignore))
            return hit.point;

        // Fallback if mask not set: try without mask
        if (Physics.Raycast(probe, Vector3.down, out hit, 300f, ~0, QueryTriggerInteraction.Ignore))
            return hit.point;

        return new Vector3(probe.x, center.y, probe.z);
    }

    /// <summary>Moves a spawned object so its renderer-bounds bottom sits exactly on the ground under it.</summary>
    void PlaceOnGround(Transform t)
    {
        // Compute combined bounds (MeshRenderer + SkinnedMeshRenderer)
        var rends = t.GetComponentsInChildren<Renderer>();
        if (rends == null || rends.Length == 0) return;

        Bounds b = rends[0].bounds;
        for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);

        // Raycast straight down from above the model to GROUND ONLY
        Vector3 rayStart = new Vector3(b.center.x, b.max.y + 5f, b.center.z);

        if (!Physics.Raycast(rayStart, Vector3.down, out var hit, 500f, groundMask, QueryTriggerInteraction.Ignore))
        {
            // Fallback without mask
            if (!Physics.Raycast(rayStart, Vector3.down, out hit, 500f, ~0, QueryTriggerInteraction.Ignore))
                return;
        }

        float deltaY = hit.point.y - b.min.y;  // how much to move so bottom touches ground
        t.position += new Vector3(0f, deltaY, 0f);
    }
}

