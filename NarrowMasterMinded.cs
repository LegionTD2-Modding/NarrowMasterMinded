using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace NarrowMasterMinded
{
    using P = Plugin;
    using C = Constants;

    [BepInProcess("Legion TD 2.exe")]
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        // Using GUID for the Harmony instance, so that we can unpatch just this plugin if needed
        private readonly Assembly _assembly = Assembly.GetExecutingAssembly();
        private readonly Harmony _harmony = new(PluginInfo.PLUGIN_GUID);

        internal new static ManualLogSource Logger;

        private string _gatewayFileAbs;
        private string _gatewayFileModdedAbs;
        
        private Traverse _trPresetsOptionsSections;
        private string _configApiClientVersion;

        // When the plugin is loaded
        public void Awake() {
            // Create masking Logger that references to the base one, to use Logger from outside the Plugin class
            Logger = base.Logger;

            // Get instances of static classes with Traverse: first the type, then the instance, and we act on it
            var typeConfigApi = AccessTools.TypeByName("Assets.Api.ConfigApi");
            var trConfigApi = Traverse.Create(typeConfigApi);
            _configApiClientVersion = trConfigApi.Field("ClientVersion").GetValue<string>();
            
            var typePresets = AccessTools.TypeByName("Assets.Presets");
            var trPresets = Traverse.Create(typePresets);
            _trPresetsOptionsSections = trPresets.Field("OptionsSections");

            // Check game version to warn if we are outdated
            if (!_configApiClientVersion.Equals(C.ClientVersionSupported)) {
                Logger.LogWarning($"Unsupported client v{_configApiClientVersion}");
                Logger.LogWarning("The behavior of this mod is undefined on this version");
                Logger.LogWarning($"Update your client to v{C.ClientVersionSupported} or wait for a mod update");
            }

            _gatewayFileAbs =
                Path.Combine(Paths.GameRootPath, "Legion TD 2_Data", "uiresources", "AeonGT", C.GatewayFileName);
            _gatewayFileModdedAbs =
                Path.Combine(Paths.GameRootPath, "Legion TD 2_Data", "uiresources", "AeonGT", C.GatewayFileNameModded);

            // Inject and patch
            try {
                RemoveTempFiles();
                InjectIntoGateway();
                Patch();
            }
            catch (Exception e) {
                Logger.LogError($"Error while injecting or patching: {e}");
                throw;
            }
            
            // All done!
            Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
        }

        // Unpatch if plugin is destroyed to handle in-game plugin reloads
        public void OnDestroy() {
            UnPatch();
            RemoveTempFiles();
        }

        private void Patch() {
            // Add our data inside the static class reached during Awake
            _trPresetsOptionsSections
                .Method("Add", new object[]{C.CfgLegionField, C.CfgLegionSection})
                .GetValue();
            // Apply all patches
            _harmony.PatchAll(_assembly);
        }

        private void UnPatch() {
            if (_trPresetsOptionsSections
                .Method("ContainsKey", new object[]{C.CfgLegionField})
                .GetValue<bool>()) 
            {
                _trPresetsOptionsSections.Method("Remove", new object[]{C.CfgLegionField}).GetValue();
            }
            _harmony.UnpatchSelf();
        }

        private void InjectIntoGateway() {
            var lines = File.ReadAllLines(_gatewayFileAbs);
            var resStream = _assembly.GetManifestResourceStream(C.GatewayEmbedded);
            using (var r = new StreamReader(resStream ?? throw new FileNotFoundException(C.GatewayEmbedded))) {
                lines[C.GatewayInsertLine] = r.ReadToEnd() + Environment.NewLine + lines[C.GatewayInsertLine];
            }
            File.WriteAllLines(_gatewayFileModdedAbs, lines);
        }

        private void RemoveTempFiles() {
            if (File.Exists(_gatewayFileModdedAbs)) {
                File.Delete(_gatewayFileModdedAbs);
            }
        }
    }
    
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [SuppressMessage("ReSharper", "UnusedMember.Local")]
    [HarmonyPatch]
    internal static class PatchSendCreateView
    {
        private static Type _typeCoherentUIGTView;

        [HarmonyPrepare]
        private static void Prepare() {
            _typeCoherentUIGTView = AccessTools.TypeByName("CoherentUIGTView");
        }

        [HarmonyTargetMethod]
        private static MethodBase TargetMethod() {
            return AccessTools.Method(_typeCoherentUIGTView, "SendCreateView");
        }

        [HarmonyPrefix]
        private static bool SendCreateViewPre(ref string ___m_Page) {
            if (___m_Page.Equals(C.GatewayFile)) {
                ___m_Page = C.GatewayFileModded;
            }
            return true;
        }
    }
    
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [SuppressMessage("ReSharper", "UnusedMember.Local")]
    [HarmonyPatch]
    internal static class PatchLoadOptions
    {
        private static Type _typeHudOptions;
        private static Type _typeOptionValue;
        private static Traverse _trHudApi;

        [HarmonyPrepare]
        private static void Prepare() {
            _typeHudOptions = AccessTools.TypeByName("Assets.Features.Hud.HudOptions");
            _typeOptionValue = AccessTools.Inner(_typeHudOptions, "OptionValue");

            _trHudApi = Traverse.Create(AccessTools.TypeByName("Assets.Api.HudApi"));
        }
        
        [HarmonyTargetMethod]
        private static MethodBase TargetMethod() {
            return AccessTools.Method(_typeHudOptions, "LoadOptions");
        }
        
        [HarmonyPostfix]
        private static void LoadOptionsPost(
            ref object ___config,
            ref Dictionary<string, object> ___options,
            ref Dictionary<string, Action<string>> ___OptionsHandlers) {
            
            // If it is not already, add a handler for this option
            // This handler will update the UI globalState with legion index
            if (!___OptionsHandlers.ContainsKey(C.CfgLegionField)) {
                ___OptionsHandlers.Add(C.CfgLegionField, delegate(string value) {

                    var argsTriggerHudEvent = new object[] {
                        C.CfgLegionFieldEvent,
                        Math.Max(0, Array.IndexOf(C.CfgLegionPossibleValues, value))
                    };

                    _trHudApi.Method("TriggerHudEvent", argsTriggerHudEvent).GetValue();
                });
                P.Logger.LogInfo($"Custom mod option {C.CfgLegionField} handler assigned");
            }
            
            var optionValue = Activator.CreateInstance(_typeOptionValue);

            var argsLoadString = new object[] {
                C.CfgLegionField,
                C.CfgLegionDefaultValue,
                C.CfgLegionPossibleValues
            };

            var strFromCfg = Traverse.Create(___config)
                .Method("LoadString", argsLoadString)
                .GetValue<string>();

            Traverse.Create(optionValue).Field("value").SetValue(strFromCfg);
            Traverse.Create(optionValue).Field("defaultValue").SetValue(C.CfgLegionDefaultValue);
            Traverse.Create(optionValue).Field("optionType").SetValue("choice");
            Traverse.Create(optionValue).Field("possibleValues").SetValue(C.CfgLegionPossibleValues);
            
            if (___options.ContainsKey(C.CfgLegionField)) {
                ___options[C.CfgLegionField] = optionValue;
                
                P.Logger.LogInfo($"Custom mod option {C.CfgLegionField} loaded");
                return;
            }
            
            ___options.Add(C.CfgLegionField, optionValue);
            P.Logger.LogInfo($"Custom mod option {C.CfgLegionField} added");
        }
        
    }

    // All those const are used until the framework is ready to do it more easily
    internal static class Constants
    {
        internal const string ClientVersionSupported = "10.02.3";
        internal const string GatewayFileName = "gateway.html";
        internal const string GatewayFileNameModded = "MOD_gateway.html";
        internal const string GatewayFile = "coui://uiresources/AeonGT/gateway.html";
        internal const string GatewayFileModded = "coui://uiresources/AeonGT/MOD_gateway.html";
        internal const string GatewayEmbedded = "NarrowMasterMinded.Data.MOD_gateway.html";
        internal const int    GatewayInsertLine = 100;
        internal const string CfgLegionSection = "MOD_Mods";
        internal const string CfgLegionField = "MOD_NMM_ForcedMastermindLegion";
        internal const string CfgLegionFieldEvent = "MOD_NMM_setForcedMastermindLegion";
        internal const string CfgLegionDefaultValue = "Disabled";
        internal static readonly string[] CfgLegionPossibleValues = {
            "Disabled",
            "Lock-In",
            "Greed",
            "Redraw",
            "Yolo",
            "Chaos",
            "Hybrid",
            "Fiesta",
            "Cash Out",
            "Castle",
            "Cartel"
        };
    }
}
