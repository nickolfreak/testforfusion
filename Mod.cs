using System.Collections.Generic;
using BoneLib.BoneMenu;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;
using RigManager = Il2CppSLZ.Marrow.RigManager;

[assembly: MelonInfo(typeof(EntityBoxEsp.Mod), "EntityBoxEsp", "2.1.0-quest", "you")]
[assembly: MelonGame("Stress Level Zero", "BONELAB")]
[assembly: MelonAdditionalDependencies("BoneLib")]

namespace EntityBoxEsp
{
    public class Mod : MelonMod
    {
        // category ids
        private const int P = 0; // players
        private const int F = 1; // fords
        private const int E = 2; // enemies (other NPCs)
        private const int G = 3; // guns

        private bool _master = true;
        private readonly bool[] _catOn = { true, true, true, true };
        private readonly Color[] _catColor =
        {
            Color.red,                 // players
            Color.green,               // fords
            new Color(1f, 0.55f, 0f),  // enemies (orange)
            Color.cyan                 // guns
        };

        // used when we can't find a mesh to measure
        private static readonly Vector3[] FallbackSize =
        {
            new Vector3(0.7f, 1.8f, 0.7f),
            new Vector3(0.8f, 1.8f, 0.8f),
            new Vector3(0.8f, 1.8f, 0.8f),
            new Vector3(0.35f, 0.25f, 0.35f)
        };
        private static readonly float[] FallbackLift = { 0.9f, 0.9f, 0.9f, 0f };

        private string _fordKeyword = "ford";
        private const float LineWidth = 0.025f; // a bit thicker for Quest 3 resolution
        private const float ScanStep = 1.0f; // one category scanned per step (slower = easier on Quest)
        private const int MaxBoxes = 24;       // hard cap so a crowded scene cannot tank Quest framerate

        private class Tracked
        {
            public Component Comp;
            public Renderer Rend;
            public GameObject Go;
            public LineRenderer Line;
            public int Cat;
        }

        private readonly Dictionary<int, Tracked> _tracked = new Dictionary<int, Tracked>();
        private readonly List<int> _dead = new List<int>();
        private readonly HashSet<int> _seen = new HashSet<int>();

        private GameObject _root;
        private Material _mat;
        private bool _matTried;

        private bool _typesResolved;
        private System.Type _aiType;
        private System.Type _gunType;

        private int _scanSlot;
        private float _nextScan;
        private bool _scanErrorLogged;

        // ------------------------------------------------------------ setup

        public override void OnInitializeMelon()
        {
            try
            {
                BuildMenu();
            }
            catch (System.Exception ex)
            {
                LoggerInstance.Error("BoneMenu setup failed: " + ex);
            }
            LoggerInstance.Msg("EntityBoxEsp loaded.");
        }

        private void BuildMenu()
        {
            Page page = Page.Root.CreatePage("Entity Boxes", Color.cyan);

            page.CreateBool("Boxes On/Off", Color.white, true, v =>
            {
                _master = v;
                if (!v) ClearAll();
            });
            page.CreateBool("Players", _catColor[P], true, v => { _catOn[P] = v; });
            page.CreateBool("Fords", _catColor[F], true, v => { _catOn[F] = v; });
            page.CreateBool("Enemies", _catColor[E], true, v => { _catOn[E] = v; });
            page.CreateBool("Guns", _catColor[G], true, v => { _catOn[G] = v; });

            page.CreateString("Ford name contains", Color.white, _fordKeyword, v =>
            {
                _fordKeyword = string.IsNullOrEmpty(v) ? "" : v.ToLowerInvariant();
                ClearCategory(F);
                ClearCategory(E);
                _nextScan = 0f;
            });

            page.CreateFunction("Rescan now", Color.white, () => { _nextScan = 0f; });
        }

        // ------------------------------------------------------------ main loop

