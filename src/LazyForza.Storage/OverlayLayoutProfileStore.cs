using System.Text.Json;
using LazyForza.Domain;

namespace LazyForza.Storage;

/// <summary>Named HUD configurations. The legacy layout and the active profile commit together.</summary>
public sealed class OverlayLayoutProfileStore
{
    public const string SettingKey = "overlay.profiles";
    public const int MaximumNameLength = 40;
    public const int MaximumProfiles = 32;
    private readonly LazyForzaStore store;
    private readonly string? recoveryJson;

    public OverlayLayoutProfileStore(LazyForzaStore store, OverlayLayout current, string initialName)
    {
        this.store = store;
        var json = store.GetAppSetting(SettingKey);
        try
        {
            if (json is not null)
            {
                var loaded = JsonSerializer.Deserialize<OverlayLayoutProfileCollection>(json);
                if (loaded is not null && IsValid(loaded))
                {
                    // overlay.layout remains authoritative for older clients and existing backups.
                    State = loaded with { Profiles = loaded.Profiles.Select(profile => profile with
                    {
                        Layout = Normalize(profile.Id == loaded.ActiveId ? current : profile.Layout)
                    }).ToArray() };
                    return;
                }
                recoveryJson = json;
            }
        }
        catch (JsonException) { recoveryJson = json; }
        var first = new OverlayLayoutProfile(Guid.NewGuid(), initialName, Normalize(current));
        State = new(1, first.Id, [first]);
    }

    public OverlayLayoutProfileCollection State { get; private set; }
    public OverlayLayoutProfile Active => State.Profiles.Single(profile => profile.Id == State.ActiveId);

    public bool CanUseName(string? name, Guid? exceptId = null) =>
        !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= MaximumNameLength &&
        !name.Any(char.IsControl) && !State.Profiles.Any(profile => profile.Id != exceptId &&
            string.Equals(profile.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    public void SaveCurrent(OverlayLayout layout) => Commit(State with
    {
        Profiles = State.Profiles.Select(profile => profile.Id == State.ActiveId
            ? profile with { Layout = Normalize(layout) } : profile).ToArray()
    });

    public void Create(string name)
    {
        if (State.Profiles.Count >= MaximumProfiles) throw new InvalidOperationException("Too many HUD layouts.");
        ValidateName(name);
        var profile = new OverlayLayoutProfile(Guid.NewGuid(), name.Trim(), Active.Layout);
        Commit(State with { ActiveId = profile.Id, Profiles = [.. State.Profiles, profile] });
    }

    public void Rename(string name)
    {
        ValidateName(name, State.ActiveId);
        Commit(State with { Profiles = State.Profiles.Select(profile => profile.Id == State.ActiveId
            ? profile with { Name = name.Trim() } : profile).ToArray() });
    }

    public void Select(Guid id)
    {
        if (!State.Profiles.Any(profile => profile.Id == id)) throw new ArgumentOutOfRangeException(nameof(id));
        Commit(State with { ActiveId = id });
    }

    public void DeleteActive()
    {
        if (State.Profiles.Count == 1) throw new InvalidOperationException("The last HUD layout cannot be deleted.");
        var profiles = State.Profiles.Where(profile => profile.Id != State.ActiveId).ToArray();
        Commit(State with { ActiveId = profiles[0].Id, Profiles = profiles });
    }

    private void ValidateName(string name, Guid? exceptId = null)
    {
        if (!CanUseName(name, exceptId)) throw new ArgumentException("Invalid or duplicate HUD layout name.", nameof(name));
    }

    private void Commit(OverlayLayoutProfileCollection next)
    {
        var settings = new Dictionary<string, string>
        {
            [SettingKey] = JsonSerializer.Serialize(next),
            ["overlay.layout"] = JsonSerializer.Serialize(next.Profiles.Single(profile => profile.Id == next.ActiveId).Layout)
        };
        if (recoveryJson is not null) settings["overlay.profiles.recovery"] = recoveryJson;
        store.SetAppSettings(settings);
        State = next;
    }

    private static OverlayLayout Normalize(OverlayLayout layout) => OverlayLayoutGeometry.Normalize(layout) with
    {
        IsLocked = true, ClickThrough = true
    };

    private static bool IsValid(OverlayLayoutProfileCollection value) =>
        value.Version == 1 && value.Profiles is { Count: > 0 and <= MaximumProfiles } &&
        value.Profiles.All(profile => profile is not null && profile.Id != Guid.Empty && profile.Layout is not null &&
            !string.IsNullOrWhiteSpace(profile.Name) && profile.Name.Length <= MaximumNameLength && !profile.Name.Any(char.IsControl)) &&
        value.Profiles.Select(profile => profile.Id).Distinct().Count() == value.Profiles.Count &&
        value.Profiles.Select(profile => profile.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == value.Profiles.Count &&
        value.Profiles.Any(profile => profile.Id == value.ActiveId);
}
