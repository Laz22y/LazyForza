# Embedded estate engine

Offline NuGet packages generated from the committed RaceServer Core and Protocol sources by `scripts/Sync-EstatePeerEngine.ps1`. Each package contains the unmodified C# sources under `source/`; `provenance.json` identifies their source revision and SHA-256 hashes. No runtime data or configuration is included.

Change authority rules in RaceServer and its Cloudflare equivalent, run their tests, commit, then regenerate these packages and update the central package versions together. Client builds and runtime do not require a sibling checkout or an external package service for this engine.

`scripts/Sync-EstatePeerControl.ps1` packages the native Web support classes and exact `wwwroot` assets from the same committed revision. Its generated wrapper extracts the native middleware, routes and helpers verbatim; the peer host supplies listeners, local authentication bootstrap and the existing coordinator. `control-provenance.json` records the input files. No separate handwritten copy of race-control endpoints or UI is maintained. Only the optional host references this package.
