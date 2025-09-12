using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(DroneAgent)), RequireComponent(typeof(Rigidbody))]
public class DroneProximitySensor : MonoBehaviour
{
    [Header("Sensor shape")]
    [Tooltip("Horizontal detection radius on XZ (meters).")]
    public float horizontalRadius = 6f;
    [Tooltip("Extra down reach (meters) below ground just in case.")]
    public float extraDownreach = 0.5f;

    [Header("Layers")]
    [Tooltip("Layers considered 'ground' for altitude raycasts.")]
    public LayerMask groundMask; // set this in Inspector (same as in SearchCoordinator)

    [Header("Logging")]
    public bool verboseLogs = true;

    CapsuleCollider sensor;     // trigger collider
    DroneAgent agent;
    Rigidbody rb;

    // simple cooldown so we don't spam the same person
    readonly HashSet<int> recent = new HashSet<int>();
    float cooldown = 1.0f; // seconds
    readonly Dictionary<int, float> seenAt = new Dictionary<int, float>();

    void Awake()
    {
        agent = GetComponent<DroneAgent>();
        rb = GetComponent<Rigidbody>();
        sensor = GetComponent<CapsuleCollider>();
        if (sensor == null) sensor = gameObject.AddComponent<CapsuleCollider>();
        sensor.isTrigger = true;
        sensor.direction = 1; // Y axis
        sensor.radius = horizontalRadius;
        sensor.height = 2f;   // will be set correctly in Update
        // Rigidbody already on Drone; triggers require at least one Rigidbody in the pair.
    }

    void Update()
    {
        // Raycast to ground to compute current altitude
        float groundY;
        if (!GetGround(out groundY))
        {
            // fallback: assume y=0 ground
            groundY = 0f;
        }
        float altitude = Mathf.Max(0f, transform.position.y - groundY);

        // Make the capsule extend DOWN to ground (and a bit beyond)
        float h = Mathf.Max(2f, altitude + extraDownreach);
        sensor.height = h;
        sensor.radius = horizontalRadius;

        // center so the TOP of the capsule is at drone center, bottom at (droneY - h)
        sensor.center = new Vector3(0f, -h * 0.5f, 0f);

        // cleanup cooldown
        if (seenAt.Count > 0)
        {
            var now = Time.time;
            var tmp = new List<int>();
            foreach (var kv in seenAt) if (now - kv.Value > cooldown) tmp.Add(kv.Key);
            foreach (var id in tmp) { seenAt.Remove(id); recent.Remove(id); }
        }
    }

    bool GetGround(out float groundY)
    {
        Vector3 top = transform.position + Vector3.up * 5f;
        // try groundMask first
        if (groundMask.value != 0 &&
            Physics.Raycast(top, Vector3.down, out var hit, 200f, groundMask, QueryTriggerInteraction.Ignore))
        { groundY = hit.point.y; return true; }

        // fallback: any collider
        if (Physics.Raycast(top, Vector3.down, out hit, 200f, ~0, QueryTriggerInteraction.Ignore))
        { groundY = hit.point.y; return true; }

        groundY = 0f; return false;
    }

    void OnTriggerEnter(Collider other)
    {
        var person = other.GetComponentInParent<PersonDescriptor>() ?? other.GetComponent<PersonDescriptor>();
        if (person == null) return;

        int id = person.GetInstanceID();
        if (recent.Contains(id)) return;
        recent.Add(id); seenAt[id] = Time.time;

        if (verboseLogs)
        {
            float gy; GetGround(out gy);
            float altitude = transform.position.y - gy;
            Debug.Log($"[Sensor] {agent.droneId} detected {person.name} | horizR={horizontalRadius:0.0}m | alt≈{altitude:0.0}m");
            Debug.Log($"[Sensor ▶] mission vs candidate → mission='{SearchCoordinator.Instance?.GetMissionText()}' candidate='{person.description}'");
        }

        // forward to your existing flow
        SearchCoordinator.Instance?.OnPersonProximity(agent, person);
    }
}
