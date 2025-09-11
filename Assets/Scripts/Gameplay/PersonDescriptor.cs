using UnityEngine;

[RequireComponent(typeof(SphereCollider))]
public class PersonDescriptor : MonoBehaviour
{
    [Header("Ground-truth description for this person")]
    [TextArea(2, 6)] public string description = "adult person in casual clothing";

    // >>> Add this field so Editor tools can write tags <<<
    public string[] tags = System.Array.Empty<string>();

    [Header("Trigger")]
    public float triggerRadius = 100f;

    void Reset()
    {
        var sc = GetComponent<SphereCollider>() ?? gameObject.AddComponent<SphereCollider>();
        sc.isTrigger = true;
        sc.radius = triggerRadius;
    }

    void OnValidate()
    {
        var sc = GetComponent<SphereCollider>();
        if (sc != null)
        {
            sc.isTrigger = true;
            sc.radius = triggerRadius;
        }
    }

    void OnTriggerEnter(Collider other)
    {
        var drone = other.GetComponentInParent<DroneAgent>() ?? other.GetComponent<DroneAgent>();
        if (drone != null)
            SearchCoordinator.Instance?.OnPersonProximity(drone, this);
    }
}
