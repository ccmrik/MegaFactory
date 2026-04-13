using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace MegaFactory
{
    /// <summary>
    /// On-screen diagnostic overlay that exposes Smelter/Refinery internal state in real time.
    /// Toggle with F8 (configurable). Shows the nearest managed station: component type,
    /// m_spawnStack, queue count, fuel, spawn buffer, conversion table, and nearby containers.
    /// The HUD also surfaces the last deposit attempt so you can tell if the intercept is
    /// firing but silently failing.
    /// </summary>
    public class DiagnosticsHud : MonoBehaviour
    {
        private static DiagnosticsHud _instance;
        public static DiagnosticsHud Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("MegaFactory_DiagHud");
                    _instance = go.AddComponent<DiagnosticsHud>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        private bool _visible;
        private GUIStyle _style;
        private GUIStyle _titleStyle;
        private readonly StringBuilder _sb = new StringBuilder();

        public static string LastEvent = "(no production events yet)";

        public static void RecordEvent(string msg)
        {
            LastEvent = $"[{Time.time:F1}s] {msg}";
        }

        private void Update()
        {
            if (MegaFactoryPlugin.DiagnosticsHotkey != null &&
                Input.GetKeyDown(MegaFactoryPlugin.DiagnosticsHotkey.Value))
            {
                _visible = !_visible;
                if (_visible) DumpNearestToLog();
            }
        }

        private void DumpNearestToLog()
        {
            var smelter = FindNearestSmelter();
            if (smelter == null)
            {
                MegaFactoryPlugin.Log?.LogInfo("[Diag] No Smelter within 30m of player.");
                return;
            }
            MegaFactoryPlugin.Log?.LogInfo($"[Diag] ====== DUMP ======\n{BuildStateReport(smelter)}\n[Diag] ==================");
        }

        private Smelter FindNearestSmelter()
        {
            var player = Player.m_localPlayer;
            if (player == null) return null;
            Vector3 pp = player.transform.position;
            float bestSq = 30f * 30f;
            Smelter best = null;
            foreach (var s in FactoryProcessor.AllSmelters)
            {
                if (s == null) continue;
                float sq = (s.transform.position - pp).sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = s; }
            }
            return best;
        }

        private string BuildStateReport(Smelter s)
        {
            _sb.Clear();
            _sb.AppendLine($"Station: {s.gameObject.name}  (type={s.GetType().Name})");
            _sb.AppendLine($"  m_name={s.m_name}");
            _sb.AppendLine($"  m_maxOre={s.m_maxOre}  m_maxFuel={s.m_maxFuel}");
            _sb.AppendLine($"  m_secPerProduct={s.m_secPerProduct}  m_fuelPerProduct={s.m_fuelPerProduct}");
            _sb.AppendLine($"  m_spawnStack={s.m_spawnStack}  m_requiresRoof={s.m_requiresRoof}");
            _sb.AppendLine($"  m_fuelItem={(s.m_fuelItem != null ? s.m_fuelItem.gameObject.name : "<null>")}");
            _sb.AppendLine($"  m_outputPoint={(s.m_outputPoint != null ? s.m_outputPoint.position.ToString("F1") : "<null>")}");

            _sb.AppendLine("  Conversions:");
            if (s.m_conversion != null)
            {
                foreach (var c in s.m_conversion)
                {
                    string from = c?.m_from != null ? c.m_from.gameObject.name : "<null>";
                    string to   = c?.m_to   != null ? c.m_to.gameObject.name   : "<null>";
                    _sb.AppendLine($"    {from} -> {to}");
                }
            }

            var nview = s.GetComponent<ZNetView>();
            if (nview != null && nview.IsValid())
            {
                var zdo = nview.GetZDO();
                _sb.AppendLine($"  ZDO: owner={nview.IsOwner()}  uid={zdo.m_uid}");
                _sb.AppendLine($"    s_queued={zdo.GetInt(ZDOVars.s_queued, -1)}");
                _sb.AppendLine($"    s_fuel={zdo.GetFloat(ZDOVars.s_fuel, -1f):F2}");
                _sb.AppendLine($"    s_bakeTimer={zdo.GetFloat(ZDOVars.s_bakeTimer, -1f):F2}");
                _sb.AppendLine($"    s_accTime={zdo.GetFloat(ZDOVars.s_accTime, -1f):F2}");
                _sb.AppendLine($"    s_spawnOre='{zdo.GetString(ZDOVars.s_spawnOre, "")}'");
                _sb.AppendLine($"    s_spawnAmount={zdo.GetInt(ZDOVars.s_spawnAmount, 0)}");
                _sb.Append("    queue items: [");
                int q = zdo.GetInt(ZDOVars.s_queued, 0);
                for (int i = 0; i < q && i < s.m_maxOre; i++)
                {
                    if (i > 0) _sb.Append(", ");
                    _sb.Append(zdo.GetString($"item{i}", "?"));
                }
                _sb.AppendLine("]");
            }
            else
            {
                _sb.AppendLine("  ZDO: <invalid>");
            }

            var containers = ContainerHelper.FindNearbyContainers(s.transform.position, MegaFactoryPlugin.SearchRadius.Value);
            _sb.AppendLine($"  Nearby containers within {MegaFactoryPlugin.SearchRadius.Value:F0}m: {containers.Count}");
            foreach (var c in containers)
            {
                var ctype = ContainerHelper.GetContainerType(c);
                var inv = c.GetInventory();
                int items = inv != null ? inv.GetAllItems().Count : -1;
                _sb.AppendLine($"    - {c.gameObject.name} ({ctype})  items={items}");
            }

            _sb.AppendLine($"  Last production event: {LastEvent}");
            _sb.AppendLine($"  Registered smelters: {FactoryProcessor.AllSmelters.Count}");
            return _sb.ToString();
        }

        private void EnsureStyles()
        {
            if (_style != null) return;
            _style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                normal = { textColor = new Color(0.95f, 0.95f, 0.70f) },
                richText = true,
            };
            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(1f, 0.80f, 0.20f) },
            };
        }

        private void OnGUI()
        {
            if (!_visible) return;
            EnsureStyles();

            var smelter = FindNearestSmelter();
            string body = smelter != null
                ? BuildStateReport(smelter)
                : "(no smelter within 30m)";

            var boxRect = new Rect(10, 10, 520, Mathf.Min(Screen.height - 20, 480));
            GUI.color = new Color(0, 0, 0, 0.75f);
            GUI.DrawTexture(boxRect, Texture2D.whiteTexture);
            GUI.color = Color.white;

            GUILayout.BeginArea(boxRect);
            GUILayout.Label($"MegaFactory Diagnostics  (v{MegaFactoryPlugin.PluginVersion})   —  F8 to hide", _titleStyle);
            GUILayout.Space(4);
            GUILayout.Label(body, _style);
            GUILayout.EndArea();
        }
    }
}
