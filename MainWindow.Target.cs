using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using CSCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using CharacterBase = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase;
using WeaponModelId = FFXIVClientStructs.FFXIV.Client.Game.Character.WeaponModelId;
using WeaponSlot = FFXIVClientStructs.FFXIV.Client.Game.Character.DrawDataContainer.WeaponSlot;

namespace HOutfits;

/// <summary>
/// The dressing target: another human actor (an HDM puppet, say) that every apply goes to instead of you. Set over IPC
/// (HOutfits.Target.*, for HDM's "Dress..." button); a banner at the top of the window shows it, with Back to me.
///
/// - Every Glamourer call takes <see cref="TargetIndex"/> (0 = you). Glamourer makes a state for any actor it can identify.
/// - Weapons on a target skip Glamourer (its class check would turn most of them down on a puppet) and load straight through
///   the game's weapon loader, for any class; like the Override, only what is drawn changes.
/// - The Faces tab reads the target's body. Your class chip, GPose rules, the Override and the Moniker nameplate stay
///   about you.
/// - The target is dropped (back to you, silently) when its slot empties or holds another object, when it has had no
///   body for a few seconds, or on a zone change, so a stale index never dresses a recycled one.
/// </summary>
public sealed partial class MainWindow
{
    private int _targetIndex = -1;                 // -1 = yourself
    private string _targetLabel = string.Empty;
    private nint _targetAddress;                   // the object the index pointed at when it was set
    private string _targetName = string.Empty;
    private long _targetNoBodySince;               // Environment.TickCount64 when its draw object went missing; 0 = it has one
    private const long NoBodyGraceMs = 3000;       // a redraw (an HDM Look, a Penumbra redraw) drops the body briefly

    private ICallGateProvider<(uint, uint)>? _tgVersionIpc;
    private ICallGateProvider<int, string, bool>? _tgSetIpc;
    private ICallGateProvider<object?>? _tgClearIpc;
    private ICallGateProvider<int>? _tgGetIpc;

    /// <summary>True while dressing someone other than you.</summary>
    private bool Targeting => _targetIndex > 0;

    /// <summary>The object index every apply goes to: the target, or 0 (you).</summary>
    private int TargetIndex => Targeting ? _targetIndex : 0;

    private void InitTarget()
    {
        try
        {
            _tgVersionIpc = Plugin.PluginInterface.GetIpcProvider<(uint, uint)>("HOutfits.Target.ApiVersion");
            _tgVersionIpc.RegisterFunc(() => (1u, 0u));
            _tgSetIpc = Plugin.PluginInterface.GetIpcProvider<int, string, bool>("HOutfits.Target.Set");
            _tgSetIpc.RegisterFunc(SetTarget);
            _tgClearIpc = Plugin.PluginInterface.GetIpcProvider<object?>("HOutfits.Target.Clear");
            _tgClearIpc.RegisterAction(ClearTarget);
            _tgGetIpc = Plugin.PluginInterface.GetIpcProvider<int>("HOutfits.Target.Get");
            _tgGetIpc.RegisterFunc(() => Targeting ? _targetIndex : -1);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Couldn't register the dressing-target IPC; HDM's Dress button won't work.");
        }
        Plugin.Framework.Update += OnTargetTick;
        Plugin.ClientState.TerritoryChanged += OnTerritoryChangedClearTarget;
    }

    private void DisposeTarget()
    {
        Plugin.Framework.Update -= OnTargetTick;
        Plugin.ClientState.TerritoryChanged -= OnTerritoryChangedClearTarget;
        try { _tgGetIpc?.UnregisterFunc(); } catch { }
        try { _tgClearIpc?.UnregisterAction(); } catch { }
        try { _tgSetIpc?.UnregisterFunc(); } catch { }
        try { _tgVersionIpc?.UnregisterFunc(); } catch { }
    }

    /// <summary>
    /// HOutfits.Target.Set: dress the actor at <paramref name="objectIndex"/> from now on and open the window. Your own index
    /// (0) means back to you, and open. False, with nothing changed, unless the index holds a live human character.
    /// </summary>
    private bool SetTarget(int objectIndex, string label)
    {
        try
        {
            if (objectIndex == 0)
            {
                ClearTarget();
                IsOpen = true;
                return true;
            }

            unsafe
            {
                var chara = HumanAt(objectIndex);
                if (chara == null)
                    return false;
                _targetIndex       = objectIndex;
                _targetAddress     = (nint)chara;
                _targetName        = chara->NameString;
                _targetLabel       = string.IsNullOrWhiteSpace(label) ? _targetName : label;
                _targetNoBodySince = 0;
            }
            OnTargetChanged();
            IsOpen = true;
            SetStatus(true, $"Dressing {_targetLabel}.", string.Empty);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Dressing target: Set({Index}) failed.", objectIndex);
            return false;
        }
    }