        public override void OnLateUpdate()
        {
            if (!_master) return;

            if (Time.time >= _nextScan)
            {
                _nextScan = Time.time + ScanStep;
                try
                {
                    RunScanStep();
                }
                catch (System.Exception ex)
                {
                    if (!_scanErrorLogged)
                    {
                        _scanErrorLogged = true;
                        LoggerInstance.Error("Scan failed (logged once): " + ex);
                    }
                }
            }

            UpdateAll();
        }

        private void UpdateAll()
        {
            _dead.Clear();
            foreach (var kv in _tracked)
            {
                Tracked t = kv.Value;
                if (t.Comp == null || t.Go == null || t.Line == null || !_catOn[t.Cat])
                {
                    _dead.Add(kv.Key);
                    continue;
                }
                UpdateBox(t);
            }
            foreach (int id in _dead) Remove(id);
        }

        // ------------------------------------------------------------ scanning

        // Scans ONE category per step so Quest never does all the FindObjectsOfType work in one frame.
        private void RunScanStep()
        {
            for (int i = 0; i < 3; i++)
            {
                int slot = _scanSlot;
                _scanSlot = (_scanSlot + 1) % 3;

                if (slot == 0 && _catOn[P]) { ScanPlayers(); return; }
                if (slot == 1 && (_catOn[F] || _catOn[E])) { ScanNpcs(); return; }
                if (slot == 2 && _catOn[G]) { ScanGuns(); return; }
            }
        }

        private void ScanPlayers()
        {
            var rigs = UnityEngine.Object.FindObjectsOfType<RigManager>();
            _seen.Clear();

            if (rigs == null || rigs.Length < 2)
            {
                RemoveStale(P, P);
                return;
            }

            // Our own rig is the one sitting under our headset camera.
            Camera cam = Camera.main;
            int localIdx = -1;
            if (cam != null)
            {
                Vector3 cp = cam.transform.position;
                float best = float.MaxValue;
                for (int i = 0; i < rigs.Length; i++)
                {
                    RigManager r = rigs[i];
                    if (r == null) continue;
                    Vector3 d = r.transform.position - cp;
                    d.y = 0f;
                    float m = d.sqrMagnitude;
                    if (m < best) { best = m; localIdx = i; }
                }
            }

            if (localIdx < 0)
            {
                // can't tell which rig is ours - show nothing rather than boxing ourselves
                RemoveStale(P, P);
                return;
            }

            for (int i = 0; i < rigs.Length; i++)
            {
                if (i == localIdx) continue;
                RigManager r = rigs[i];
                if (r == null) continue;
                Track(P, r, false);
            }

            RemoveStale(P, P);
        }

        private void ScanNpcs()
        {
            ResolveTypes();
            if (_aiType == null) return;

            var arr = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(_aiType));
            _seen.Clear();

            if (arr != null)
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    UnityEngine.Object o = arr[i];
                    if (o == null) continue;
                    Component comp = o.TryCast<Component>();
                    if (comp == null) continue;

