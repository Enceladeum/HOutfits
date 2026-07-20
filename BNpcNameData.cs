using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;

namespace HOutfits;

/// <summary>
/// Provides the BNpcBase -> BNpcName-id mapping needed to name battle NPCs.
///
/// This mapping does NOT exist in the game's own sheets — BNpcBase links to
/// BNpcCustomize and NpcEquip but not to BNpcName. The association is derived
/// externally (game-observed spawn data, compiled by the community). We fetch it
/// at runtime from the upstream community source and cache it to disk so the NPC
/// tab works offline after the first successful fetch.
///
/// Data source (factual game-observed BNpc/name pairs), same upstream Penumbra
/// itself cites:
///   https://github.com/Infiziert90/FFXIVGachaSpreadsheet
/// Shape: [ { "Base": bnpcBaseRowId, "Names": [ bnpcNameRowId, ... ] }, ... ]
///
/// Fetch-and-cache: on load, use the on-disk cache if present (instant, offline-
/// safe) and kick a background refresh; if no cache exists, fetch once. A failed
/// fetch is non-fatal — battle NPCs simply won't be named until a fetch succeeds,
/// and event NPCs (which don't need this) are unaffected.
/// </summary>
public sealed class BNpcNameData
{
    private const string UpstreamUrl =
        "https://raw.githubusercontent.com/Infiziert90/FFXIVGachaSpreadsheet/master/website/static/data/BnpcPairsSimple.json";

    private readonly IPluginLog _log;
    private readonly string _cachePath;

    // BNpcBase RowId -> list of BNpcName RowIds. Empty until loaded.
    private Dictionary<uint, List<uint>> _map = new();
    private volatile bool _ready;

    public BNpcNameData(IPluginLog log)
    {
        _log = log;
        var dir = Plugin.PluginInterface.GetPluginConfigDirectory();
        Directory.CreateDirectory(dir);
        _cachePath = Path.Combine(dir, "bnpc_names.json");
    }

    public bool Ready => _ready;

    /// <summary>
    /// Raised (on a background thread) after a successful upstream refresh that
    /// changed the data — lets the NPC list rebuild to include battle NPCs. Not
    /// raised for the initial cache load (the list is built fresh anyway).
    /// </summary>
    public event Action? OnRefreshed;

    /// <summary>Name ids for a BNpcBase, or empty if unknown / not yet loaded.</summary>
    public IReadOnlyList<uint> NamesFor(uint bnpcBaseId)
        => _map.TryGetValue(bnpcBaseId, out var names) ? names : Array.Empty<uint>();

    /// <summary>
    /// Load the mapping. Uses the disk cache immediately if present, then refreshes
    /// in the background; otherwise fetches once. Safe to call at startup — never
    /// blocks the UI thread and never throws.
    /// </summary>
    public void Load()
    {
        // Load cache synchronously if we have one (fast, local).
        if (File.Exists(_cachePath))
        {
            try
            {
                var cached = File.ReadAllText(_cachePath);
                Parse(cached);
                _ready = true;
                _log.Information("Loaded BNpc name map from cache ({Count} bases).", _map.Count);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "BNpc name cache unreadable; will refetch.");
            }
        }

        // Refresh (or first fetch) in the background.
        Task.Run(RefreshAsync);
    }

    private async Task RefreshAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var json = await http.GetStringAsync(UpstreamUrl).ConfigureAwait(false);

            Parse(json);
            _ready = true;

            // Persist for offline use next time.
            try { await File.WriteAllTextAsync(_cachePath, json).ConfigureAwait(false); }
            catch (Exception ex) { _log.Warning(ex, "Couldn't write BNpc name cache."); }

            _log.Information("Refreshed BNpc name map from upstream ({Count} bases).", _map.Count);
            OnRefreshed?.Invoke();
        }
        catch (Exception ex)
        {
            // Non-fatal: keep whatever cache we loaded (if any). Battle NPCs just
            // won't be named until a fetch succeeds.
            _log.Warning(ex, "BNpc name map fetch failed; battle NPCs may be unnamed this session.");
        }
    }

    private void Parse(string json)
    {
        var entries = JsonConvert.DeserializeObject<List<Entry>>(json) ?? new List<Entry>();
        var map = new Dictionary<uint, List<uint>>(entries.Count);
        foreach (var e in entries)
        {
            if (e.Names is null || e.Names.Count == 0)
                continue;
            // Drop 0 name ids (they resolve to empty names).
            var names = new List<uint>(e.Names.Count);
            foreach (var n in e.Names)
                if (n != 0)
                    names.Add(n);
            if (names.Count > 0)
                map[e.Base] = names;
        }
        _map = map;
    }

    private sealed class Entry
    {
        public uint Base { get; set; }
        public List<uint>? Names { get; set; }
    }
}
