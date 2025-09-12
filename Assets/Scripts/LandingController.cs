using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class LandingController : MonoBehaviour
{
    public enum LState { Idle, SelectPoint, PreHover, Descent, Touchdown, Complete, Abort }
    public LState State { get; private set; } = LState.Idle;

    [Header("Params")]
    public float preHoverHeight = 3f;          // meters above landing point
    public float descentSpeed = 0.35f;         // m/s vertical down
    public float climbSpeed = 0.8f;            // m/s vertical up to reach prehover
    public float lateralSpeed = 3f;            // m/s while approaching
    public float keepOutRadius = 1.0f;         // cylinder radius around person
    public float landTolerance = 0.06f;        // m to ground
    public float minSeparation = 2.5f;         // inter-drone sep during landing

    [Header("Ground")]
    public LayerMask groundMask;               // set to Ground/Terrain layers in Inspector
    public float groundRayLen = 80f;

    static readonly List<LandingController> ActiveLandings = new();  // simple separation
    DroneAgent agent;
    Rigidbody rb;

    // target info
    Vector3 personPos;        // world
    Vector3 landingPoint;     // point on ring
    Vector3 preHover;         // pre-hover waypoint above landingPoint
    float ringMin, ringMax;

    bool inProgress = false;

    void Awake()
    {
        agent = GetComponent<DroneAgent>();
        rb = agent.Body; // must return a Rigidbody
        if (!rb) rb = GetComponent<Rigidbody>();
    }

    public bool IsInProgress => inProgress;

    /// <summary>Call once to start the landing on a safe ring around a person.</summary>
    public void BeginLanding(DroneAgent owner, Vector3 personWorldPos, float ringMinMeters = 1.8f, float ringMaxMeters = 2.4f)
    {
        personPos = personWorldPos;
        ringMin = ringMinMeters;
        ringMax = ringMaxMeters;

        if (!ActiveLandings.Contains(this)) ActiveLandings.Add(this);

        inProgress = true;
        StopAllCoroutines();
        StartCoroutine(LandRoutine());
    }

    IEnumerator LandRoutine()
    {
        State = LState.SelectPoint;

        // 1) pick a point on the ring (simple sampler)
        landingPoint = SelectLandingPoint(personPos, ringMin, ringMax);
        if (!GroundPoint(ref landingPoint))
        {
            Debug.LogWarning($"{name}: could not find valid ground for landingPoint. Aborting.");
            yield return Abort();
            yield break;
        }

        preHover = landingPoint + Vector3.up * preHoverHeight;

        // 2) go to prehover (now includes vertical control)
        State = LState.PreHover;
        var moved = MoveTo(preHover, 0.25f, 20f);
        yield return moved;
        if (State == LState.Abort || !inProgress) yield break; // timed out or aborted

        // 3) vertical descent with continuous checks
        State = LState.Descent;
        yield return DescendToGround(landingPoint);
        if (State == LState.Abort || !inProgress) yield break;

        // 4) touchdown window: hold near the point for ≥0.5s
        State = LState.Touchdown;
        float t0 = Time.time;
        while (Time.time - t0 < 0.5f)
        {
            Vector2 a = new Vector2(transform.position.x, transform.position.z);
            Vector2 b = new Vector2(landingPoint.x,     landingPoint.z);
            if (Vector2.Distance(a, b) > 0.25f) t0 = Time.time; // reset if we drifted
            yield return null;
        }

        // stop drift
        rb.linearVelocity = Vector3.zero;

        State = LState.Complete;
        CleanupAndRelease();
        agent.EndExternalControl();
    }

    IEnumerator Abort()
    {
        State = LState.Abort;
        // climb a bit to clear local obstacles
        Vector3 safe = transform.position + Vector3.up * 2f;
        yield return MoveTo(safe, 0.35f, 5f);
        CleanupAndRelease();
        agent.EndExternalControl();
    }

    void CleanupAndRelease()
    {
        inProgress = false;
        ActiveLandings.Remove(this);
    }

    // === Motion primitives ===

    IEnumerator MoveTo(Vector3 wp, float stopDist = 0.25f, float timeout = 15f)
    {
        float t0 = Time.time;
        while ((transform.position - wp).sqrMagnitude > stopDist * stopDist)
        {
            if (Time.time - t0 > timeout)
            {
                Debug.LogWarning($"{name}: MoveTo timeout → abort.");
                yield return Abort();
                yield break;
            }

            // Horizontal steer
            SteerPlanarWithSeparation(new Vector3(wp.x, transform.position.y, wp.z));

            // Vertical steer toward wp.y
            float dy = wp.y - transform.position.y;
            float vy = Mathf.Clamp(dy, -descentSpeed, climbSpeed); // descend slower than climb if needed
            rb.linearVelocity = new Vector3(rb.linearVelocity.x, vy, rb.linearVelocity.z);

            yield return null;
        }

        // stop residual vertical velocity
        rb.linearVelocity = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);
    }

    IEnumerator DescendToGround(Vector3 targetOnGround)
    {
        for (;;)
        {
            // keep-out around person cylinder
            float d = Vector3.Distance(
                new Vector3(transform.position.x, personPos.y, transform.position.z),
                new Vector3(personPos.x,          personPos.y, personPos.z)
            );

            if (d < keepOutRadius) // too close – move outward before descending
            {
                Vector3 away = (new Vector3(transform.position.x, 0, transform.position.z) -
                                new Vector3(personPos.x,          0, personPos.z)).normalized;
                Vector3 tangent = Quaternion.Euler(0, 90, 0) * away;
                Vector3 fix = targetOnGround + away * (keepOutRadius + 0.5f) + tangent * 0.25f;
                yield return MoveTo(new Vector3(fix.x, transform.position.y + 0.1f, fix.z), 0.18f, 3.5f);
            }

            // horizontal align over landingPoint
            Vector3 flat = new Vector3(targetOnGround.x, transform.position.y, targetOnGround.z);
            SteerPlanarWithSeparation(flat);

            // vertical step (smooth, clamp near ground)
            float vy = -descentSpeed;
            if (GetGround(out var gY))
            {
                float alt = transform.position.y - gY;
                if (alt <= landTolerance) break;
                // gently slow down when close to reduce bounce
                if (alt < 0.6f) vy *= Mathf.Clamp01(alt / 0.6f);
            }
            rb.linearVelocity = new Vector3(rb.linearVelocity.x, vy, rb.linearVelocity.z);
            yield return null;
        }

        // final settle
        rb.linearVelocity = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);
        // nudge horizontally onto exact landing point if needed
        yield return MoveTo(new Vector3(targetOnGround.x, transform.position.y, targetOnGround.z), 0.18f, 2.5f);
    }

    void SteerPlanarWithSeparation(Vector3 target)
    {
        Vector3 pos = transform.position;
        Vector3 to = target - pos; to.y = 0f;

        Vector3 desired = to.sqrMagnitude > 0.0004f ? to.normalized * lateralSpeed : Vector3.zero;

        // simple separation from other landing drones
        foreach (var other in ActiveLandings)
        {
            if (other == null || other == this) continue;
            Vector3 op = other.transform.position;
            Vector3 delta = new Vector3(pos.x - op.x, 0, pos.z - op.z);
            float dist = delta.magnitude;
            if (dist < minSeparation && dist > 0.001f)
            {
                desired += delta.normalized * (minSeparation - dist); // repulsive push
            }
        }

        // keep-out wrt person cylinder
        Vector3 toPerson = new Vector3(pos.x - personPos.x, 0, pos.z - personPos.z);
        float d = toPerson.magnitude;
        if (d < keepOutRadius + 0.4f && d > 0.0001f)
        {
            desired += toPerson.normalized * (keepOutRadius + 0.4f - d);
        }

        // apply planar velocity (smooth)
        Vector3 v = rb.linearVelocity; v.y = 0f;
        Vector3 newPlanar = Vector3.Lerp(v, desired, 0.25f);
        rb.linearVelocity = new Vector3(newPlanar.x, rb.linearVelocity.y, newPlanar.z);

        // yaw align
        if (newPlanar.sqrMagnitude > 0.01f)
        {
            Quaternion look = Quaternion.LookRotation(newPlanar.normalized, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, look, Time.deltaTime * 3.5f);
        }
    }

    // === Geometry helpers ===

    Vector3 SelectLandingPoint(Vector3 center, float minR, float maxR, int samples = 24)
    {
        // prefer mid-ring
        float r = 0.5f * (minR + maxR);
        float best = float.NegativeInfinity;
        Vector3 bestP = center + Vector3.right * r;

        for (int i = 0; i < samples; i++)
        {
            float a = i * Mathf.PI * 2f / samples;
            Vector3 p = new Vector3(center.x + r * Mathf.Cos(a), center.y, center.z + r * Mathf.Sin(a));

            // line-of-sight check from pre-hover height
            Vector3 pre = p + Vector3.up * preHoverHeight;
            if (Physics.SphereCast(pre, 0.15f, Vector3.down, out _, preHoverHeight - 0.05f, ~0, QueryTriggerInteraction.Ignore))
                continue;

            // score: prefer farther from person and closer to current pos
            float clearance = Vector3.Distance(new Vector3(p.x, 0, p.z), new Vector3(center.x, 0, center.z));
            float travel = Vector3.Distance(new Vector3(transform.position.x, 0, transform.position.z), new Vector3(pre.x, 0, pre.z));
            float score = clearance * 1.5f - travel * 0.35f;

            if (score > best) { best = score; bestP = p; }
        }
        return bestP;
    }

    bool GroundPoint(ref Vector3 p)
    {
        Vector3 top = p + Vector3.up * 10f;
        if (Physics.Raycast(top, Vector3.down, out var hit, groundRayLen, groundMask, QueryTriggerInteraction.Ignore) ||
            Physics.Raycast(top, Vector3.down, out hit, groundRayLen, ~0, QueryTriggerInteraction.Ignore))
        {
            p = hit.point;
            return true;
        }
        return false;
    }

    bool GetGround(out float groundY)
    {
        Vector3 top = transform.position + Vector3.up * 10f;
        if (Physics.Raycast(top, Vector3.down, out var hit, groundRayLen, groundMask, QueryTriggerInteraction.Ignore) ||
            Physics.Raycast(top, Vector3.down, out hit, groundRayLen, ~0, QueryTriggerInteraction.Ignore))
        {
            groundY = hit.point.y;
            return true;
        }
        groundY = 0f;
        return false;
    }

    void OnDrawGizmosSelected()
    {
        if (State == LState.Idle) return;
        // person cylinder & landing ring
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(new Vector3(personPos.x, transform.position.y, personPos.z), keepOutRadius);
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(new Vector3(personPos.x, transform.position.y, personPos.z), 0.5f * (ringMin + ringMax));
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(landingPoint, 0.15f);
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(preHover, landingPoint);
    }
}

