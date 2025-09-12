using UnityEngine;
using UnityEngine.UI;

public class MissionHUD : MonoBehaviour
{
    public static MissionHUD Instance { get; private set; }
    public Text label;

    void Awake()
    {
        Instance = this;
        if (!label) label = GetComponentInChildren<Text>(true);
        if (label) label.text = "State: Idle";
    }

    public void Set(string text)
    {
        if (label) label.text = text;
    }

    public static void UpdateStatus(string text)
    {
        if (Instance) Instance.Set(text);
    }
}
