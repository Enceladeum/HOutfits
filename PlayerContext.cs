using Dalamud.Plugin.Services;

namespace HOutfits;

/// <summary>
/// What the Weapons tab needs to know about the player right now: which class or job they are on, and whether they are in
/// GPose (where Glamourer lifts its restriction on weapons). Read from the UI thread, once per frame, so it is never stale.
/// </summary>
public sealed class PlayerContext
{
    private readonly IPlayerState _player;
    private readonly IClientState _client;

    public PlayerContext(IPlayerState player, IClientState client)
    {
        _player = player;
        _client = client;
    }

    /// <summary>The ClassJob row id of the class or job the player is on, or 0 when no character is loaded (the title screen).</summary>
    public uint ActiveJobId
    {
        get
        {
            try { return _player.IsLoaded ? _player.ClassJob.RowId : 0u; }
            catch { return 0u; }
        }
    }

    public bool InGpose
    {
        get
        {
            try { return _client.IsGPosing; }
            catch { return false; }
        }
    }
}