                    int cat = IsFord(comp) ? F : E;
                    if (!_catOn[cat]) continue;
                    Track(cat, comp, true);
                }
            }

            RemoveStale(F, E);
        }

        private void ScanGuns()
        {
            ResolveTypes();
            if (_gunType == null) return;

            var arr = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(_gunType));
            _seen.Clear();

            if (arr != null)
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    UnityEngine.Object o = arr[i];
                    if (o == null) continue;
                    Component comp = o.TryCast<Component>();
                    if (comp == null) continue;
                    Track(G, comp, false);
                }
            }

            RemoveStale(G, G);
        }

        private bool IsFord(Component comp)
        {
            if (string.IsNullOrEmpty(_fordKeyword)) return false;
            string a = comp.gameObject.name;
            string b = comp.transform.root.gameObject.name;
            return (a != null && a.ToLowerInvariant().Contains(_fordKeyword))
                || (b != null && b.ToLowerInvariant().Contains(_fordKeyword));
        }

        // ------------------------------------------------------------ type lookup

        // The NPC and gun component types live in Il2CppSLZ.* assemblies whose exact
        // namespaces differ between game patches, so we look them up by name at runtime.
        private void ResolveTypes()
        {
            if (_typesResolved) return;
            _typesResolved = true;

            _aiType = FindIl2CppType("AIBrain");
            _gunType = FindIl2CppType("Gun");

            LoggerInstance.Msg(_aiType != null
                ? "NPC type found: " + _aiType.FullName
                : "WARNING: AIBrain type not found - Fords/Enemies boxes will not work.");
            LoggerInstance.Msg(_gunType != null
                ? "Gun type found: " + _gunType.FullName
                : "WARNING: Gun type not found - Guns boxes will not work.");
        }

        private static System.Type FindIl2CppType(string simpleName)
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                string asmName = asm.GetName().Name;
                if (asmName == null || !asmName.StartsWith("Il2CppSLZ")) continue;

                System.Type[] types;
                try { types = asm.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }
                if (types == null) continue;

                foreach (var t in types)
                {
                    if (t != null && t.Name == simpleName) return t;
                }
            }
            return null;
        }

        // ------------------------------------------------------------ tracking

        private void Track(int cat, Component comp, bool measureFromParent)
        {
            int id = comp.GetInstanceID();
            _seen.Add(id);

            if (!_tracked.ContainsKey(id) && _tracked.Count >= MaxBoxes) return;

            Tracked existing;
            if (_tracked.TryGetValue(id, out existing))
            {
                if (existing.Cat == cat)
                {
                    // avatars/NPC meshes can finish loading after the box was made
                    if (existing.Rend == null) existing.Rend = PickRenderer(comp, measureFromParent);
                    return;
                }
                Remove(id); // category changed (Ford keyword edited)
            }

            GameObject go = new GameObject("EntityBox");
            go.transform.SetParent(GetRoot().transform, false);

            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.loop = false;
            lr.positionCount = 16;
            lr.startWidth = LineWidth;
            lr.endWidth = LineWidth;
            lr.numCapVertices = 0;
            lr.numCornerVertices = 0;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.startColor = _catColor[cat];
            lr.endColor = _catColor[cat];

            Material m = GetMaterial();
            if (m != null) lr.sharedMaterial = m;

            _tracked[id] = new Tracked
            {
                Comp = comp,
                Rend = PickRenderer(comp, measureFromParent),
                Go = go,
                Line = lr,
                Cat = cat
            };
        }

        // Picks the biggest body mesh under the entity so the box can hug it.
        private static Renderer PickRenderer(Component comp, bool fromParent)
        {
            Transform start = comp.transform;
            if (fromParent && start.parent != null) start = start.parent;

            var rends = start.GetComponentsInChildren<Renderer>();
            if (rends == null) return null;

            Renderer best = null;
            float bestSize = 0f;
            for (int i = 0; i < rends.Length; i++)
            {
                Renderer r = rends[i];
                if (r == null) continue;
                if (r.TryCast<SkinnedMeshRenderer>() == null && r.TryCast<MeshRenderer>() == null) continue;

                float s = r.bounds.size.magnitude;
                if (s > bestSize) { bestSize = s; best = r; }
            }
            return best;
        }

        private void UpdateBox(Tracked t)
        {
            bool active = t.Comp.gameObject.activeInHierarchy;
            t.Line.enabled = active;
            if (!active) return;

            Vector3 c;
            Vector3 size;
            bool usedMesh = false;

            c = Vector3.zero;
            size = Vector3.zero;

            if (t.Rend != null)
            {
                Bounds b = t.Rend.bounds;
                c = b.center;
                size = b.size;
                // reject nonsense bounds (huge shared meshes, zero-size, etc.)
                usedMesh = size.x > 0.05f && size.y > 0.05f && size.z > 0.05f
                           && size.x < 4f && size.y < 4f && size.z < 4f;
            }

            if (!usedMesh)
            {
                c = t.Comp.transform.position + new Vector3(0f, FallbackLift[t.Cat], 0f);
                size = FallbackSize[t.Cat];
            }

            Vector3 h = size * 0.5f;

            Vector3 b0 = c + new Vector3(-h.x, -h.y, -h.z);
            Vector3 b1 = c + new Vector3(h.x, -h.y, -h.z);
            Vector3 b2 = c + new Vector3(h.x, -h.y, h.z);
            Vector3 b3 = c + new Vector3(-h.x, -h.y, h.z);
            Vector3 t0 = c + new Vector3(-h.x, h.y, -h.z);
            Vector3 t1 = c + new Vector3(h.x, h.y, -h.z);
            Vector3 t2 = c + new Vector3(h.x, h.y, h.z);
            Vector3 t3 = c + new Vector3(-h.x, h.y, h.z);

            // One continuous strip that traces all 12 edges of the box.
            LineRenderer lr = t.Line;
            lr.SetPosition(0, b0);
            lr.SetPosition(1, b1);
            lr.SetPosition(2, b2);
            lr.SetPosition(3, b3);
            lr.SetPosition(4, b0);
            lr.SetPosition(5, t0);
            lr.SetPosition(6, t1);
            lr.SetPosition(7, b1);
            lr.SetPosition(8, t1);
            lr.SetPosition(9, t2);
            lr.SetPosition(10, b2);
            lr.SetPosition(11, t2);
            lr.SetPosition(12, t3);
            lr.SetPosition(13, b3);
            lr.SetPosition(14, t3);
            lr.SetPosition(15, t0);
        }

        // ------------------------------------------------------------ cleanup

        private void RemoveStale(int catA, int catB)
        {
            _dead.Clear();
            foreach (var kv in _tracked)
            {
                int cat = kv.Value.Cat;
                if ((cat == catA || cat == catB) && !_seen.Contains(kv.Key))
                    _dead.Add(kv.Key);
            }
            foreach (int id in _dead) Remove(id);
        }

        private void Remove(int id)
        {
            Tracked t;
            if (!_tracked.TryGetValue(id, out t)) return;
            if (t.Go != null) UnityEngine.Object.Destroy(t.Go);
            _tracked.Remove(id);
        }

        private void ClearCategory(int cat)
        {
            var ids = new List<int>();
            foreach (var kv in _tracked)
                if (kv.Value.Cat == cat) ids.Add(kv.Key);
            foreach (int id in ids) Remove(id);
        }

        private void ClearAll()
        {
            var ids = new List<int>(_tracked.Keys);
            foreach (int id in ids) Remove(id);
        }

        // ------------------------------------------------------------ helpers

        private GameObject GetRoot()
        {
            if (_root == null)
            {
                _root = new GameObject("EntityBoxEsp_Root");
                UnityEngine.Object.DontDestroyOnLoad(_root);
            }
            return _root;
        }

        // Tries shaders that can draw on top of walls. Names can be stripped from a build.
        private Material GetMaterial()
        {
            if (_mat != null || _matTried) return _mat;
            _matTried = true;

            string[] names = { "UI/Default", "Hidden/Internal-Colored", "Sprites/Default" };
            foreach (string n in names)
            {
                Shader sh = Shader.Find(n);
                if (sh == null) continue;

                Material m = new Material(sh);
                m.renderQueue = 5000;
                m.SetInt("unity_GUIZTestMode", 8); // CompareFunction.Always (UI/Default)
                m.SetInt("_ZTest", 8);             // CompareFunction.Always (Internal-Colored)
                m.color = Color.white;

                _mat = m;
                LoggerInstance.Msg("Box shader: " + n);
                return _mat;
            }

            LoggerInstance.Warning("No usable shader found - boxes may not draw.");
            return null;
        }
    }
}
