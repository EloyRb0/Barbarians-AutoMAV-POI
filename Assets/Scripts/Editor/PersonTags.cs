#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System.Collections.Generic;

public static class ApplyPersonTags
{
    class Profile
    {
        public string nameMatch; public string desc;
        public string[] tags; public float triggerRadius = 5f;
    }

    static readonly List<Profile> PROFILES = new()
    {
        new Profile{
            nameMatch="mujerpeliroja", 
            desc="Adult woman with red hair, casual outfit. Distinctive red hair is the key identifier.\nES: Mujer adulta con cabello pelirrojo, ropa casual.",
            tags=new[]{"woman","female","red hair","pelirroja","casual","standing","person"}
        },
        new Profile{
            nameMatch="trabajador",
            desc="Construction worker wearing a yellow hard hat and high-visibility vest; work attire.\nES: Trabajador de construcción con casco amarillo y chaleco reflejante.",
            tags=new[]{"worker","construction","hard hat","helmet","yellow helmet","hi-vis vest","reflective vest","PPE","safety","standing","person"}
        },
        new Profile{
            nameMatch="maletinchavo",
            desc="Young man carrying a briefcase/suitcase; semi-formal clothing.\nES: Joven con maletín/maleta; ropa semiformal.",
            tags=new[]{"man","male","briefcase","suitcase","bag","business","semi-formal","standing","carrying","person"}
        },
        new Profile{
            nameMatch="negro",
            desc="Person wearing an all-black outfit (dark top and dark pants).\nES: Persona con ropa negra (parte superior y pantalón oscuros).",
            tags=new[]{"person","black outfit","black top","black pants","dark clothing","standing"}
        },
        new Profile{
            nameMatch="personita1",
            desc="Generic adult person in casual clothing.\nES: Persona adulta genérica con ropa casual.",
            tags=new[]{"person","adult","casual","standing"}
        },
        new Profile{
            nameMatch="mujervestidochivas",
            desc="Woman wearing a red-and-white striped soccer jersey (Chivas style).\nES: Mujer con jersey de futbol rayado rojo y blanco (estilo Chivas).",
            tags=new[]{"woman","female","soccer jersey","red and white stripes","chivas","sportswear","shirt","standing","person"}
        },
    };

    [MenuItem("Tools/Apply Person Tags")]
    public static void Apply()
    {
        var objs = Selection.gameObjects;
        if (objs == null || objs.Length == 0)
        {
            EditorUtility.DisplayDialog("Apply Person Tags", "Select one or more prefabs or scene objects.", "OK");
            return;
        }

        int setCount = 0;
        foreach (var go in objs)
        {
            var pd = go.GetComponent<PersonDescriptor>();
            if (!pd) pd = go.AddComponent<PersonDescriptor>();

            // choose profile by name substring (case-insensitive)
            string n = go.name.ToLowerInvariant();
            Profile best = null;
            foreach (var p in PROFILES)
            {
                if (n.Contains(p.nameMatch)) { best = p; break; }
            }
            if (best == null)
            {
                // also try source asset path if available
                var path = AssetDatabase.GetAssetPath(go);
                if (!string.IsNullOrEmpty(path))
                {
                    string lp = path.ToLowerInvariant();
                    foreach (var p in PROFILES) if (lp.Contains(p.nameMatch)) { best = p; break; }
                }
            }
            if (best == null) continue;

            Undo.RecordObject(pd, "Apply Person Tags");
            pd.description = best.desc;
            pd.tags = best.tags;
            pd.triggerRadius = best.triggerRadius;

            // ensure trigger collider
            var sc = go.GetComponent<SphereCollider>() ?? go.AddComponent<SphereCollider>();
            sc.isTrigger = true; sc.radius = best.triggerRadius;

            EditorUtility.SetDirty(pd);
            setCount++;
        }

        AssetDatabase.SaveAssets();
        EditorUtility.DisplayDialog("Apply Person Tags", $"Updated {setCount} object(s).", "OK");
    }
}
#endif
