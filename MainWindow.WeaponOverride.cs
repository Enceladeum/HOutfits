using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using CSCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using SceneWeapon = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Weapon;
using WeaponModelId = FFXIVClientStructs.FFXIV.Client.Game.Character.WeaponModelId;
using WeaponSlot = FFXIVClientStructs.FFXIV.Client.Game.Character.DrawDataContainer.WeaponSlot;

namespace HOutfits;

/// <summary>
/// Weapons override. Outside GPose, Glamourer only applies weapons of your current class's type. With Override on, a weapon
/// from another class is loaded straight onto your character through the game's own weapon loader, skipping Glamourer for
/// that slot. It only changes what is drawn (skipGameObject = 1): your character's stored weapon is left alone, so Restore
/// can reload it, and nothing about it differs for the server. Session only, never saved.
///
/// The override is published over IPC (HOutfits.WeaponOverride.*) so HMapSync can show it to the others in an HMS session.
/// A Penumbra redraw of your character reloads the stored weapon, so while an override is on it is drawn again once the
/// rebuilt weapon has settled (the rule HMapSync uses on peers): about a second of the class weapon, then the override.
/// A class change reloads the class weapon, so it ends the override, as does logging out.
/// </summary>
public sealed partial class MainWindow
{
    private bool _weaponOverride;
    private bool _weaponOverrideActive;   // Override is on and the class shown is one Glamourer would refuse

    // What is overridden, as WeaponModelId values (0 = not overridden; with a main hand, Off is always drawn with it and 0
    // means an empty off hand). Read by the IPC from any thread; written on the framework thread.
    private ulong _ovMain, _ovOff;
    private uint _ovJob;                  // the class the override was drawn on; changing class ends it
    private string _ovLastDrawn = "";
    private int _ovStableFrames;
    private const int SettleFrames = 10;

    private ICallGateProvider<(uint, uint)>? _ovVersionIpc;
    private ICallGateProvider<(ulong, ulong)>? _ovGetIpc;

    private bool OverrideOn => _ovMain != 0 || _ovOff != 0;

    private void InitWeaponOverride()
    {
        try
        {
            _ovVersionIpc = Plugin.PluginInterface.GetIpcProvider<(uint, uint)>("HOutfits.WeaponOverride.ApiVersion");
            _ovVersionIpc.RegisterFunc(() => (1u, 0u));
            _ovGetIpc = Plugin.PluginInterface.GetIpcProvider<(ulong, ulong)>("HOutfits.WeaponOverride.Get");
            _ovGetIpc.RegisterFunc(() => (_ovMain, _ovOff));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Couldn't register the weapon-override IPC; HMapSync won't see overrides.");
        }
        Plugin.Framework.Update += OnWeaponOverrideTick;
        Plugin.ClientState.Logout += OnLogoutClearOverride;
    }

    private void DisposeWeaponOverride()
    {
        Plugin.Framework.Update -= OnWeaponOverrideTick;
        Plugin.ClientState.Logout -= OnLogoutClearOverride;
        try { _ovGetIpc?.UnregisterFunc(); } catch { }
        try { _ovVersionIpc?.UnregisterFunc(); } catch { }
        if (OverrideOn)
        {
            try { RestoreClassWeapon(); } catch { }   // don't leave another class's weapon drawn after an unload
        }
        ClearOverride();
    }

    private void OnLogoutClearOverride(int type, int code) => ClearOverride();

    private void ClearOverride()
    {
        _ovMain = _ovOff = 0;
        _ovLastDrawn = "";
        _ovStableFrames = 0;
    }

