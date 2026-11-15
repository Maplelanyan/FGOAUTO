using FgoAuto.Models;

namespace FgoAuto.Services;

public sealed record CommandCardCandidate(int Slot, ImageClickRule Rule, ImageMatchHit Hit)
{
    public string PositionDescription => Rule.IsNoblePhantasm ? $"上方第 {Slot - 4} 张宝具" : $"下方第 {Slot + 1} 张普通卡";
}
public sealed record CommandCardBatchResult(int ClickedCount, ImageClickRule? LimitRule, bool ScreenChanged = false);

/// <summary>Chooses up to three Noble Phantasms first, then fills the hand with distinct ordinary cards.</summary>
public sealed class CommandCardSession
{
    private readonly HashSet<int> _clickedSlots = [];
    private readonly Dictionary<int, (string Id, int X, int Y)> _nobleAnchors = [];
    private DateTime? _missingSince;
    private int _missingFrames;
    private DateTime? _firstVisibleAt;

    public int VisibleCardCount { get; private set; }
    public int VisibleNoblePhantasmCount { get; private set; }
    public int VisibleNormalCardCount { get; private set; }
    public bool WaitingForSettle { get; private set; }
    public bool IsComplete => _clickedSlots.Count >= 3;
    public bool HasSelectedCards => _clickedSlots.Count > 0;

