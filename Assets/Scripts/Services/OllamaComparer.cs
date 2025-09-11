using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace AutoMAV.Vision
{
    public static class OllamaLLMComparer
    {
        [Serializable] public class MatchObj { public bool match; public float confidence; public string reason; }

        [Serializable] class ChatReqMsg { public string role; public string content; }
        [Serializable] class ChatReq
        {
            public string model;
            public ChatReqMsg[] messages;
            public bool stream = false;
            public string format = "json";
            public int temperature = 0;
        }
        [Serializable] class ChatRespMsg { public string role; public string content; }
        [Serializable] class ChatResp { public ChatRespMsg message; public bool done; }

        /// <summary>Calls Ollama /api/chat and expects JSON: {"match":bool,"confidence":0..1,"reason":string}</summary>
        public static IEnumerator Compare(
            string url, string model, string mission, string candidate,
            Action<MatchObj> onDone, Action<string> onError = null, bool verbose = true)
        {
            if (string.IsNullOrWhiteSpace(url)) { onError?.Invoke("Ollama URL missing"); yield break; }
            if (string.IsNullOrWhiteSpace(model)) { onError?.Invoke("Ollama model missing"); yield break; }

            string sys = "Return ONLY JSON: {\"match\":bool,\"confidence\":number,\"reason\":string}. " +
                         "Be conservative: match true only if candidate clearly satisfies mission. " +
                         "confidence must be in [0,1]. No extra text or code fences.";
            string usr = $"mission: \"{mission}\"\ncandidate: \"{candidate}\"";

            var reqObj = new ChatReq {
                model = model,
                messages = new [] {
                    new ChatReqMsg{ role="system", content=sys },
                    new ChatReqMsg{ role="user",   content=usr }
                }
            };

            var req = new UnityWebRequest(url, "POST");
            req.uploadHandler   = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(reqObj)));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");

            if (verbose)
                Debug.Log($"[Ollama ▶] model={model}\n--- mission ---\n{mission}\n--- candidate ---\n{candidate}");

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                onError?.Invoke($"Ollama error: {req.error}");
                if (verbose) Debug.LogWarning($"[Ollama ◀] HTTP error: {req.error}\nBody: {req.downloadHandler?.text}");
                yield break;
            }

            string raw = req.downloadHandler.text;
            if (verbose) Debug.Log($"[Ollama ◀] raw: {raw}");

            ChatResp env = null;
            try { env = JsonUtility.FromJson<ChatResp>(raw); }
            catch { onError?.Invoke("Failed to parse Ollama envelope."); yield break; }

            var content = env?.message?.content?.Trim();
            if (verbose) Debug.Log($"[Ollama ◀] message.content: {content}");

            MatchObj verdict = null;
            try { verdict = JsonUtility.FromJson<MatchObj>(content); }
            catch
            {
                if (!string.IsNullOrEmpty(content))
                {
                    int i = content.IndexOf('{'); int j = content.LastIndexOf('}');
                    if (i >= 0 && j > i)
                    {
                        var slice = content.Substring(i, j - i + 1);
                        try { verdict = JsonUtility.FromJson<MatchObj>(slice); } catch {}
                    }
                }
            }

            if (verdict == null) { onError?.Invoke("Could not parse JSON verdict."); yield break; }
            if (verbose) Debug.Log($"[Ollama ✔] match={verdict.match} conf={verdict.confidence:0.00} reason={verdict.reason}");
            onDone?.Invoke(verdict);
        }
    }
}