    /// <summary>HOutfits.Target.Clear, Back to me, and every automatic drop: dress yourself again.</summary>
    private void ClearTarget()
    {
        if (!Targeting)
            return;
        _targetIndex   = -1;
        _targetLabel   = _targetName = string.Empty;
        _targetAddress = 0;
        OnTargetChanged();
    }

    private void OnTargetChanged()
    {
        _npcState.Target = TargetIndex;
        _mineNextRead    = 0;   // the Faces tab reads the new body next frame
    }

    private void OnTerritoryChangedClearTarget(uint _) => ClearTarget();

    /// <summary>Drops the target once it is gone: its slot empty or holding another object, or no body for a few seconds.</summary>
    private void OnTargetTick(IFramework _)
    {
        if (!Targeting)
            return;
        unsafe
        {
            var gom = GameObjectManager.Instance();
            var obj = gom == null ? null : gom->Objects.IndexSorted[_targetIndex].Value;
            if (obj == null || (nint)obj != _targetAddress || !obj->IsCharacter() || obj->NameString != _targetName)
            {
                ClearTarget();
                return;
            }
            if (obj->DrawObject != null)
            {
                _targetNoBodySince = 0;
                return;
            }
            var now = Environment.TickCount64;
            if (_targetNoBodySince == 0)
                _targetNoBodySince = now;
            else if (now - _targetNoBodySince > NoBodyGraceMs)
                ClearTarget();
        }
    }

    /// <summary>The character at the index if it is drawn as a human (Glamourer can dress it), else null.</summary>
    private static unsafe CSCharacter* HumanAt(int objectIndex)
    {
        var gom = GameObjectManager.Instance();
        if (gom == null || objectIndex < 0 || objectIndex >= gom->Objects.IndexSorted.Length)
            return null;
        var obj = gom->Objects.IndexSorted[objectIndex].Value;
        if (obj == null || !obj->IsCharacter() || obj->DrawObject == null)
            return null;
        var body = (CharacterBase*)obj->DrawObject;
        return body->GetModelType() == CharacterBase.ModelType.Human ? (CSCharacter*)obj : null;
    }

    /// <summary>The "Dressing: Alyva (HDM puppet) · Back to me" banner, only while a target is set.</summary>
    private void DrawTargetBanner()
    {
        if (!Targeting)
            return;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(new Vector4(0.45f, 0.80f, 0.95f, 1f), $"Dressing: {_targetLabel}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Back to me"))
        {
            ClearTarget();
            SetStatus(true, "Dressing yourself again.", string.Empty);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Everything you click now applies to this character instead of you. Back to me dresses you again.");
    }

    /// <summary>
    /// A weapon onto the target through the game's own weapon loader, any class (Glamourer's class check would refuse most on
    /// a puppet). Model packs set, type and variant like Item.ModelMain. Only what is drawn changes.
    /// </summary>
    private bool LoadWeaponOnTarget(WeaponSlot slot, ulong model)
    {
        unsafe
        {
            var chara = HumanAt(_targetIndex);
            if (chara == null)
                return false;
            var id = new WeaponModelId
            {
                Id      = (ushort)(model & 0xFFFF),
                Type    = (ushort)((model >> 16) & 0xFFFF),
                Variant = (ushort)((model >> 32) & 0xFFFF),
            };
            chara->DrawData.LoadWeapon(slot, id, 1, 0, 1, 0, false);   // redrawOnEquality = 1, skipGameObject = 1
            return true;
        }
    }

    private static WeaponSlot WeaponSlotOf(Glamourer.Api.Enums.ApiEquipSlot slot)
        => slot == Glamourer.Api.Enums.ApiEquipSlot.OffHand ? WeaponSlot.OffHand : WeaponSlot.MainHand;

    /// <summary>An NPC weapon piece as a weapon model (set, type, variant), the layout <see cref="LoadWeaponOnTarget"/> takes.</summary>
    private static ulong NpcWeaponModel(NpcPiece piece)
        => piece.Model | ((ulong)piece.Secondary << 16) | ((ulong)piece.Variant << 32);

    /// <summary>After a Revert on the target: draw its own stored weapons again (undoes any weapon loaded onto it here).</summary>
    private void RestoreTargetWeapons()
    {
        unsafe
        {
            var chara = HumanAt(_targetIndex);
            if (chara == null)
                return;
            chara->DrawData.LoadWeapon(WeaponSlot.MainHand, chara->DrawData.Weapon(WeaponSlot.MainHand).ModelId, 1, 0, 1, 0, false);
            chara->DrawData.LoadWeapon(WeaponSlot.OffHand, chara->DrawData.Weapon(WeaponSlot.OffHand).ModelId, 1, 0, 1, 0, false);
        }
    }
}