    public IReadOnlyList<CommandCardCandidate> Plan(
        IEnumerable<(ImageClickRule Rule, ImageMatchHit Hit)> matches,
        int screenWidth, int screenHeight, DateTime now, Random? random = null, bool waitForNoblePhantasms = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(screenWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(screenHeight);
        var allMatches = matches.ToArray();
        var normalCards = allMatches
            .Where(candidate => candidate.Rule.IsCommandCard
                && candidate.Hit.CenterX >= 0 && candidate.Hit.CenterX < screenWidth
                && candidate.Hit.CenterY >= screenHeight / 2 && candidate.Hit.CenterY < screenHeight)
            // There are five command-card positions. Multiple templates can describe the same card.
            .GroupBy(candidate => Math.Clamp((int)(candidate.Hit.CenterX * 5d / screenWidth), 0, 4))
            .Select(group =>
            {
                var best = group.MaxBy(candidate => candidate.Hit.Score);
                return new CommandCardCandidate(group.Key, best.Rule, best.Hit);
            }).ToArray();
        // Upper cards can be centred differently when only one or two are available, so deduplicate by distance.
        var phantasmHits = new List<(ImageClickRule Rule, ImageMatchHit Hit)>();
        foreach (var candidate in allMatches.Where(candidate => candidate.Rule.IsNoblePhantasm
            && candidate.Hit.CenterX >= 0 && candidate.Hit.CenterX < screenWidth
            && candidate.Hit.CenterY >= 0 && candidate.Hit.CenterY < screenHeight / 2)
            .OrderByDescending(candidate => candidate.Hit.Score))
        {
            if (phantasmHits.Any(existing => Math.Abs(existing.Hit.CenterX - candidate.Hit.CenterX) < screenWidth * 0.10
                && Math.Abs(existing.Hit.CenterY - candidate.Hit.CenterY) < screenHeight * 0.20))
                continue;
            phantasmHits.Add(candidate);
            if (phantasmHits.Count == 3)
                break;
        }
        var phantasms = new List<CommandCardCandidate>();
        var usedSlots = new HashSet<int>();
        foreach (var candidate in phantasmHits.OrderBy(candidate => candidate.Hit.CenterX))
        {
            var prior = _nobleAnchors.Where(anchor => !usedSlots.Contains(anchor.Key))
                .Where(anchor => anchor.Value.Id == candidate.Rule.Target.Id
                    || Math.Abs(anchor.Value.X - candidate.Hit.CenterX) < screenWidth * 0.10)
                .OrderBy(anchor => anchor.Value.Id == candidate.Rule.Target.Id ? 0 : 1)
                .ThenBy(anchor => Math.Abs(anchor.Value.X - candidate.Hit.CenterX)).ToArray();
            var slot = prior.Length > 0 ? prior[0].Key : Enumerable.Range(5, 3).FirstOrDefault(key => !_nobleAnchors.ContainsKey(key), -1);
            if (slot < 0) continue;
            usedSlots.Add(slot);
            _nobleAnchors[slot] = (candidate.Rule.Target.Id, candidate.Hit.CenterX, candidate.Hit.CenterY);
            phantasms.Add(new CommandCardCandidate(slot, candidate.Rule, candidate.Hit));
        }
        var visible = phantasms.Concat(normalCards).ToArray();
        VisibleNoblePhantasmCount = phantasms.Count;
        VisibleNormalCardCount = normalCards.Length;
        VisibleCardCount = visible.Length;
        WaitingForSettle = false;

        if (VisibleCardCount == 0)
        {
            _missingSince ??= now;
            _missingFrames++;
            // A single missed match/selection animation must not arm the same hand again.
            if (_missingFrames >= 2 && now - _missingSince.Value >= TimeSpan.FromSeconds(1))
            {
                _clickedSlots.Clear();
                _nobleAnchors.Clear();
                _firstVisibleAt = null;
            }
        }
        else
        {
            _missingSince = null;
            _missingFrames = 0;
            _firstVisibleAt ??= now;
        }

        if (IsComplete)
            return [];
        if (waitForNoblePhantasms && VisibleCardCount > 0 && now - _firstVisibleAt!.Value < TimeSpan.FromMilliseconds(250))
        {
            WaitingForSettle = true;
            return [];
        }
        var available = visible.Where(candidate => !_clickedSlots.Contains(candidate.Slot)).ToArray();
        var remaining = 3 - _clickedSlots.Count;
        if (available.Length < remaining)
            return [];
        var priority = available.Where(candidate => candidate.Rule.IsNoblePhantasm).OrderBy(candidate => candidate.Hit.CenterX).Take(remaining).ToArray();
        var ordinary = available.Where(candidate => !candidate.Rule.IsNoblePhantasm).ToArray();
        (random ?? Random.Shared).Shuffle(ordinary);
        return priority.Concat(ordinary.Take(remaining - priority.Length)).ToArray();
    }

    public Task<CommandCardBatchResult> ExecuteAsync(
        IReadOnlyList<CommandCardCandidate> plan,
        Func<CommandCardCandidate, CancellationToken, Task> tap,
        Action<CommandCardCandidate>? onClicked,
        CancellationToken cancellationToken, int intervalMs = 120)
        => ExecuteVerifiedAsync(plan, (candidate, _) => Task.FromResult<CommandCardCandidate?>(candidate),
            async (candidate, token) => { await tap(candidate, token); return true; }, onClicked, cancellationToken, intervalMs);

    public async Task<CommandCardBatchResult> ExecuteVerifiedAsync(
        IReadOnlyList<CommandCardCandidate> plan,
        Func<CommandCardCandidate, CancellationToken, Task<CommandCardCandidate?>> refresh,
        Func<CommandCardCandidate, CancellationToken, Task<bool>> tryTap,
        Action<CommandCardCandidate>? onClicked,
        CancellationToken cancellationToken, int intervalMs = 0)
    {
        var clicked = 0;
        foreach (var planned in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsComplete)
                break;
            var candidate = await refresh(planned, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate is null)
                return new CommandCardBatchResult(clicked, null, true);
            if (_clickedSlots.Contains(candidate.Slot))
                return new CommandCardBatchResult(clicked, null, true);
            if (!await tryTap(candidate, cancellationToken))
                return new CommandCardBatchResult(clicked, null, true);
            _clickedSlots.Add(candidate.Slot);
            clicked++;
            var reachedLimit = candidate.Rule.RegisterSuccessfulTrigger();
            onClicked?.Invoke(candidate);
            if (reachedLimit)
                return new CommandCardBatchResult(clicked, candidate.Rule);
            if (!IsComplete && clicked < plan.Count && intervalMs > 0)
                await Task.Delay(intervalMs, cancellationToken);
        }
        return new CommandCardBatchResult(clicked, null);
    }
}
