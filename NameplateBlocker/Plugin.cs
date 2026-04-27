using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.NET.Common;
using BepInExResoniteShim;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using HarmonyLib;

namespace NameplateBlocker;

[ResonitePlugin(PluginMetadata.GUID, PluginMetadata.NAME, PluginMetadata.VERSION, PluginMetadata.AUTHORS, PluginMetadata.REPOSITORY_URL)]
[BepInDependency(BepInExResoniteShim.PluginMetadata.GUID, BepInDependency.DependencyFlags.HardDependency)]
public class Plugin : BasePlugin
{
    internal static new ManualLogSource Log = null!;

    internal static ConfigEntry<bool> Enabled = null!;
    internal static ConfigEntry<bool> Invert = null!;
    internal static ConfigEntry<StringSet> BlockedNameplates = null!;
    internal static ConfigEntry<StringSet> AllowedNameplates = null!;

    public override void Load()
    {
        Log = base.Log;

        TomlTypeConverter.AddConverter(typeof(StringSet), new TypeConverter
        {
            ConvertToObject = (str, _) => StringSet.Parse(str),
            ConvertToString = (obj, _) => ((StringSet)obj).ToString()
        });

        Enabled = Config.Bind("General", "Enabled", true, "Enable the plugin");
        Enabled.SettingChanged += RunChange;
        Invert = Config.Bind("General", "Invert", false, "Invert the logic\nWhen enabled, if UseCustomNameplates is disabled, you can specifically allow certain nameplates");
        Invert.SettingChanged += RunChange;

        BlockedNameplates = Config.Bind("Lists", "Blocked Nameplates", new StringSet(), "Blocked Nameplates -\n By UserID, separated by commas");
        AllowedNameplates = Config.Bind("Lists", "Allowed Nameplates", new StringSet(), "Allowed Nameplates -\n By UserID, separated by commas");
        Config.Bind("Lists", "Update Current Nameplates", default(dummy), new ConfigDescription("Updates current nameplates", null, UpdateVisibility));

        HarmonyInstance.PatchAll();

        Log.LogInfo($"Plugin {PluginMetadata.GUID} is loaded!");
    }

    public static void RunChange(object? sender, EventArgs args) => UpdateVisibility();

    public static void ToggleUser(string id, StringSet __set)
    {
        if (!__set.Add(id))
        {
            __set.Remove(id);
        }

        UpdateVisibility();
    }


    public static void UpdateVisibility() => NameplatePatch.AvailableDrivers.ForEach(d => d.UpdateVisibility());
}

[HarmonyPatch(typeof(ContactsDialog), "UpdateSelectedContactUI")]
public class ContactsPatch
{
    [HarmonyPostfix]
    private static void PinUserAdder(ContactsDialog __instance, UIBuilder ___actionsUi)
    {
        if (!Plugin.Enabled.Value) return;

        if (!__instance.World.IsUserspace()) return;
        if (__instance.SelectedContact == null || __instance.SelectedContactId == __instance.Cloud.Platform.AppUserId || __instance.SelectedContact.IsSelfContact)
            return;

        ___actionsUi.PushStyle();

        float size = Plugin.Invert.Value ? 32f : 48f;
        
        Button blockButton = ___actionsUi.Button(Plugin.BlockedNameplates.Value.Contains(__instance.SelectedContactId) ? "Unblock NP" : "Block NP");
        blockButton.Slot.GetComponent<LayoutElement>().PreferredWidth.Value = size;
        blockButton.LocalPressed += (btn, _) =>
        {
            StringSet set = Plugin.BlockedNameplates.Value;
            Plugin.ToggleUser(__instance.SelectedContactId, set);
            
            btn.LabelTextField.Value = set.Contains(__instance.SelectedContactId) ? "Unblock NP" : "Block NP";
        };
        
        int index = blockButton.Slot.ChildIndex;
        if (index > 0)
        {
            Slot prev = blockButton.Slot.Parent[index - 1];
            if (prev.ChildrenCount > 0 && (prev[0].GetComponent<LocaleStringDriver>()?.Key.Value?.Contains("pin", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                prev.GetComponent<LayoutElement>().PreferredWidth.Value = 48f;
            }
        }

        if (Plugin.Invert.Value)
        {
            Button allowButton = ___actionsUi.Button(Plugin.AllowedNameplates.Value.Contains(__instance.SelectedContactId) ? "Remove NP" : "Add NP");
            allowButton.Slot.GetComponent<LayoutElement>().PreferredWidth.Value = size;
            allowButton.LocalPressed += (btn, _) =>
            {
                StringSet set = Plugin.AllowedNameplates.Value;
                Plugin.ToggleUser(__instance.SelectedContactId, set);
            
                btn.LabelTextField.Value = set.Contains(__instance.SelectedContactId) ? "Remove NP" : "Add NP";
            };
        }

        ___actionsUi.PopStyle();
    }
}

[HarmonyPatch(typeof(AvatarNameplateVisibilityDriver))]
public class NameplatePatch
{
    public static readonly List<AvatarNameplateVisibilityDriver> AvailableDrivers = new List<AvatarNameplateVisibilityDriver>();

    [HarmonyPatch("get_ShouldBeVisible"), HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> codes = new List<CodeInstruction>(instructions);

        MethodInfo getValue = AccessTools.PropertyGetter(typeof(SyncField<bool>), "Value");
        FieldInfo customNameplatesField = AccessTools.Field(typeof(NamePlateSettings), "UseCustomNameplates");

        for (int i = 0; i < codes.Count; i++)
        {
            CodeInstruction instruction = codes[i];

            yield return instruction;

            if (i > 0 && getValue != null && instruction.Calls(getValue) && customNameplatesField != null && codes[i - 1].LoadsField(customNameplatesField))
            {
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(NameplatePatch), nameof(ForceDefaultStyle)));
            }
        }
    }

    [HarmonyPatch("OnAwake"), HarmonyPostfix]
    public static void Postfix(AvatarNameplateVisibilityDriver __instance)
    {
        AvailableDrivers.Add(__instance);
        __instance.Destroyed += _ => AvailableDrivers.Remove(__instance);
    }

    public static bool ForceDefaultStyle(bool original, AvatarNameplateVisibilityDriver instance)
    {
        if (!Plugin.Enabled.Value) return original;
        
        User user = instance.Slot.ActiveUser;
        if (user == null) return original;

        string userId = user.UserID;
        if (string.IsNullOrEmpty(userId))
        {
            return original;
        }

        if (Plugin.Invert.Value && instance._settings != null && !instance._settings.UseCustomNameplates)
        {
            return Plugin.AllowedNameplates.Value.Contains(userId);
        }

        if (Plugin.BlockedNameplates.Value.Contains(userId))
        {
            return false;
        }

        return original;
    }
}

public class StringSet : HashSet<string>
{
    public StringSet() : base(StringComparer.Ordinal) { }

    public StringSet(IEnumerable<string> items) : base(items.Select(s => s.Trim()), StringComparer.Ordinal) { }

    public override string ToString() => string.Join(", ", this);

    public static StringSet Parse(string str) => new StringSet(str.Split(',', StringSplitOptions.TrimEntries));
}