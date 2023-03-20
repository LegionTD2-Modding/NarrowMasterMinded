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
            // Create masking Logger as internal to use more easily in code
            Logger = base.Logger;

            // Get type of 'Assets.Api.ConfigApi' game class, hook a Traverse on an instance of it
            // Then jump to the ClientVersion field and get the value
            var typeConfigApi = AccessTools.TypeByName("Assets.Api.ConfigApi");
            var trConfigApi = Traverse.Create(typeConfigApi);
            _configApiClientVersion = trConfigApi.Field("ClientVersion").GetValue<string>();
            
            // Get type of 'Assets.Presets' game class, hook a Traverse on an instance of it
            // Then save a Traverse to its 'OptionsSections' field
            var typePresets = AccessTools.TypeByName("Assets.Presets");
            var trPresets = Traverse.Create(typePresets);
            _trPresetsOptionsSections = trPresets.Field("OptionsSections");

            // Check game version to warn if we are outdated
            if (!_configApiClientVersion.Equals(C.ClientVersionSupported)) {
                Logger.LogWarning($"Unsupported client v{_configApiClientVersion}");
                Logger.LogWarning("The behavior of this mod is undefined on this version");
                Logger.LogWarning($"Update your client to v{C.ClientVersionSupported} or wait for a mod update");
            }

            // Get paths to game js folder and to our future modded gateway
            _gatewayFileAbs =
                Path.Combine(Paths.GameRootPath, "Legion TD 2_Data", "uiresources", "AeonGT", C.GatewayFileName);
            _gatewayFileModdedAbs =
                Path.Combine(Paths.GameRootPath, "Legion TD 2_Data", "uiresources", "AeonGT", C.GatewayFileNameModded);

            // Inject custom js and patch c#
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
        // Remove files we created
        public void OnDestroy() {
            UnPatch();
            RemoveTempFiles();
        }

        private void Patch() {
            // Call our saved Traverse to add to underlying 'OptionsSections' our custom menu option section: "Mods"
            _trPresetsOptionsSections
                .Method("Add", new object[]{C.CfgLegionField, C.CfgLegionSection})
                .GetValue();
            // Apply all patches or current assembly
            _harmony.PatchAll(_assembly);
        }

        // Undoes what Patch() did
        private void UnPatch() {
            if (_trPresetsOptionsSections
                .Method("ContainsKey", new object[]{C.CfgLegionField})
                .GetValue<bool>()) 
            {
                _trPresetsOptionsSections.Method("Remove", new object[]{C.CfgLegionField}).GetValue();
            }
            _harmony.UnpatchSelf();
        }

        // Adds content of embedded html to the original gateway
        // Save result in custom gateway that we'll force the game to use
        private void InjectIntoGateway() {
            var lines = File.ReadAllLines(_gatewayFileAbs);
            var resStream = _assembly.GetManifestResourceStream(C.GatewayEmbedded);
            using (var r = new StreamReader(resStream ?? throw new FileNotFoundException(C.GatewayEmbedded))) {
                lines[C.GatewayInsertLine] = r.ReadToEnd() + Environment.NewLine + lines[C.GatewayInsertLine];
            }
            File.WriteAllLines(_gatewayFileModdedAbs, lines);
        }

        // Delete custom gateway file
        private void RemoveTempFiles() {
            if (File.Exists(_gatewayFileModdedAbs)) {
                File.Delete(_gatewayFileModdedAbs);
            }
        }
    }
    
    // This patches a method inside a class, details inside
    // Just before it is called, and if the m_Page field references to gateway.html
    // We will change the m_Page field to reference our MOD_gateway.html instead
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [SuppressMessage("ReSharper", "UnusedMember.Local")]
    [HarmonyPatch]
    internal static class PatchSendCreateView
    {
        private static Type _typeCoherentUIGTView;

        // To prepare the patch, we save the type we want to patch in game dll: CoherentUIGTView
        [HarmonyPrepare]
        private static void Prepare() {
            _typeCoherentUIGTView = AccessTools.TypeByName("CoherentUIGTView");
        }

        // Then we give info about the method in this type we want to patch: SendCreateView
        [HarmonyTargetMethod]
        private static MethodBase TargetMethod() {
            return AccessTools.Method(_typeCoherentUIGTView, "SendCreateView");
        }

        // For the method returned in TargetMethod, add this prefix
        // ___m_Page is a ref to field m_Page from patched object CoherentUIGTView
        // We edit it, and return true to still call the real SendCreateView
        // Therefore we edit the target file of the view as we please
        [HarmonyPrefix]
        private static bool SendCreateViewPre(ref string ___m_Page) {
            if (___m_Page.Equals(C.GatewayFile)) {
                ___m_Page = C.GatewayFileModded;
            }
            return true;
        }
    }
    
    // Same global idea as before, except we need also references to other classes
    //
    // We add our custom setting to the HudOptions.options field and to OptionsHandlers field
    //
    // Look at LoadOptions source, this simply recreates what this line would do in LoadOptions:
    // action(P.CfgLegionField, P.CfgLegionDefaultValue, P.CfgLegionPossibleValues);
    // This adds a handler to be able to send the data to the UI's globalState
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [SuppressMessage("ReSharper", "UnusedMember.Local")]
    [HarmonyPatch]
    internal static class PatchLoadOptions
    {
        private static Type _typeHudOptions;
        private static Type _typeOptionValue;
        private static Traverse _trHudApi;

        // We get _typeHudOptions for the target method, but also grab classes in HudOptions
        // OptionValue is an inner class of HudOptions so we use Inner()
        [HarmonyPrepare]
        private static void Prepare() {
            _typeHudOptions = AccessTools.TypeByName("Assets.Features.Hud.HudOptions");
            _typeOptionValue = AccessTools.Inner(_typeHudOptions, "OptionValue");

            _trHudApi = Traverse.Create(AccessTools.TypeByName("Assets.Api.HudApi"));
        }
        
        // We patch LoadOptions inside HudOptions
        [HarmonyTargetMethod]
        private static MethodBase TargetMethod() {
            return AccessTools.Method(_typeHudOptions, "LoadOptions");
        }
        
        // And this time we patch as post, and we add data that was not added by the base game
        // We add handlers for the new config and the new option to use the game standard settings logic
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
            
            // Create instance of a class we do not really know based on game dll knowledge
            // We use Activator, AccessTools and Traverse to do it indirectly without adding game dlls to dependancies
            var optionValue = Activator.CreateInstance(_typeOptionValue);

            var argsLoadString = new object[] {
                C.CfgLegionField,
                C.CfgLegionDefaultValue,
                C.CfgLegionPossibleValues
            };

            // Hijack the storing of settings from the game to use it as well for custom options
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
