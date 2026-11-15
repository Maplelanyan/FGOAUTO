using FgoAuto.Models;
using FgoAuto.Services;

internal static class AutomationCompatibilityChecks
{
    public static async Task RunAsync()
    {
        for (var nobleCount = 0; nobleCount <= 3; nobleCount++)
        {
            var ordinary = Rule("ordinary", card: true);
            var noble = Rule("noble", noble: true);
            var matches = Enumerable.Range(0, 5).Select(slot => (ordinary,
                new ImageMatchHit(slot * 384 + 130, 750, 80, 60, .99)))
                .Concat(Enumerable.Range(0, nobleCount).Select(slot => (noble,
                    new ImageMatchHit(300 + slot * 450, 200, 80, 60, .99)))).ToArray();
            var session = new CommandCardSession();
            var now = DateTime.UtcNow;
            Require(session.Plan(matches, 1920, 1080, now, waitForNoblePhantasms: true).Count == 0,
                "The hand-settle guard was bypassed.");
            var plan = session.Plan(matches, 1920, 1080, now.AddMilliseconds(300), new Random(6381), true);
            Require(plan.Count == 3 && plan.Select(card => card.Slot).Distinct().Count() == 3,
                "Card planning selected duplicate slots or the wrong number.");
            Require(plan.Take(nobleCount).All(card => card.Rule.IsNoblePhantasm)
                && plan.Count(card => card.Rule.IsNoblePhantasm) == nobleCount, "Noble Phantasm priority changed.");
            var clicked = new List<int>();
            var result = await session.ExecuteVerifiedAsync(plan,
                (card, _) => Task.FromResult<CommandCardCandidate?>(card),
                (card, _) => { clicked.Add(card.Slot); return Task.FromResult(true); }, null, CancellationToken.None);
            Require(result.ClickedCount == 3 && clicked.Distinct().Count() == 3 && session.IsComplete,
                "Verified card execution did not commit exactly three distinct cards.");
            Require(session.Plan(matches, 1920, 1080, now.AddSeconds(1)).Count == 0,
                "The same visible hand can be clicked again.");
        }

        var limited = Rule("limited", card: true, limit: 1);
        var cappedSession = new CommandCardSession();
        var cappedPlan = Enumerable.Range(0, 3).Select(slot => new CommandCardCandidate(slot, limited,
            new ImageMatchHit(slot * 384 + 130, 750, 80, 60, .99))).ToArray();
        var capped = await cappedSession.ExecuteVerifiedAsync(cappedPlan,
            (card, _) => Task.FromResult<CommandCardCandidate?>(card), (_, _) => Task.FromResult(true), null, CancellationToken.None);
        Require(capped.ClickedCount == 1 && ReferenceEquals(capped.LimitRule, limited), "Rule trigger limit did not stop the card batch immediately.");

        var changedSession = new CommandCardSession();
        var unlimited = Rule("unlimited", card: true);
        var changedPlan = cappedPlan.Select(card => card with { Rule = unlimited }).ToArray();
        var attempts = 0;
        var changed = await changedSession.ExecuteVerifiedAsync(changedPlan,
            (card, _) => Task.FromResult<CommandCardCandidate?>(card), (_, _) => Task.FromResult(++attempts == 1), null, CancellationToken.None);
        Require(changed.ClickedCount == 1 && changed.ScreenChanged && unlimited.TriggerCount == 1,
            "Failed verification still counted or executed remaining cards.");
        Console.WriteLine("PASS automation compatibility: 0-3 NP priority, three unique cards, no repeat hand, limits and failed verification");
    }

    private static ImageClickRule Rule(string id, bool card = false, bool noble = false, int? limit = null) =>
        new(new ImageMatchTarget(id, "unused.png", 80, 60, null!, id), .82, limit, card, false, noble);
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
