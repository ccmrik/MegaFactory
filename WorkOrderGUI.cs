using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace MegaFactory
{
    /// <summary>
    /// IMGUI-based Work Order panel. Hold Shift+E on a factory station to open.
    /// Select items to produce and set quantities. Orders persist via ZDO.
    /// </summary>
    public class WorkOrderGUI : MonoBehaviour
    {
        private static WorkOrderGUI _instance;
        private bool _visible;
        private Smelter _targetStation;
        private ZNetView _targetNView;
        private StationType _stationType;
        private StationDefinitions.InputItem[] _availableInputs;
        private Dictionary<string, string> _inputFields = new Dictionary<string, string>();
        private List<WorkOrder> _currentOrders;
        private Vector2 _scrollPos;
        private Rect _windowRect;
        private string _statusMessage = "";
        private float _statusTimer;

        private static GUIStyle _titleStyle;
        private static GUIStyle _headerStyle;
        private static GUIStyle _buttonStyle;
        private static GUIStyle _clearButtonStyle;
        private static GUIStyle _statusStyle;
        private static GUIStyle _inputStyle;
        private static GUIStyle _progressStyle;
        private static GUIStyle _boxStyle;
        private static bool _stylesInitialized;

        public static WorkOrderGUI Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("MegaFactory_WorkOrderGUI");
                    _instance = go.AddComponent<WorkOrderGUI>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        public bool IsVisible => _visible;

        public void Show(Smelter station, StationType stationType)
        {
            _targetStation = station;
            _targetNView = station.GetComponent<ZNetView>();
            _stationType = stationType;
            _availableInputs = StationDefinitions.GetInputs(stationType);
            _currentOrders = WorkOrderManager.GetOrders(_targetNView) ?? new List<WorkOrder>();

            // Initialize input fields from existing orders
            _inputFields.Clear();
            foreach (var input in _availableInputs)
            {
                string val = "0";
                foreach (var order in _currentOrders)
                {
                    if (order.PrefabName == input.PrefabName)
                    {
                        val = order.Requested.ToString();
                        break;
                    }
                }
                _inputFields[input.PrefabName] = val;
            }

            float width = 380f;
            float height = 120f + _availableInputs.Length * 80f;
            _windowRect = new Rect(Screen.width / 2f - width / 2f, Screen.height / 2f - height / 2f, width, height);
            _visible = true;
        }

        public void Hide()
        {
            _visible = false;
            _targetStation = null;
            _targetNView = null;
        }

        private void Update()
        {
            if (_visible && Input.GetKeyDown(KeyCode.Escape))
                Hide();

            if (_statusTimer > 0)
                _statusTimer -= Time.deltaTime;
        }

        private static void InitStyles()
        {
            if (_stylesInitialized) return;

            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 0.84f, 0f) }
            };

            _headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.8f, 0.9f, 1f) }
            };

            _buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold
            };

            _clearButtonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(1f, 0.4f, 0.4f) }
            };

            _statusStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Italic,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.4f, 1f, 0.4f) }
            };

            _inputStyle = new GUIStyle(GUI.skin.textField)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleCenter
            };

            _progressStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                normal = { textColor = new Color(0.7f, 0.7f, 0.7f) }
            };

            _boxStyle = new GUIStyle(GUI.skin.box);

            _stylesInitialized = true;
        }

        private void OnGUI()
        {
            if (!_visible || _targetStation == null) return;

            InitStyles();

            // Darken background
            GUI.color = new Color(0, 0, 0, 0.5f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;

            _windowRect = GUI.Window(98765, _windowRect, DrawWindow, "", GUI.skin.window);
        }

        private void DrawWindow(int windowId)
        {
            string stationName = StationDefinitions.GetStationDisplayName(_stationType);
            GUILayout.Space(5);
            GUILayout.Label($"Work Order: {stationName}", _titleStyle);
            GUILayout.Space(10);

            _scrollPos = GUILayout.BeginScrollView(_scrollPos);

            foreach (var input in _availableInputs)
            {
                GUILayout.BeginVertical(_boxStyle);

                GUILayout.Label(input.DisplayName, _headerStyle);

                GUILayout.BeginHorizontal();
                GUILayout.Label("Quantity:", GUILayout.Width(65));

                if (!_inputFields.ContainsKey(input.PrefabName))
                    _inputFields[input.PrefabName] = "0";

                _inputFields[input.PrefabName] = GUILayout.TextField(_inputFields[input.PrefabName], _inputStyle, GUILayout.Width(80));

                // Quick-set buttons
                if (GUILayout.Button("10", _buttonStyle, GUILayout.Width(35)))
                    _inputFields[input.PrefabName] = "10";
                if (GUILayout.Button("50", _buttonStyle, GUILayout.Width(35)))
                    _inputFields[input.PrefabName] = "50";
                if (GUILayout.Button("100", _buttonStyle, GUILayout.Width(40)))
                    _inputFields[input.PrefabName] = "100";
                if (GUILayout.Button("500", _buttonStyle, GUILayout.Width(40)))
                    _inputFields[input.PrefabName] = "500";

                GUILayout.EndHorizontal();

                // Show progress for existing orders
                foreach (var order in _currentOrders)
                {
                    if (order.PrefabName == input.PrefabName && order.Requested > 0)
                    {
                        float pct = order.Requested > 0 ? (float)order.Produced / order.Requested : 0f;
                        GUILayout.Label($"  Progress: {order.Produced}/{order.Requested} ({pct:P0})", _progressStyle);
                    }
                }

                GUILayout.EndVertical();
                GUILayout.Space(3);
            }

            GUILayout.EndScrollView();

            GUILayout.Space(5);

            // Status message
            if (_statusTimer > 0 && !string.IsNullOrEmpty(_statusMessage))
                GUILayout.Label(_statusMessage, _statusStyle);

            GUILayout.Space(5);

            GUILayout.BeginHorizontal();

            if (GUILayout.Button("Submit Order", _buttonStyle, GUILayout.Height(30)))
                SubmitOrder();

            if (GUILayout.Button("Clear All", _clearButtonStyle, GUILayout.Height(30)))
                ClearOrders();

            if (GUILayout.Button("Close", _buttonStyle, GUILayout.Height(30)))
                Hide();

            GUILayout.EndHorizontal();

            GUI.DragWindow(new Rect(0, 0, _windowRect.width, 30));
        }

        private void SubmitOrder()
        {
            if (_targetNView == null || !_targetNView.IsValid())
            {
                _statusMessage = "Station no longer valid!";
                _statusTimer = 3f;
                return;
            }

            var orders = new List<WorkOrder>();

            foreach (var input in _availableInputs)
            {
                if (!_inputFields.TryGetValue(input.PrefabName, out string val)) continue;
                if (!int.TryParse(val, out int qty) || qty <= 0) continue;

                // Check if there's an existing order to carry over progress
                int existingProduced = 0;
                foreach (var existing in _currentOrders)
                {
                    if (existing.PrefabName == input.PrefabName)
                    {
                        existingProduced = existing.Produced;
                        break;
                    }
                }

                orders.Add(new WorkOrder
                {
                    PrefabName = input.PrefabName,
                    DisplayName = input.DisplayName,
                    Requested = qty,
                    Produced = existingProduced
                });
            }

            WorkOrderManager.SetOrders(_targetNView, orders);
            _currentOrders = orders;

            _statusMessage = $"Work order submitted! ({orders.Count} item(s))";
            _statusTimer = 3f;

            if (Player.m_localPlayer != null)
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, $"Work Order set on {StationDefinitions.GetStationDisplayName(_stationType)}");
        }

        private void ClearOrders()
        {
            if (_targetNView != null && _targetNView.IsValid())
                WorkOrderManager.ClearOrders(_targetNView);

            _currentOrders = new List<WorkOrder>();
            foreach (var input in _availableInputs)
                _inputFields[input.PrefabName] = "0";

            _statusMessage = "All orders cleared!";
            _statusTimer = 3f;

            if (Player.m_localPlayer != null)
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, "Work orders cleared");
        }
    }

    // ==================== INTERACTION PATCH ====================
    // Intercept Shift+E on factory stations to open the Work Order GUI

    [HarmonyPatch(typeof(Smelter), "OnInteract")]
    public static class Smelter_OnInteract_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(Smelter __instance, ref bool __result)
        {
            if (!Input.GetKey(MegaFactoryPlugin.WorkOrderKey.Value))
                return true; // Normal interaction

            var stationType = GetStationType(__instance);
            if (stationType == null)
                return true; // Not one of our stations

            // Open work order GUI
            WorkOrderGUI.Instance.Show(__instance, stationType.Value);
            __result = true;
            return false; // Skip normal interaction
        }

        private static StationType? GetStationType(Smelter smelter)
        {
            string name = smelter.gameObject.name.ToLower();
            if (name.Contains("charcoal_kiln") || name.Contains("charcoalkiln"))
                return StationType.Kiln;
            if (name.Contains("blastfurnace") || name.Contains("blast_furnace"))
                return StationType.BlastFurnace;
            if (name.Contains("smelter"))
                return StationType.Smelter;
            if (name.Contains("windmill"))
                return StationType.Windmill;
            if (name.Contains("spinningwheel") || name.Contains("spinning_wheel"))
                return StationType.SpinningWheel;
            return null;
        }
    }

    // Show work order status on hover
    [HarmonyPatch(typeof(Smelter), "GetHoverText")]
    public static class Smelter_GetHoverText_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Smelter __instance, ref string __result)
        {
            var nview = __instance.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;

            if (WorkOrderManager.HasActiveOrders(nview))
            {
                var orders = WorkOrderManager.GetOrders(nview);
                __result += "\n<color=yellow>[MegaFactory Work Orders]</color>";
                foreach (var order in orders)
                {
                    if (order.IsComplete)
                        __result += $"\n  <color=green>✓ {order.DisplayName}: {order.Produced}/{order.Requested}</color>";
                    else
                        __result += $"\n  <color=orange>► {order.DisplayName}: {order.Produced}/{order.Requested}</color>";
                }
            }

            string keyName = MegaFactoryPlugin.WorkOrderKey.Value.ToString();
            __result += $"\n[<color=yellow>{keyName}+E</color>] Work Order";
        }
    }
}
