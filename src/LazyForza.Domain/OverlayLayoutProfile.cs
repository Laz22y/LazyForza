namespace LazyForza.Domain;

public sealed record OverlayLayoutProfile(Guid Id, string Name, OverlayLayout Layout);

public sealed record OverlayLayoutProfileCollection(
    int Version,
    Guid ActiveId,
    IReadOnlyList<OverlayLayoutProfile> Profiles);