    private void DrawWeaponOverride(ref bool canApply)
    {
        if (_player.InGpose)
            return;                       // Glamourer already applies every weapon in GPose

        ImGui.SameLine();
        if (ImGui.Checkbox("Override", ref _weaponOverride) && !_weaponOverride && OverrideOn)
            RestoreClassWeapon();         // turning Override off puts your class weapon back
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Load other classes' weapons straight onto your character, bypassing Glamourer's class check.\n" +
                             "Only what is drawn changes; others see it in an HMapSync session. Some animations may look wrong.\n" +
                             "Restore (or changing class) puts your class weapon back.");
        if (_weaponOverride)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Restore"))
                RestoreClassWeapon();
        }

        _weaponOverrideActive = _weaponOverride && !canApply;
        if (_weaponOverrideActive)
            canApply = true;
    }

    private void TryApplyWeaponOverride(GearItem item, ref bool handled)
    {
        if (!_weaponOverrideActive || !item.IsWeapon)
            return;
        handled = true;

        unsafe
        {
            var chara = LocalCharacter();
            if (chara == null)
            {
                SetStatus(false, string.Empty, "No character loaded.");
                return;
            }

            // A main hand brings its own second model (arrows, the other dagger, a fist's off piece) or an empty off hand,
            // as the game does when you equip it; an off-hand tile only replaces the off hand.
            if (item.Slot == GearSlot.MainHand)
            {
                _ovMain = item.Model;
                _ovOff  = item.ModelSub;
            }
            else
            {
                _ovOff = item.Model;
            }
            _ovJob = _player.ActiveJobId;
            _ovLastDrawn = "";
            _ovStableFrames = 0;
            LoadOverride(chara);
        }

        SetStatus(true, $"Override: loaded \"{item.Name}\" ({SlotNames[(int)item.Slot]}). Restore puts your class weapon back.",
            string.Empty);
    }

    private void RestoreClassWeapon()
    {
        ClearOverride();
        unsafe
        {
            var chara = LocalCharacter();
            if (chara == null)
                return;

            // The stored weapon was never overwritten, so reloading it draws your real weapon again; Glamourer's own weapon
            // hook then layers your Glamourer weapon on top as usual.
            chara->DrawData.LoadWeapon(WeaponSlot.MainHand, chara->DrawData.Weapon(WeaponSlot.MainHand).ModelId, 1, 0, 1, 0, false);
            chara->DrawData.LoadWeapon(WeaponSlot.OffHand, chara->DrawData.Weapon(WeaponSlot.OffHand).ModelId, 1, 0, 1, 0, false);
        }

        SetStatus(true, "Override: your class weapon is back.", string.Empty);
    }

    /// <summary>
    /// Keeps the override drawn: after a redraw rebuilds the weapon, waits until it has settled (a weapon is drawn, it is
    /// not half-loaded, and it hasn't changed for SettleFrames frames) and draws the override again once. Writing earlier,
    /// while the weapon is missing or half-loaded, is lost. A class change ends the override instead.
    /// </summary>
    private void OnWeaponOverrideTick(IFramework _)
    {
        if (!OverrideOn)
            return;
        if (_player.ActiveJobId != _ovJob)
        {
            ClearOverride();              // the game loads the new class's weapon itself
            return;
        }

        unsafe
        {
            var chara = LocalCharacter();
            if (chara == null)
                return;
            var drawn = DrawnWeaponKey(chara);
            if (drawn != _ovLastDrawn)
            {
                _ovLastDrawn = drawn;
                _ovStableFrames = 0;
                return;
            }
            if (++_ovStableFrames != SettleFrames)
                return;
            if (OverrideDrawn(chara) || !WeaponSettled(chara))
                return;
            LoadOverride(chara);
        }
    }

    private unsafe void LoadOverride(CSCharacter* chara)
    {
        if (_ovMain != 0)
            Load(chara, WeaponSlot.MainHand, _ovMain);
        Load(chara, WeaponSlot.OffHand, _ovOff);
    }

    private static unsafe (ushort Set, ushort Secondary, ushort Variant)? DrawnWeapon(CSCharacter* chara, WeaponSlot slot)
    {
        var obj = chara->DrawData.Weapon(slot).DrawData.DrawObject;
        if (obj == null)
            return null;
        var w = (SceneWeapon*)obj;
        return (w->ModelSetId, w->SecondaryId, w->Variant);
    }

    private static unsafe string DrawnWeaponKey(CSCharacter* chara)
    {
        static string K((ushort Set, ushort Secondary, ushort Variant)? d) => d is { } v ? $"{v.Set}-{v.Secondary}-{v.Variant}" : "none";
        return K(DrawnWeapon(chara, WeaponSlot.MainHand)) + "/" + K(DrawnWeapon(chara, WeaponSlot.OffHand));
    }

    /// <summary>Whether what is drawn now is the override (set, secondary id and variant; dyes are not on the draw ids).</summary>
    private unsafe bool OverrideDrawn(CSCharacter* chara)
    {
        static bool Same((ushort Set, ushort Secondary, ushort Variant)? d, ulong model)
        {
            ushort set = (ushort)(model & 0xFFFF), type = (ushort)((model >> 16) & 0xFFFF), variant = (ushort)((model >> 32) & 0xFFFF);
            if (set == 0)
                return d == null || d.Value.Set == 0;    // an empty slot wanted
            return d is { } v && v.Set == set && v.Secondary == type && v.Variant == variant;
        }
        bool mainOk = _ovMain == 0 || Same(DrawnWeapon(chara, WeaponSlot.MainHand), _ovMain);
        return mainOk && Same(DrawnWeapon(chara, WeaponSlot.OffHand), _ovOff);
    }

    /// <summary>The slot we drive has a weapon drawn and it is not half-loaded (secondary id and variant both 0 while loading).</summary>
    private unsafe bool WeaponSettled(CSCharacter* chara)
    {
        var d = DrawnWeapon(chara, _ovMain != 0 ? WeaponSlot.MainHand : WeaponSlot.OffHand);
        return d is { } v && !(v.Secondary == 0 && v.Variant == 0);
    }

    /// <summary>Item.ModelMain / ModelSub pack set, base and variant in the low 48 bits, the same order as a weapon model.</summary>
    private static unsafe void Load(CSCharacter* chara, WeaponSlot slot, ulong model)
    {
        var id = new WeaponModelId
        {
            Id      = (ushort)(model & 0xFFFF),
            Type    = (ushort)((model >> 16) & 0xFFFF),
            Variant = (ushort)((model >> 32) & 0xFFFF),
        };
        chara->DrawData.LoadWeapon(slot, id, 1, 0, 1, 0, false);   // redrawOnEquality = 1, skipGameObject = 1
    }

    private static unsafe CSCharacter* LocalCharacter()
    {
        var gom = GameObjectManager.Instance();
        if (gom == null)
            return null;
        var obj = gom->Objects.IndexSorted[0].Value;
        return obj != null && obj->ObjectKind == ObjectKind.Pc ? (CSCharacter*)obj : null;
    }
}
