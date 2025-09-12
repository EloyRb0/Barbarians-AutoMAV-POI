using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class LandingController : MonoBehaviour
{
    public enum LState { Idle, SelectPoint, PreHover, Descent, Touchdown, Complete, Abort }
    public LState State { get; private set; } = LState.Idle;

    [Header("Params")]
    public float preHoverHeight = 2.5f;

    // SPEEDS
    public float descentSpeed = 4.0f;   // was 0.8
    public float climbSpeed   = 2.5f;   // was 1.0
    public float lateralSpeed = 3.5f;   // was 2.5

    public float keepOutRadius = 1.0f;
    public float landTolerance = 0.06f;
    public float minSeparation = 2.5f;

    [Header("Near-ground")]
    // When altitude < nearGroundSlowdownStart, we taper descent (linear), but never below min factor
    public float nearGroundSlowdownStart = 0.8f; // meters
    [Range(0.15f, 1f)] public float nearGroundMinFactor = 0.45f; // min fraction of descentSpeed when very close

    [Header("Touchdown")]
    public bool snapToLandingPoint = true;   // snap XZ onto target when we reach ground
    public bool freezeOnTouchdown = true;    // freeze X/Z so it can’t slide away
    public float holdRadius = 0.6f;          // how close to stay during hold
    public float holdSeconds = 0.30f;        // how long to stay within holdRadius

    [Header("Ground")]
    public LayerMask groundMask;
    public float groundRayLen = 100f;

    static readonly List<LandingController> ActiveLandings = new();

    DroneAgent agent;
    Rigidbody rb;
    Transform pose; // the transform that actually moves (rb.transform if rb exists, else this.transform)

    // target info
    Vector3 personPos;
    Vector3 landingPoint;
    Vector3 preHover;
    float ringMin, ringMax;

    bool inProgress = false;

    // convenience accessors
    Vector3 PosePos
    {
        get => pose ? pose.position : transform.position;
        set
        {
            if (rb) rb.position = value;
            else if (pose) pose.position = value;
            else transform.position = value;
        }
    }
    Quaternion PoseRot
    {
        get => pose ? pose.rotation : transform.rotation;
        set
        {
            if (rb) rb.rotation = value;
            else if (pose) pose.rotation = value;
            else transform.rotation = value;
        }
    }

    public bool IsInProgress => inProgress;

    void Awake()
    {
        agent = GetComponent<DroneAgent>();
        rb = (agent && agent.Body) ? agent.Body : GetComponentInChildren<Rigidbody>();
        if (!rb) rb = GetComponent<Rigidbody>();
        pose = rb ? rb.transform : transform;
    }

    public void BeginLanding(DroneAgent owner, Vector3 personWorldPos, float ringMinMeters = 1.6f, float ringMaxMeters = 2.0f)
    {
        personPos = personWorldPos;
        ringMin = ringMinMeters;
        ringMax = ringMaxMeters;

        if (!agent) agent = owner ? owner : GetComponent<DroneAgent>();
        if (!rb) rb = (agent && agent.Body) ? agent.Body : GetComponentInChildren<Rigidbody>();
        pose = rb ? rb.transform : transform;

        // Take control from any autopilot logic
        if (agent && agent.enabled) agent.enabled = false;

        if (rb)
        {
            rb.isKinematic = false;
            rb.useGravity = false;            // vertical under our control
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.constraints = RigidbodyConstraints.None;
        }

        if (!ActiveLandings.Contains(this)) ActiveLandings.Add(this);

        inProgress = true;
        StopAllCoroutines();
        StartCoroutine(LandRoutine());
    }

    IEnumerator LandRoutine()
    {
        State = LState.SelectPoint;

        // pick ground point on ring
        landingPoint = SelectLandingPoint(personPos, ringMin, ringMax);
        if (!GroundPoint(ref landingPoint))
        {
            Debug.LogWarning($"{name}: could not find valid ground for landingPoint. Aborting.");
            yield return Abort();
            yield break;
        }

        preHover = landingPoint + Vector3.up * preHoverHeight;

        // move to pre-hover (use RB pose)
        State = LState.PreHover;
        var moved = MoveTo(preHover, 0.80f, 0.45f, -1f);
        yield return moved;
        if (State == LState.Abort || !inProgress) yield break;

        // descend
        State = LState.Descent;
        yield return DescendToGround(landingPoint);
        if (State == LState.Abort || !inProgress) yield break;

        // touchdown hold
        State = LState.Touchdown;
        float t0 = Time.time;
        while (Time.time - t0 < holdSeconds)
        {
            float dxz = Vector2.Distance(
                new Vector2(PosePos.x, PosePos.z),
                new Vector2(landingPoint.x, landingPoint.z)
            );
            if (dxz > holdRadius) t0 = Time.time; // reset if we drift (shouldn't if frozen)
            yield return null;
        }

        State = LState.Complete;
        CleanupAndRelease();
        agent?.EndExternalControl();
    }

    IEnumerator Abort()
    {
        State = LState.Abort;
        Vector3 safe = PosePos + Vector3.up * 2f;
        yield return MoveTo(safe, 0.6f, 0.4f, 5f);
        CleanupAndRelease();
        agent?.EndExternalControl();
    }

    void CleanupAndRelease()
    {
        inProgress = false;
        ActiveLandings.Remove(this);
        // If you want to take off again later, you could unfreeze here:
        // if (rb && freezeOnTouchdown) rb.constraints = RigidbodyConstraints.None;
    }

    // === Motion ===

    IEnumerator MoveTo(Vector3 wp, float stopDistXZ = 0.60f, float stopDistY = 0.35f, float timeoutSec = -1f)
    {
        Vector3 p0 = PosePos;
        float horiz = Vector2.Distance(new Vector2(p0.x, p0.z), new Vector2(wp.x, wp.z));
        float vert  = Mathf.Abs(wp.y - p0.y);
        if (timeoutSec < 0f)
        {
            float tPlanar = horiz / Mathf.Max(0.1f, lateralSpeed);
            float tVert   = vert  / Mathf.Max(0.1f, Mathf.Max(climbSpeed, descentSpeed));
            timeoutSec = Mathf.Clamp(tPlanar + tVert + 6f, 10f, 90f);
        }

        float t0 = Time.time;
        float lastY = PosePos.y;
        float lastYChangeTime = Time.time;

        for (;;)
        {
            float dxz = Vector2.Distance(new Vector2(PosePos.x, PosePos.z), new Vector2(wp.x, wp.z));
            float dy  = Mathf.Abs(wp.y - PosePos.y);

            if (dxz <= stopDistXZ && dy <= stopDistY) break;

            if (Time.time - t0 > timeoutSec)
            {
                Debug.LogWarning($"{name}: MoveTo timeout → abort. dxz={dxz:0.00} dy={dy:0.00} timeout={timeoutSec:0}");
                yield return Abort();
                yield break;
            }

            // planar steer
            Vector3 flat = new Vector3(wp.x, PosePos.y, wp.z);
            SteerPlanarWithSeparation(flat);

            // vertical steer
            float dv = wp.y - PosePos.y;
            float vy = Mathf.Clamp(dv, -descentSpeed, climbSpeed);

            // Keep it snappy except when VERY close in Y
            float absDv = Mathf.Abs(dv);
            if (absDv < 0.25f) vy *= Mathf.Clamp01(absDv / 0.25f);

            if (rb) rb.linearVelocity = new Vector3(rb.linearVelocity.x, vy, rb.linearVelocity.z);

            // stuck detection (no Y change for 0.75s while we want to move)
            float yNow = PosePos.y;
            if (Mathf.Abs(yNow - lastY) > 0.01f) lastYChangeTime = Time.time;
            else if (Time.time - lastYChangeTime > 0.75f && Mathf.Abs(vy) > 0.05f)
            {
                // gentle nudge by position towards target Y
                float step = Mathf.Sign(dv) * Mathf.Min(0.25f, Mathf.Abs(dv));
                PosePos = new Vector3(PosePos.x, PosePos.y + step, PosePos.z);
                lastYChangeTime = Time.time;
            }
            lastY = yNow;

            yield return null;
        }

        if (rb) rb.linearVelocity = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);
    }

    IEnumerator DescendToGround(Vector3 targetOnGround)
    {
        float lastY = PosePos.y;
        float lastChange = Time.time;

        for (;;)
        {
            // keep-out around person
            float horizToPerson = Vector3.Distance(
                new Vector3(PosePos.x, personPos.y, PosePos.z),
                new Vector3(personPos.x, personPos.y, personPos.z)
            );
            if (horizToPerson < keepOutRadius)
            {
                Vector3 away = (new Vector3(PosePos.x, 0, PosePos.z) - new Vector3(personPos.x, 0, personPos.z)).normalized;
                Vector3 tangent = Quaternion.Euler(0, 90, 0) * away;
                Vector3 fix = targetOnGround + away * (keepOutRadius + 0.5f) + tangent * 0.25f;
                yield return MoveTo(new Vector3(fix.x, PosePos.y + 0.1f, fix.z), 0.5f, 0.35f, 4.0f);
            }

            // planar align while descending
            Vector3 flat = new Vector3(targetOnGround.x, PosePos.y, targetOnGround.z);
            SteerPlanarWithSeparation(flat);

            // vertical step (fast until near ground, then flare)
            float vy = -descentSpeed;
            if (GetGround(out var gY))
            {
                float alt = PosePos.y - gY;
                if (alt <= landTolerance) break;

                if (alt < nearGroundSlowdownStart)
                {
                    float f = Mathf.Lerp(nearGroundMinFactor, 1f, alt / Mathf.Max(0.001f, nearGroundSlowdownStart));
                    vy = -descentSpeed * Mathf.Clamp01(f);
                }
            }
            if (rb) rb.linearVelocity = new Vector3(rb.linearVelocity.x, vy, rb.linearVelocity.z);

            // stuck detection while descending
            float yNow = PosePos.y;
            if (Mathf.Abs(yNow - lastY) > 0.01f) lastChange = Time.time;
            else if (Time.time - lastChange > 0.75f)
            {
                PosePos = new Vector3(PosePos.x, PosePos.y - 0.25f, PosePos.z);
                lastChange = Time.time;
            }
            lastY = yNow;

            yield return null;
        }

        // we consider ourselves "on ground" here. Zero vertical and stop steering.
        if (rb) rb.linearVelocity = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);

        // SNAP onto the exact ring point (XZ) and freeze if requested
        if (snapToLandingPoint && GetGround(out var gY2))
        {
            PosePos = new Vector3(targetOnGround.x, gY2, targetOnGround.z);
        }
        if (freezeOnTouchdown && rb)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.constraints = RigidbodyConstraints.FreezePositionX | RigidbodyConstraints.FreezePositionZ | RigidbodyConstraints.FreezeRotation;
        }

        // No extra MoveTo here – already on target XZ and (optionally) frozen.
    }

    void SteerPlanarWithSeparation(Vector3 target)
    {
        // Do nothing once we're finishing/finished
        if (State == LState.Touchdown || State == LState.Complete || State == LState.Abort) return;

        Vector3 pos = PosePos;
        Vector3 to = target - pos; to.y = 0f;
        Vector3 desired = to.sqrMagnitude > 0.0004f ? to.normalized * lateralSpeed : Vector3.zero;

        foreach (var other in ActiveLandings)
        {
            if (other == null || other == this) continue;
            Vector3 delta = new Vector3(pos.x - other.PosePos.x, 0, pos.z - other.PosePos.z);
            float dist = delta.magnitude;
            if (dist < minSeparation && dist > 0.001f)
                desired += delta.normalized * (minSeparation - dist);
        }

        Vector3 toPerson = new Vector3(pos.x - personPos.x, 0, pos.z - personPos.z);
        float d = toPerson.magnitude;
        if (d < keepOutRadius + 0.4f && d > 0.0001f)
            desired += toPerson.normalized * (keepOutRadius + 0.4f - d);

        Vector3 v = rb ? new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z) : Vector3.zero;
        Vector3 newPlanar = Vector3.Lerp(v, desired, 0.25f);
        if (rb) rb.linearVelocity = new Vector3(newPlanar.x, rb.linearVelocity.y, newPlanar.z);

        if (newPlanar.sqrMagnitude > 0.01f)
        {
            Quaternion look = Quaternion.LookRotation(newPlanar.normalized, Vector3.up);
            PoseRot = Quaternion.Slerp(PoseRot, look, Time.deltaTime * 3.5f);
        }
    }

    // === Geometry & ground ===

    Vector3 SelectLandingPoint(Vector3 center, float minR, float maxR, int samples = 24)
    {
        float r = 0.5f * (minR + maxR);
        float best = float.NegativeInfinity;
        Vector3 bestP = center + Vector3.right * r;

        for (int i = 0; i < samples; i++)
        {
            float a = i * Mathf.PI * 2f / samples;
            Vector3 p = new Vector3(center.x + r * Mathf.Cos(a), center.y, center.z + r * Mathf.Sin(a));

            Vector3 pre = p + Vector3.up * preHoverHeight;
            if (Physics.SphereCast(pre, 0.15f, Vector3.down, out _, preHoverHeight - 0.05f, ~0, QueryTriggerInteraction.Ignore))
                continue;

            float clearance = Vector3.Distance(new Vector3(p.x, 0, p.z), new Vector3(center.x, 0, center.z));
            float travel = Vector3.Distance(new Vector3(PosePos.x, 0, PosePos.z), new Vector3(pre.x, 0, pre.z));
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
        { p = hit.point; return true; }
        return false;
    }

    bool GetGround(out float groundY)
    {
        Vector3 top = PosePos + Vector3.up * 10f;
        if (Physics.Raycast(top, Vector3.down, out var hit, groundRayLen, groundMask, QueryTriggerInteraction.Ignore) ||
            Physics.Raycast(top, Vector3.down, out hit, groundRayLen, ~0, QueryTriggerInteraction.Ignore))
        { groundY = hit.point.y; return true; }
        groundY = 0f; return false;
    }

    void OnDrawGizmosSelected()
    {
        if (State == LState.Idle) return;
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(new Vector3(personPos.x, PosePos.y, personPos.z), keepOutRadius);
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(new Vector3(personPos.x, PosePos.y, personPos.z), 0.5f * (ringMin + ringMax));
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(landingPoint, 0.15f);
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(preHover, landingPoint);
    }
}
