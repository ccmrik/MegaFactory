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
    [DefaultExecutionOrder(10000)]
    public class WorkOrderGUI : MonoBehaviour
    {
        private static WorkOrderGUI _instance;
        private bool _visible;
        private int _debugLogCounter;
        internal static int _escConsumedFrame = -1;
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
        private static GUIStyle _subtitleStyle;
        private static GUIStyle _headerStyle;
        private static GUIStyle _buttonStyle;
        private static GUIStyle _clearButtonStyle;
        private static GUIStyle _submitButtonStyle;
        private static GUIStyle _statusStyle;
        private static GUIStyle _inputStyle;
        private static GUIStyle _progressTextStyle;
        private static GUIStyle _completeStyle;
        private static GUIStyle _boxStyle;
        private static GUIStyle _windowStyle;
        private static GUIStyle _hintStyle;
        private static Texture2D _txWood;
        private static Texture2D _txParchment;
        private static Texture2D _txGold;
        private static Texture2D _txProgressFill;
        private static Texture2D _txProgressDone;
        private static Texture2D _txProgressBg;
        private static Texture2D _txDivider;
        private static bool _stylesInitialized;

        // Valheim-inspired palette
        private static readonly Color C_WOOD       = new Color(0.13f, 0.09f, 0.05f, 0.97f); // dark stained oak
        private static readonly Color C_PARCHMENT  = new Color(0.91f, 0.83f, 0.66f, 1.00f); // aged paper
        private static readonly Color C_GOLD       = new Color(0.90f, 0.74f, 0.32f, 1.00f); // viking gold
        private static readonly Color C_GOLD_DIM   = new Color(0.65f, 0.52f, 0.20f, 1.00f);
        private static readonly Color C_RED_RUNE   = new Color(0.80f, 0.20f, 0.18f, 1.00f);
        private static readonly Color C_GREEN_RUNE = new Color(0.45f, 0.85f, 0.40f, 1.00f);
        private static readonly Color C_AMBER      = new Color(0.95f, 0.60f, 0.20f, 1.00f);

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

            float width = 460f;
            float height = 200f + _availableInputs.Length * 110f;
            height = Mathf.Min(height, Screen.height * 0.85f);
            _windowRect = new Rect(Screen.width / 2f - width / 2f, Screen.height / 2f - height / 2f, width, height);
            _visible = true;
            if (MegaFactoryPlugin.DebugMode.Value)
                MegaFactoryPlugin.Log?.LogInfo($"[WorkOrder] GUI opened for {stationType}");
        }

        public void Hide()
        {
            _visible = false;
            _targetStation = null;
            _targetNView = null;
            if (MegaFactoryPlugin.DebugMode.Value)
                MegaFactoryPlugin.Log?.LogInfo("[WorkOrder] GUI closed.");
        }

        private void Update()
        {
            if (_visible && Input.GetKeyDown(KeyCode.Escape))
            {
                _escConsumedFrame = Time.frameCount;
                Hide();
            }

            if (_statusTimer > 0)
                _statusTimer -= Time.deltaTime;
        }

        private void LateUpdate()
        {
            if (_visible)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;

                // Debug logging (throttled — every 120 frames)
                _debugLogCounter++;
                if (MegaFactoryPlugin.DebugMode.Value && _debugLogCounter % 120 == 1)
                    MegaFactoryPlugin.Log?.LogInfo($"[WorkOrder] LateUpdate: visible={_visible}, cursorLock={Cursor.lockState}, cursorVis={Cursor.visible}");
            }
        }

        private static Texture2D MakeTex(Color color)
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }

        private static void InitStyles()
        {
            if (_stylesInitialized) return;

            _txWood          = MakeTex(C_WOOD);
            _txParchment     = MakeTex(C_PARCHMENT);
            _txGold          = MakeTex(C_GOLD);
            _txProgressBg    = MakeTex(new Color(0.08f, 0.06f, 0.03f, 1f));
            _txProgressFill  = MakeTex(C_AMBER);
            _txProgressDone  = MakeTex(C_GREEN_RUNE);
            _txDivider       = MakeTex(C_GOLD_DIM);

            _windowStyle = new GUIStyle(GUI.skin.window)
            {
                padding = new RectOffset(14, 14, 8, 14),
                border  = new RectOffset(8, 8, 8, 8),
                normal   = { background = _txWood, textColor = C_GOLD },
                onNormal = { background = _txWood, textColor = C_GOLD },
                hover    = { background = _txWood, textColor = C_GOLD },
                onHover  = { background = _txWood, textColor = C_GOLD },
                focused  = { background = _txWood, textColor = C_GOLD },
                onFocused= { background = _txWood, textColor = C_GOLD },
                active   = { background = _txWood, textColor = C_GOLD },
                onActive = { background = _txWood, textColor = C_GOLD },
            };

            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 22,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = C_GOLD }
            };

            _subtitleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Italic,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = C_PARCHMENT }
            };

            _headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                normal = { textColor = C_GOLD }
            };

            _buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                normal    = { background = _txProgressBg, textColor = C_PARCHMENT },
                hover     = { background = _txGold,       textColor = C_WOOD },
                active    = { background = _txGold,       textColor = C_WOOD },
                focused   = { background = _txProgressBg, textColor = C_PARCHMENT },
                border    = new RectOffset(2, 2, 2, 2),
                padding   = new RectOffset(6, 6, 4, 4),
            };

            _submitButtonStyle = new GUIStyle(_buttonStyle)
            {
                fontSize = 14,
                normal = { background = _txProgressBg, textColor = C_GOLD },
                hover  = { background = _txGold,       textColor = C_WOOD },
                active = { background = _txGold,       textColor = C_WOOD },
            };

            _clearButtonStyle = new GUIStyle(_buttonStyle)
            {
                fontSize = 13,
                normal = { background = _txProgressBg, textColor = C_RED_RUNE },
                hover  = { background = MakeTex(C_RED_RUNE), textColor = C_PARCHMENT },
                active = { background = MakeTex(C_RED_RUNE), textColor = C_PARCHMENT },
            };

            _statusStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Italic,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = C_GREEN_RUNE }
            };

            _inputStyle = new GUIStyle(GUI.skin.textField)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal    = { background = _txParchment, textColor = C_WOOD },
                hover     = { background = _txParchment, textColor = C_WOOD },
                focused   = { background = MakeTex(new Color(1f, 0.95f, 0.78f)), textColor = C_WOOD },
                padding   = new RectOffset(4, 4, 4, 4),
            };

            _progressTextStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = C_PARCHMENT }
            };

            _completeStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = C_GREEN_RUNE }
            };

            _hintStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 10,
                fontStyle = FontStyle.Italic,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = C_GOLD_DIM }
            };

            _boxStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = MakeTex(new Color(0.20f, 0.14f, 0.08f, 0.95f)), textColor = C_PARCHMENT },
                border = new RectOffset(4, 4, 4, 4),
                padding = new RectOffset(10, 10, 8, 8),
                margin = new RectOffset(0, 0, 4, 4),
            };

            _stylesInitialized = true;
        }

        private void OnGUI()
        {
            if (!_visible || _targetStation == null) return;

            InitStyles();

            // Swallow ALL mouse buttons during this OnGUI pass so clicks (esp. attack)
            // never reach the game world. Done before drawing so even mis-clicks outside
            // the window don't trigger an attack swing.
            var ev = Event.current;
            if (ev != null && (ev.type == EventType.MouseDown || ev.type == EventType.MouseUp ||
                               ev.type == EventType.MouseDrag || ev.type == EventType.ScrollWheel))
            {
                ev.Use();
            }

            // Vignette the world
            GUI.color = new Color(0, 0, 0, 0.55f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;

            _windowRect = GUI.Window(98765, _windowRect, DrawWindow, "", _windowStyle);
        }

        // Decorative gold horizontal divider with twin runes
        private void DrawDivider()
        {
            GUILayout.Space(4);
            var rect = GUILayoutUtility.GetRect(1, 2, GUILayout.ExpandWidth(true));
            // Inset by a few pixels so the line doesn't kiss the wood frame
            var line = new Rect(rect.x + 12, rect.y, rect.width - 24, 1.5f);
            GUI.DrawTexture(line, _txDivider);
            GUILayout.Space(4);
        }

        // Manual progress bar — Valheim-flavoured (amber while working, green when done)
        private void DrawProgressBar(int produced, int requested)
        {
            float pct = requested > 0 ? Mathf.Clamp01((float)produced / requested) : 0f;
            bool done = produced >= requested && requested > 0;

            var rect = GUILayoutUtility.GetRect(0f, 18f, GUILayout.ExpandWidth(true), GUILayout.Height(18));
            GUI.DrawTexture(rect, _txProgressBg);

            if (pct > 0f)
            {
                var fillRect = new Rect(rect.x, rect.y, rect.width * pct, rect.height);
                GUI.DrawTexture(fillRect, done ? _txProgressDone : _txProgressFill);
            }

            // Gold border (top + bottom rules)
            GUI.DrawTexture(new Rect(rect.x, rect.y,                rect.width, 1f), _txDivider);
            GUI.DrawTexture(new Rect(rect.x, rect.y + rect.height - 1f, rect.width, 1f), _txDivider);

            string label = requested > 0
                ? $"{produced} / {requested}   ({pct * 100f:F0}%)"
                : "—";
            GUI.Label(rect, label, _progressTextStyle);
        }

        private void DrawWindow(int windowId)
        {
            string stationName = StationDefinitions.GetStationDisplayName(_stationType);

            GUILayout.Space(2);
            // Forge banner — Valheim's UI font lacks most unicode glyphs, so use ASCII.
            GUILayout.Label("<< W O R K   O R D E R >>", _titleStyle);
            GUILayout.Label($"~  {stationName}  ~", _subtitleStyle);
            DrawDivider();

            _scrollPos = GUILayout.BeginScrollView(_scrollPos, GUILayout.MaxHeight(380));

            foreach (var input in _availableInputs)
            {
                // Find current order for this input (if any)
                WorkOrder currentOrder = null;
                foreach (var o in _currentOrders)
                {
                    if (o.PrefabName == input.PrefabName) { currentOrder = o; break; }
                }

                GUILayout.BeginVertical(_boxStyle);

                // Header row: item name + status badge
                GUILayout.BeginHorizontal();
                GUILayout.Label(input.DisplayName, _headerStyle);
                GUILayout.FlexibleSpace();
                if (currentOrder != null && currentOrder.Requested > 0)
                {
                    if (currentOrder.IsComplete)
                        GUILayout.Label("[ COMPLETE ]", _completeStyle);
                    else
                        GUILayout.Label($"{currentOrder.Remaining} left", _hintStyle);
                }
                GUILayout.EndHorizontal();

                GUILayout.Space(4);

                // Quantity row
                GUILayout.BeginHorizontal();
                GUILayout.Label("Quantity", GUILayout.Width(60));

                if (!_inputFields.ContainsKey(input.PrefabName))
                    _inputFields[input.PrefabName] = "0";

                _inputFields[input.PrefabName] = GUILayout.TextField(
                    _inputFields[input.PrefabName], _inputStyle,
                    GUILayout.Width(72), GUILayout.Height(26));

                GUILayout.Space(6);

                if (GUILayout.Button("10",  _buttonStyle, GUILayout.Width(36), GUILayout.Height(26)))
                    _inputFields[input.PrefabName] = "10";
                if (GUILayout.Button("50",  _buttonStyle, GUILayout.Width(36), GUILayout.Height(26)))
                    _inputFields[input.PrefabName] = "50";
                if (GUILayout.Button("100", _buttonStyle, GUILayout.Width(42), GUILayout.Height(26)))
                    _inputFields[input.PrefabName] = "100";
                if (GUILayout.Button("500", _buttonStyle, GUILayout.Width(42), GUILayout.Height(26)))
                    _inputFields[input.PrefabName] = "500";

                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                // Progress bar
                if (currentOrder != null && currentOrder.Requested > 0)
                {
                    GUILayout.Space(4);
                    DrawProgressBar(currentOrder.Produced, currentOrder.Requested);
                }

                GUILayout.EndVertical();
                GUILayout.Space(2);
            }

            GUILayout.EndScrollView();

            DrawDivider();

            // Status message (transient confirmation toast)
            if (_statusTimer > 0 && !string.IsNullOrEmpty(_statusMessage))
            {
                GUILayout.Label(_statusMessage, _statusStyle);
                GUILayout.Space(4);
            }

            // Action row
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("FORGE  ORDER", _submitButtonStyle, GUILayout.Height(34)))
                SubmitOrder();
            GUILayout.Space(6);
            if (GUILayout.Button("Clear", _clearButtonStyle, GUILayout.Width(80), GUILayout.Height(34)))
                ClearOrders();
            GUILayout.Space(6);
            if (GUILayout.Button("Close", _buttonStyle, GUILayout.Width(80), GUILayout.Height(34)))
                Hide();
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.Label("Re-submit a completed order to start another batch.", _hintStyle);

            GUI.DragWindow(new Rect(0, 0, _windowRect.width, 36));
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

                // Carry over progress for IN-PROGRESS orders only.
                // If the prior order was already complete, treat this as a fresh
                // batch (Produced=0) so the user can re-run the same recipe without
                // having to "Clear All" first.
                int existingProduced = 0;
                foreach (var existing in _currentOrders)
                {
                    if (existing.PrefabName == input.PrefabName)
                    {
                        if (!existing.IsComplete && existing.Produced < qty)
                            existingProduced = existing.Produced;
                        // else: complete or quantity reduced below progress → reset to 0
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

    // ==================== INPUT BLOCKING PATCHES ====================
    // Block player movement/interaction and camera mouse-look while Work Order GUI is open

    // This is the KEY patch — PlayerController.InInventoryEtc() gates mouse-look in FixedUpdate.
    // When it returns true, the game skips reading mouse axes for camera rotation.
    [HarmonyPatch(typeof(PlayerController), "InInventoryEtc")]
    public static class PlayerController_InInventoryEtc_WorkOrder_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ref bool __result)
        {
            if (WorkOrderGUI.Instance != null && WorkOrderGUI.Instance.IsVisible)
                __result = true;
        }
    }

    [HarmonyPatch(typeof(Player), "TakeInput")]
    public static class Player_TakeInput_WorkOrder_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ref bool __result)
        {
            if (WorkOrderGUI.Instance != null && WorkOrderGUI.Instance.IsVisible)
                __result = false;
        }
    }

    // Belt-and-braces: ZInput is what reads attack/use/block buttons. Force every
    // button query to "not pressed" while our GUI is visible so a click on the panel
    // can't bleed through and start a swing.
    internal static class WorkOrderInputBlocker
    {
        public static bool IsBlocking
            => WorkOrderGUI.Instance != null && WorkOrderGUI.Instance.IsVisible;
    }

    [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButton))]
    public static class ZInput_GetButton_WorkOrder_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(ref bool __result)
        {
            if (!WorkOrderInputBlocker.IsBlocking) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButtonDown))]
    public static class ZInput_GetButtonDown_WorkOrder_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(ref bool __result)
        {
            if (!WorkOrderInputBlocker.IsBlocking) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButtonUp))]
    public static class ZInput_GetButtonUp_WorkOrder_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(ref bool __result)
        {
            if (!WorkOrderInputBlocker.IsBlocking) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetMouseButton))]
    public static class ZInput_GetMouseButton_WorkOrder_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(ref bool __result)
        {
            if (!WorkOrderInputBlocker.IsBlocking) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetMouseButtonDown))]
    public static class ZInput_GetMouseButtonDown_WorkOrder_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(ref bool __result)
        {
            if (!WorkOrderInputBlocker.IsBlocking) return true;
            __result = false;
            return false;
        }
    }

    // Block ESC from opening main menu on the same frame we close our GUI
    [HarmonyPatch(typeof(Menu), "Update")]
    public static class Menu_Update_WorkOrder_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (WorkOrderGUI._escConsumedFrame == Time.frameCount)
                return false; // Skip Menu.Update this frame
            return true;
        }
    }

    // ==================== INTERACTION PATCHES ====================
    // Intercept Shift+E on factory stations to open the Work Order GUI
    // Valheim uses Switch callbacks (OnAddOre, OnAddFuel, OnEmpty) instead of Interact

    public static class WorkOrderInterceptHelper
    {
        public static bool TryOpenWorkOrderGUI(Smelter instance, Humanoid user, ref bool __result)
        {
            bool keyHeld = Input.GetKey(MegaFactoryPlugin.WorkOrderKey.Value);
            if (!keyHeld)
                return true; // Normal interaction

            var stationType = GetStationType(instance);
            if (stationType == null)
                return true;

            if (MegaFactoryPlugin.DebugMode.Value)
                MegaFactoryPlugin.Log?.LogInfo($"[WorkOrder] Opening Work Order GUI for {stationType.Value}");
            WorkOrderGUI.Instance.Show(instance, stationType.Value);
            __result = true;
            return false;
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
            if (name.Contains("eitrrefinery") || name.Contains("eitr_refinery"))
                return StationType.EitrRefinery;
            return null;
        }
    }

    [HarmonyPatch(typeof(Smelter), "OnAddOre")]
    public static class Smelter_OnAddOre_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(Smelter __instance, Humanoid user, ref bool __result)
        {
            return WorkOrderInterceptHelper.TryOpenWorkOrderGUI(__instance, user, ref __result);
        }
    }

    [HarmonyPatch(typeof(Smelter), "OnAddFuel")]
    public static class Smelter_OnAddFuel_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(Smelter __instance, Humanoid user, ref bool __result)
        {
            return WorkOrderInterceptHelper.TryOpenWorkOrderGUI(__instance, user, ref __result);
        }
    }

    // Show work order status on hover (ore switch)
    [HarmonyPatch(typeof(Smelter), "OnHoverAddOre")]
    public static class Smelter_OnHoverAddOre_Patch
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
                        __result += $"\n  <color=green>* {order.DisplayName}: {order.Produced}/{order.Requested}</color>";
                    else
                        __result += $"\n  <color=orange>> {order.DisplayName}: {order.Produced}/{order.Requested}</color>";
                }
            }

            string keyName = MegaFactoryPlugin.WorkOrderKey.Value.ToString();
            __result += $"\n[<color=yellow>{keyName}+E</color>] Work Order";
        }
    }
}
