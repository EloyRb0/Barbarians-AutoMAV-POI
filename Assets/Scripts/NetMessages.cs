using System;
using UnityEngine;

[Serializable]
public struct MissionConfig
{
    public Vector3 roiCenter;
    public float roiRadiusMeters;
    public string textDescription; // optional future use
}

[Serializable]
public struct InferenceRequest
{
    public string droneId;
    public byte[] frameJpg;
    public string queryText;
}

[Serializable]
public struct CandidateDetection
{
    public string droneId;
    public Rect bbox;           // screen or image-space rect of the person
    public float clipScore;     // fast gate score
    public float jointScore;    // fused score (if using open-voc/pose later)
    public Vector3 worldHint;   // raycasted ground anchor (optional)
}
