using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class LandingController : MonoBehaviour
{
    public enum LState { Idle, SelectPoint, PreHover, Descent, Touchdown, Complete, Abort }
    public LState State { get; private set; } = LState.Idle;

    [Header("Params")]
    public float preHoverHeight = 3f;          // meters above landing point
    public float descentSpeed = 0.25f;         // m/s vertical
    public float lateralSpeed = 3f;            // m/s while approaching
    public float keepOutRadius = 1.2f;         // cylinder around person
    public float landTolerance = 0.05f;        // m to ground
    public float minSeparation = 2.5f;         // inter-drone sep during landing

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
        rb = agent.Body;
    }

    public bool IsInProgress => inProgress;

    public void BeginLanding(DroneAgent owner, Vector3 personWorldPos, float ringMinMeters = 1.6f, float ringMaxMeters = 2.0f)
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

        // 2) go to prehover
        State = LState.PreHover;
        yield return MoveTo(preHover, 0.2f, 20f);

        // 3) vertical descent with continuous checks
        State = LState.Descent;
        yield return DescendToGround(landingPoint);

        // 4) touchdown check
        State = LState.Touchdown;
        // simple touchdown: close to ground and near landingPoint for ≥ 0.5 s
        float t0 = Time.time;
        while (Time.time - t0 < 0.5f)
        {
            if (Vector3.Distance(new Vector3(transform.position.x, 0, transform.position.z),
                                 new Vector3(landingPoint.x, 0, landingPoint.z)) > 0.2f)
            { t0 = Time.time; }
            yield return null;
        }

        State = LState.Complete;
        CleanupAndRelease();
        agent.EndExternalControl();
    }

    IEnumerator Abort()
    {
        State = LState.Abort;
        // climb a bit
        Vector3 safe = transform.position + Vector3.up * 2f;
        yield return MoveTo(safe, 0.2f, 5f);
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
            if (Time.time - t0 > timeout) yield break;
            SteerPlanarWithSeparation(wp);
            yield return null;
        }
    }

    IEnumerator DescendToGround(Vector3 targetOnGround)
    {
        // keep horizontal over landingPoint, go down smoothly
        for (;;)
        {
            // keep-out around person cylinder
            float d = Vector3.Distance(new Vector3(transform.position.x, personPos.y, transform.position.z),
                                       new Vector3(personPos.x, personPos.y, personPos.z));
            if (d < keepOutRadius) // too close – move outward before descending
            {
                Vector3 away = (new Vector3(transform.position.x, 0, transform.position.z) -
                                new Vector3(personPos.x, 0, personPos.z)).normalized;
                Vector3 tangent = Quaternion.Euler(0, 90, 0) * away;
                Vector3 fix = targetOnGround + away * (keepOutRadius + 0.4f) + tangent * 0.2f;
                yield return MoveTo(new Vector3(fix.x, transform.position.y, fix.z), 0.15f, 3f);
            }

            // horizontal align over landingPoint
            Vector3 flat = new Vector3(targetOnGround.x, transform.position.y, targetOnGround.z);
            SteerPlanarWithSeparation(flat);

            // vertical step
            float vy = -descentSpeed;
            rb.linearVelocity = new Vector3(rb.linearVelocity.x, vy, rb.linearVelocity.z);

            // ground proximity
            if (GetGround(out var gY))
            {
                float alt = transform.position.y - gY;
                if (alt <= landTolerance) break;
            }
            yield return null;
        }
    }

    void SteerPlanarWithSeparation(Vector3 target)
    {
        // planar seek
        Vector3 pos = transform.position;
        Vector3 to = target - pos;
        to.y = 0f;

        Vector3 desired = to.normalized * lateralSpeed;

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

        // apply velocity
        Vector3 v = rb.linearVelocity; v.y = 0f;
        Vector3 newPlanar = Vector3.Lerp(v, desired, 0.2f);
        rb.linearVelocity = new Vector3(newPlanar.x, rb.linearVelocity.y, newPlanar.z);

        // yaw align
        if (newPlanar.sqrMagnitude > 0.01f)
        {
            Quaternion look = Quaternion.LookRotation(newPlanar.normalized, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, look, Time.deltaTime * 3.5f);
        }
    }

    // === Geometry helpers ===

    Vector3 SelectLandingPoint(Vector3 center, float minR, float maxR, int samples = 16)
    {
        float r = 0.5f * (minR + maxR);
        float best = float.NegativeInfinity;
        Vector3 bestP = center + Vector3.right * r;

        for (int i = 0; i < samples; i++)
        {
            float a = i * Mathf.PI * 2f / samples;
            Vector3 p = new Vector3(center.x + r * Mathf.Cos(a), center.y, center.z + r * Mathf.Sin(a));

            // line-of-sight check from 3m above point (simple)
            Vector3 pre = p + Vector3.up * preHoverHeight;
            if (Physics.SphereCast(pre, 0.15f, Vector3.down, out _, preHoverHeight - 0.05f)) continue;

            // score: prefer farther from person and closer to current pos
            float clearance = Vector3.Distance(new Vector3(p.x, 0, p.z), new Vector3(center.x, 0, center.z));
            float travel = Vector3.Distance(new Vector3(transform.position.x, 0, transform.position.z), new Vector3(pre.x, 0, pre.z));
            float score = clearance * 1.5f - travel * 0.3f;

            if (score > best) { best = score; bestP = p; }
        }
        return bestP;
    }

    bool GroundPoint(ref Vector3 p)
    {
        Vector3 top = p + Vector3.up * 10f;
        if (Physics.Raycast(top, Vector3.down, out var hit, 50f))
        {
            p = hit.point;
            return true;
        }
        return false;
    }

    bool GetGround(out float groundY)
    {
        Vector3 top = transform.position + Vector3.up * 10f;
        if (Physics.Raycast(top, Vector3.down, out var hit, 50f))
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
