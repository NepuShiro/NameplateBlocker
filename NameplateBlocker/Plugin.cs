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

    internal static ConfigEntry<StringList> BlockedNameplates;

    public override void Load()
    {
        Log = base.Log;

        TomlTypeConverter.AddConverter(typeof(StringList), new TypeConverter
        {
            ConvertToObject = (str, _) => StringList.Parse(str),
            ConvertToString = (obj, _) => ((StringList)obj).ToString()
        });

        BlockedNameplates = Config.Bind("General", "Blocked Nameplates", new StringList(), new ConfigDescription("Blocked Nameplates", null, "Hidden"));
        Config.Bind("General", "Update Current", default(dummy), new ConfigDescription("Updates current nameplates", null, UpdateVisibility, "Hidden"));

        HarmonyInstance.PatchAll();

        Log.LogInfo($"Plugin {PluginMetadata.GUID} is loaded!");
    }

    public static void ToggleUser(string id)
    {
        StringList blockedNames = BlockedNameplates.Value;

        if (!IsBlocked(id))
        {
            blockedNames.Add(id);
        }
        else
        {
            blockedNames.Remove(id);
        }

        Plugin.BlockedNameplates.Value = blockedNames;

        UpdateVisibility();
    }

    public static bool IsBlocked(string id) => BlockedNameplates.Value.Contains(id);

    public static void UpdateVisibility() => NameplatePatch.AvailableDrivers.ForEach(d => d.UpdateVisibility());
}

[HarmonyPatch(typeof(ContactsDialog), "UpdateSelectedContactUI")]
public class ContactsPatch
{
    [HarmonyPostfix]
    private static void PinUserAdder(ContactsDialog __instance, UIBuilder ___actionsUi)
    {
        if (!__instance.World.IsUserspace()) return;
        if (__instance.SelectedContact == null || __instance.SelectedContactId == __instance.Cloud.Platform.AppUserId || __instance.SelectedContact.IsSelfContact)
            return;

        ___actionsUi.PushStyle();

        Button pinButton = ___actionsUi.Button(Plugin.IsBlocked(__instance.SelectedContactId) ? "Unblock NP" : "Block NP");
        pinButton.Slot.GetComponent<LayoutElement>().PreferredWidth.Value = 48;

        int index = pinButton.Slot.ChildIndex;
        if (index > 0)
        {
            Slot prev = pinButton.Slot.Parent[index - 1];
            if (prev.ChildrenCount > 0 && (prev[0].GetComponent<LocaleStringDriver>()?.Key.Value?.Contains("pin", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                prev.GetComponent<LayoutElement>().PreferredWidth.Value = 48;
            }
        }

        pinButton.LocalPressed += (btn, _) =>
        {
            Plugin.ToggleUser(__instance.SelectedContactId);

            btn.LabelTextField.Value = Plugin.IsBlocked(__instance.SelectedContactId) ? "Unblock NP" : "Block NP";
        };

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
        User user = instance.Slot.ActiveUser;
        if (user == null) return original;

        string userId = user.UserID;

        if (string.IsNullOrEmpty(userId))
        {
            userId = user.UserName;
        }

        if (!string.IsNullOrEmpty(userId) && Plugin.IsBlocked(userId))
        {
            return false;
        }

        return original;
    }
}

public class StringList : List<string>
{
    public StringList() { }

    public StringList(IEnumerable<string> items) : base(items) { }

    public override string ToString() => string.Join(", ", this.Select(s => s.Trim()));

    public static StringList Parse(string str) => new StringList(str.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
}