using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

public static class OllamaComparer
{
    [Serializable] public class MatchObj { public bool match; public float confidence; public string reason; }
    [Serializable] class ChatReqMsg { public string role; public string content; }
    [Serializable] class ChatReq { public string model; public ChatReqMsg[] messages; public bool stream=false; public string format="json"; public int temperature=0; }
    [Serializable] class ChatRespMsg { public string role; public string content; }
    [Serializable] class ChatResp { public ChatRespMsg message; public bool done; }

    public static IEnumerator Compare(string url, string model, string mission, string candidate,
                                      Action<MatchObj> onDone, Action<string> onError = null)
    {
        if (string.IsNullOrWhiteSpace(url)) { onError?.Invoke("Ollama URL missing"); yield break; }
        if (string.IsNullOrWhiteSpace(model)) { onError?.Invoke("Ollama model missing"); yield break; }

        string sys = "Return ONLY JSON: {\"match\":bool,\"confidence\":number,\"reason\":string}. "
                   + "Be conservative: match true only if candidate clearly satisfies mission. confidence in [0,1].";
        string usr = $"mission: \"{mission}\"\ncandidate: \"{candidate}\"";

        var reqObj = new ChatReq {
            model = model,
            messages = new[]{ new ChatReqMsg{role="system",content=sys}, new ChatReqMsg{role="user",content=usr} }
        };

        var req = new UnityWebRequest(url, "POST");
        req.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(reqObj)));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success) { onError?.Invoke(req.error); yield break; }
        ChatResp env = null; try { env = JsonUtility.FromJson<ChatResp>(req.downloadHandler.text); } catch {}
        if (env?.message?.content == null) { onError?.Invoke("Empty LLM response"); yield break; }

        MatchObj verdict = null;
        try { verdict = JsonUtility.FromJson<MatchObj>(env.message.content.Trim()); }
        catch {
            // try to slice JSON
            var c = env.message.content; int i=c.IndexOf('{'); int j=c.LastIndexOf('}');
            if (i>=0 && j>i) verdict = JsonUtility.FromJson<MatchObj>(c.Substring(i, j-i+1));
        }
        if (verdict == null) { onError?.Invoke("Failed to parse JSON"); yield break; }
        onDone?.Invoke(verdict);
    }
}
