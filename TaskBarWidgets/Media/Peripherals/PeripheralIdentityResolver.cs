namespace TaskBarWidgets.Media.Peripherals;

// Endpoint IDs remain provider-owned. Only proven links become public aliases.
internal sealed class PeripheralIdentityResolver
{
    internal const int MaximumAliases = 2048;
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    internal Dictionary<string, string> Aliases => new(_aliases, StringComparer.Ordinal);
    internal sealed record Decision(string LeftId, string RightId, string Reason, bool Merged);
    internal List<Decision> Decisions { get; } = [];

    public string ResolveId(string id) => _aliases.GetValueOrDefault(id, id);

    internal void LoadAliases(IReadOnlyDictionary<string, string> aliases)
    {
        _aliases.Clear();
        // Reject cycles, overlong chains and invalid IDs rather than choosing a
        // winner from corrupt cache data. Store only flattened, bounded links.
        foreach (var id in aliases.Keys.Order(StringComparer.Ordinal).Take(MaximumAliases))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { id };
            var target = id;
            while (aliases.TryGetValue(target, out var next))
            {
                if (!PeripheralIdentity.IsValid(next) || !seen.Add(next) || seen.Count > MaximumAliases)
                { target = ""; break; }
                target = next;
            }
            if (PeripheralIdentity.IsValid(id) && PeripheralIdentity.IsValid(target) && target != id)
                _aliases[id] = target;
        }
    }

    public PeripheralBatterySettings NormalizeSettings(PeripheralBatterySettings settings)
    {
        var normalized = settings.Clone();
        normalized.Normalize();
        normalized.Order = normalized.Order.Select(ResolveId).Distinct(StringComparer.Ordinal).ToList();
        normalized.CardVisibility = normalized.CardVisibility.GroupBy(p => ResolveId(p.Key), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => normalized.CardVisibility.TryGetValue(g.Key, out var explicitValue)
                ? explicitValue : g.All(p => p.Value), StringComparer.Ordinal);
        return normalized;
    }

    internal readonly record struct Endpoint(int Provider, string Id);
    internal sealed record Observation(Endpoint Endpoint, PeripheralDeviceSnapshot Device);
    private sealed record Node(Observation? Observation, string? HistoricalId,
        PeripheralIdentityEvidence[] Evidence, string[] Ids);

    internal Dictionary<Endpoint, string> ResolveBatch(IEnumerable<Observation> observations,
        IReadOnlyDictionary<string, PeripheralIdentityEvidence[]> history,
        IReadOnlyDictionary<Endpoint, string> previousRoutes, PeripheralBatterySettings settings, IReadOnlySet<string> taskbar)
    {
        Decisions.Clear();
        var current = observations.Where(o => PeripheralIdentity.IsValid(o.Endpoint.Id))
            .OrderBy(o => o.Endpoint.Provider).ThenBy(o => o.Endpoint.Id, StringComparer.Ordinal)
            .Take(1024).ToArray();
        foreach (var observation in current)
        {
            if (observation.Device.Identity is not { } evidence) continue;
            var target = ResolveId(observation.Endpoint.Id);
            if (history.TryGetValue(target, out var remembered) && remembered.Any(e => Conflicts(evidence, e)))
            {
                // Public aliases retain their old physical meaning even when a
                // provider reuses that raw endpoint. Candidate filtering below
                // gives the replacement a separate entity and internal route.
                if (Decisions.Count < 512) Decisions.Add(new(observation.Endpoint.Id, target, "historicalIdentityConflict", false));
            }
        }
        var nodes = current.Select(o => new Node(o, null,
                o.Device.Identity is { } evidence ? [evidence] : [],
                new[] { previousRoutes.GetValueOrDefault(o.Endpoint, o.Endpoint.Id), o.Endpoint.Id }
                    .Concat(o.Device.LegacyIds ?? []).Where(PeripheralIdentity.IsValid).Select(ResolveId)
                    .Where(id => o.Device.Identity is not { } evidence || !history.TryGetValue(id, out var remembered) ||
                        !remembered.Any(old => Conflicts(evidence, old)))
                    .Distinct(StringComparer.Ordinal).Take(32).ToArray())).ToList();
        nodes.AddRange(history.OrderBy(p => p.Key, StringComparer.Ordinal).Take(256)
            .Select(p => new Node(null, p.Key, p.Value.Take(16).ToArray(), [ResolveId(p.Key)])));
        var groups = Enumerable.Range(0, nodes.Count).Select(i => new List<int> { i }).ToArray();
        var owners = Enumerable.Range(0, nodes.Count).ToArray();
        var edges = new List<(int A, int B, int Strength, string Reason)>();
        // ponytail: bounded discovery batches permit a pair scan; index evidence
        // keys if the endpoint ceiling ever grows beyond 1024.
        for (var a = 0; a < nodes.Count; a++)
            for (var b = a + 1; b < nodes.Count; b++)
            {
                var left = nodes[a]; var right = nodes[b];
                var strength = left.Evidence.SelectMany(x => right.Evidence.Select(y => Proof(x, y))).DefaultIfEmpty(0).Max();
                var reason = strength switch { 5 => "endpoint", 4 => "uniqueSerial", 3 => "unit", 2 => "bluetooth", 1 => "deviceContainer", _ => "" };
                if (left.Observation is { } l && right.Observation is { } r &&
                    left.Ids.Intersect(right.Ids, StringComparer.Ordinal).Any() &&
                    (previousRoutes.TryGetValue(l.Endpoint, out var lId) && previousRoutes.TryGetValue(r.Endpoint, out var rId) &&
                        ResolveId(lId) == ResolveId(rId) ||
                     l.Endpoint.Id != r.Endpoint.Id && ResolveId(l.Endpoint.Id) == ResolveId(r.Endpoint.Id) ||
                     (l.Device.LegacyIds ?? []).Contains(r.Endpoint.Id, StringComparer.Ordinal) ||
                     (r.Device.LegacyIds ?? []).Contains(l.Endpoint.Id, StringComparer.Ordinal) ||
                     (l.Device.LegacyIds ?? []).Where(PeripheralIdentity.IsValid)
                         .Intersect(r.Device.LegacyIds ?? [], StringComparer.Ordinal).Any()))
                { strength = 6; reason = "provenAlias"; }
                if (strength > 0) edges.Add((a, b, strength, reason));
                else if (left.Evidence.Any(x => right.Evidence.Any(y => ProofCore(x, y) > 0 && Conflicts(x, y))))
                    RecordDecision(a, b, "conflictingEvidence", false);
                else if (left.Evidence.Any(x => right.Evidence.Any(y =>
                    Same(NormalizeEndpointPath(x.EndpointPath), NormalizeEndpointPath(y.EndpointPath)) &&
                    (!string.IsNullOrWhiteSpace(x.UnitId) || !string.IsNullOrWhiteSpace(y.UnitId)))))
                    RecordDecision(a, b, "receiverChildScope", false);
            }
        foreach (var (a, b, _, reason) in edges.OrderByDescending(e => e.Strength).ThenBy(e => e.A).ThenBy(e => e.B))
        {
            var merged = Join(a, b);
            RecordDecision(a, b, merged ? reason : "conflictingGroupEvidence", merged);
        }

        // Identity-less v1 records can restore a uniquely matching endpoint's
        // reading/selection, but cannot bridge two unrelated current endpoints.
        for (var index = 0; index < nodes.Count; index++)
        {
            if (nodes[index].HistoricalId is not { } id) continue;
            var matches = Enumerable.Range(0, nodes.Count).Where(i => nodes[i].Observation is not null &&
                nodes[i].Ids.Contains(ResolveId(id), StringComparer.Ordinal)).Select(i => owners[i]).Distinct().ToArray();
            if (matches.Length == 1) RecordDecision(index, matches[0], "historicalEndpoint", Join(index, matches[0]));
        }

        var active = groups.Where(g => g.Count > 0 && g.Any(i => nodes[i].Observation is not null)).ToArray();
        var claims = active.SelectMany((group, rank) => CandidateIds(group)
                .Select(id => (Id: id, Group: rank, HasHistory: group.Any(i => nodes[i].HistoricalId == id),
                    HasRoute: group.Any(i => nodes[i].Observation is { } observation &&
                        previousRoutes.GetValueOrDefault(observation.Endpoint) == id))))
            .GroupBy(p => p.Id, StringComparer.Ordinal).ToDictionary(g => g.Key,
                g => g.OrderByDescending(p => p.HasHistory).ThenByDescending(p => p.HasRoute).ThenBy(p => p.Group).First().Group,
                StringComparer.Ordinal);
        var routes = new Dictionary<Endpoint, string>();
        var additions = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var rank = 0; rank < active.Length; rank++)
        {
            var group = active[rank];
            var ids = CandidateIds(group)
                .Where(id => claims[id] == rank).ToArray();
            var survivor = ids.OrderBy(id => taskbar.Contains(id) ? 0 : 1)
                .ThenBy(id => { var rank = settings.Order.IndexOf(id); return rank < 0 ? int.MaxValue : rank; })
                .ThenBy(id => id, StringComparer.Ordinal).FirstOrDefault();
            if (survivor is null)
            {
                var endpoint = nodes[group.First(i => nodes[i].Observation is not null)].Observation!.Endpoint;
                survivor = PeripheralIdentity.FromKey($"endpoint:{endpoint.Provider}:{endpoint.Id}");
                var collision = 0;
                while (claims.ContainsKey(survivor) || history.ContainsKey(survivor) || used.Contains(survivor) ||
                    ResolveId(survivor) != survivor)
                    survivor = PeripheralIdentity.FromKey($"endpoint:{endpoint.Provider}:{endpoint.Id}:{++collision}");
            }
            if (!used.Add(survivor)) throw new InvalidOperationException("Duplicate peripheral entity identity");
            foreach (var id in ids.Where(id => id != survivor)) additions[id] = survivor;
            foreach (var index in group)
                if (nodes[index].Observation is { } observation) routes[observation.Endpoint] = survivor;
        }
        // Migrate aliases of a losing entity as well as its current endpoint IDs.
        var combined = new Dictionary<string, string>(_aliases, StringComparer.Ordinal);
        foreach (var (id, target) in additions) combined[id] = target;
        LoadAliases(combined);
        return routes;

        IEnumerable<string> CandidateIds(List<int> group) => group.SelectMany(i => nodes[i].Ids).Distinct(StringComparer.Ordinal)
            .Where(id => !history.TryGetValue(id, out var remembered) ||
                !group.SelectMany(i => nodes[i].Evidence).Any(evidence => remembered.Any(old => Conflicts(evidence, old))));

        bool Join(int a, int b)
        {
            a = owners[a]; b = owners[b];
            if (a == b) return true;
            if (groups[a].SelectMany(i => nodes[i].Evidence).Any(x =>
                groups[b].SelectMany(i => nodes[i].Evidence).Any(y => Conflicts(x, y)))) return false;
            foreach (var index in groups[b]) { owners[index] = a; groups[a].Add(index); }
            groups[b].Clear();
            return true;
        }
        void RecordDecision(int a, int b, string reason, bool merged)
        {
            if (Decisions.Count < 512) Decisions.Add(new(nodes[a].Observation?.Endpoint.Id ?? nodes[a].HistoricalId ?? "",
                nodes[b].Observation?.Endpoint.Id ?? nodes[b].HistoricalId ?? "", reason, merged));
        }
    }

    internal static bool Conflicts(PeripheralIdentityEvidence a, PeripheralIdentityEvidence b) =>
        Different(Address(a.BluetoothAddress), Address(b.BluetoothAddress)) ||
        Same(a.SerialScope, b.SerialScope) && Different(a.UniqueSerial, b.UniqueSerial) ||
        UnitScopesMatch(a, b) && Different(a.UnitId, b.UnitId);

    internal static int Proof(PeripheralIdentityEvidence a, PeripheralIdentityEvidence b)
    {
        if (Conflicts(a, b)) return 0;
        return ProofCore(a, b);
    }

    private static int ProofCore(PeripheralIdentityEvidence a, PeripheralIdentityEvidence b)
    {
        var sameUnit = string.IsNullOrWhiteSpace(a.UnitId) && string.IsNullOrWhiteSpace(b.UnitId) ||
            Same(a.UnitId, b.UnitId) && UnitScopesMatch(a, b);
        if (sameUnit && Same(NormalizeEndpointPath(a.EndpointPath), NormalizeEndpointPath(b.EndpointPath))) return 5;
        if (Same(a.SerialScope, b.SerialScope) && Same(a.UniqueSerial, b.UniqueSerial)) return 4;
        if (UnitScopesMatch(a, b) && Same(a.UnitId, b.UnitId)) return 3;
        if (Same(Address(a.BluetoothAddress), Address(b.BluetoothAddress))) return 2;
        if (sameUnit && a.ContainerIsDevice && b.ContainerIsDevice && Same(a.ContainerId, b.ContainerId)) return 1;
        return 0;
    }

    private static bool UnitScopesMatch(PeripheralIdentityEvidence a, PeripheralIdentityEvidence b) =>
        Same(a.UnitScope, b.UnitScope) && (!Same(a.UnitScope, "endpoint") ||
            Same(NormalizeEndpointPath(a.EndpointPath), NormalizeEndpointPath(b.EndpointPath)));

    private static string Address(string? value)
    {
        var address = new string((value ?? "").Where(Uri.IsHexDigit).ToArray());
        return address.Length == 12 && address.Any(c => c != '0') && !address.All(c => c is 'f' or 'F') ? address : "";
    }
    internal static string NormalizeEndpointPath(string? value)
    {
        var path = (value ?? "").Trim();
        var isInterface = path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal);
        if (isInterface) path = path[4..];
        var separator = path.LastIndexOf('#');
        if (separator >= 0 && Guid.TryParse(path[(separator + 1)..], out _)) path = path[..separator];
        path = path.Replace('#', '\\');
        separator = path.LastIndexOf('\\');
        if (isInterface && separator >= 0 && Guid.TryParse(path[(separator + 1)..], out _)) path = path[..separator];
        return path.ToLowerInvariant();
    }
    private static bool Same(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    private static bool Different(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && !Same(a, b);
}
