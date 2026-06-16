using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Glamourer.Api.Enums;
using Glamourer.Api.Helpers;
using Glamourer.Api.IpcSubscribers;

namespace HOutfits;

/// <summary>
/// Thin wrapper over the Glamourer IPC. We only need three things:
///   - know Glamourer is present and a compatible version,
///   - push a single equipment piece onto an actor (SetItem),
///   - revert equipment back to game/automation state.
///
/// SetItem semantics (verified against Glamourer's ItemsApi.SetItem):
///   - The slot is NOT inferred from the item; the caller supplies it.
///     We resolve each item's slot ourselves (see OutfitService).
///   - ApplyFlag.Once  -> StateSource.IpcManual (transient; a redraw washes it
///                        out). This is try-on-like behaviour.
///   - no Once flag     -> StateSource.IpcFixed (sticks across redraws).
///   - key == 0         -> no lock; the user can still edit the slot afterwards
///                        in Glamourer's own UI.
///
/// To replicate "edit the dropdown in Glamourer by hand" (sticky, persists,
/// but not locked) we use:  flags = ApplyFlag.Equipment, key = 0  (NO Once).
/// </summary>
public sealed class GlamourerIpc
{
    private readonly ApiVersion _apiVersion;
    private readonly SetItem _setItem;
    private readonly RevertState _revert;

    public GlamourerIpc(IDalamudPluginInterface pi)
    {
        _apiVersion = new ApiVersion(pi);
        _setItem    = new SetItem(pi);
        _revert     = new RevertState(pi);
    }

    /// <summary>
    /// True if Glamourer is loaded and answering. We don't gate on a specific
    /// major: the SetItem.V3 / RevertState labels this plugin binds are stable
    /// across current Glamourer (2.x). If the version call throws, the subscriber
    /// isn't registered => Glamourer isn't loaded.
    /// </summary>
    public bool Available
    {
        get
        {
            try
            {
                return _apiVersion.Invoke().Major > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Apply one item to one slot on the given actor. Sticky, unlocked,
    /// equipment-only — matching a manual dropdown edit in Glamourer.
    /// </summary>
    public GlamourerApiEc ApplyItem(int objectIndex, ApiEquipSlot slot, ulong itemId)
        => _setItem.Invoke(
            objectIndex,
            slot,
            itemId,
            stain: [0, 0],             // leave dye untouched; SetItem needs a stain list
            key: 0,                    // no lock
            flags: ApplyFlag.Equipment // sticky (no Once), equipment only
        );

    /// <summary>
    /// Revert this actor fully back to game state — equipment AND customization,
    /// matching the in-game "/glamour revert &lt;me&gt;" command. Uses the library's
    /// own RevertDefault flag set (Equipment | Customization).
    /// </summary>
    public GlamourerApiEc Revert(int objectIndex)
        => _revert.Invoke(objectIndex, key: 0, flags: ApplyFlagEx.RevertDefault);
}
