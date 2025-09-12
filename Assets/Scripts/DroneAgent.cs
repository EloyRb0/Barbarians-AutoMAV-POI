using System.Collections;
using UnityEngine;

public enum DroneState { Idle, Takeoff, Transit, Search, ExternalControl, RTB }

[RequireComponent(typeof(Rigidbody), typeof(LandingController))]
public class DroneAgent : MonoBehaviour
{
    [Header("Wiring")]
    public Camera payloadCam;                  // assign on prefab (child camera, tilt ~45°)
    public string droneId = "D0";

    [Header("Flight")]
    public float cruiseAltitude = 25f;         // set by coordinator
    public float speed = 6f;
    public float turnResponsiveness = 4f;
    public float verticalResponsiveness = 2f;
    public bool useGravity = false;

    [Header("Search Pattern (Spiral)")]
    public float rMin = 4f;
    public float rMax = 18f;                   // keep < ROI (20 m)
    public float radialSpeed = 1.2f;           // m/s
    public float angularSpeedDeg = 40f;        // deg/s
    public float preTakeoffClimb = 3f;

    // Runtime
    public DroneState State { get; private set; } = DroneState.Idle;
    Vector3 roiCenter;
    float roiRadius;
    Rigidbody rb;
    LandingController lander;

    // Spiral state
    float theta;   // radians
    float radius;  // meters

    // External control flag (LandingController drives motion)
    bool externalControlActive = false;

    public Rigidbody Body => rb;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        lander = GetComponent<LandingController>();

        rb.useGravity = useGravity;
        rb.linearDamping = 0.5f;
        rb.angularDamping = 2f;
        rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
    }

    void Update()
    {
        if (externalControlActive) return;       // landing or other external control
        if (State == DroneState.Search) TickSearch();
    }

    public void ArmAndSearch(Vector3 center, float radiusMeters, float startAngleDeg, float assignedAltitude)
    {
        roiCenter = center;
        roiRadius = radiusMeters;
        cruiseAltitude = assignedAltitude;

        theta = startAngleDeg * Mathf.Deg2Rad;
        radius = Mathf.Clamp(rMin, 1f, roiRadius - 2f);

        StopAllCoroutines();
        StartCoroutine(FlyProfile());
    }

    public void Halt()
{
    StopAllCoroutines();           // stop takeoff/transit/search coroutines
    rb.linearVelocity = Vector3.zero;
    State = DroneState.Idle;       // Update() won’t tick Search anymore
}

    IEnumerator FlyProfile()
    {
        State = DroneState.Takeoff;
        yield return MoveTo(new Vector3(transform.position.x, roiCenter.y + preTakeoffClimb, transform.position.z));

        State = DroneState.Transit;
        Vector3 gate = new Vector3(roiCenter.x, roiCenter.y + cruiseAltitude, roiCenter.z) + new Vector3(6f, 0f, 0f);
        yield return MoveTo(gate);

        State = DroneState.Search;
    }

    void TickSearch()
    {
        float dt = Time.deltaTime;
        theta += Mathf.Deg2Rad * angularSpeedDeg * dt;
        radius += radialSpeed * dt;
        if (radius > rMax) radius = rMin;

        Vector3 orbit = new Vector3(Mathf.Cos(theta), 0f, Mathf.Sin(theta)) * radius;
        Vector3 target = new Vector3(roiCenter.x + orbit.x, roiCenter.y + cruiseAltitude, roiCenter.z + orbit.z);
        SteerTowards(target);
    }

    IEnumerator MoveTo(Vector3 wp, float stopDistance = 0.25f, float timeout = 15f)
    {
        float t0 = Time.time;
        while ((transform.position - wp).sqrMagnitude > stopDistance * stopDistance)
        {
            if (Time.time - t0 > timeout) break;
            SteerTowards(wp);
            yield return null;
        }
    }

    void SteerTowards(Vector3 target)
    {
        Vector3 pos = transform.position;
        Vector3 to = target - pos;

        Vector3 toXZ = Vector3.ProjectOnPlane(to, Vector3.up);
        Vector3 desiredVelXZ = toXZ.normalized * speed;

        float vErr = (target.y - pos.y);
        float desiredVy = Mathf.Clamp(vErr * verticalResponsiveness, -2.0f, 2.0f);

        Vector3 desiredVel = new Vector3(desiredVelXZ.x, desiredVy, desiredVelXZ.z);
        rb.linearVelocity = Vector3.Lerp(rb.linearVelocity, desiredVel, 0.15f);

        Vector3 fwd = rb.linearVelocity; fwd.y = 0f;
        if (fwd.sqrMagnitude > 0.01f)
        {
            Quaternion look = Quaternion.LookRotation(fwd.normalized, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, look, Time.deltaTime * turnResponsiveness);
        }
    }

    // === External control for landing ===
    public void BeginLanding(Vector3 personWorldPos, float minRing = 1.6f, float maxRing = 2.0f)
    {
        externalControlActive = true;
        State = DroneState.ExternalControl;
        lander.BeginLanding(this, personWorldPos, minRing, maxRing);
    }

    public void EndExternalControl()
    {
        externalControlActive = false;
        State = DroneState.Search;  // resume search after demo landing
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(transform.position, transform.position + transform.forward * 1.5f);
    }
}

