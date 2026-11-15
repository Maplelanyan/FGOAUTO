using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FgoAuto.Models;

public sealed class RuleSet : ObservableObject
{
    private string _name;

    public string Id { get; }
    public string Name => _name;
    public ObservableCollection<ImageClickRule> Rules { get; }
    public string Display => $"{Name}（{Rules.Count} 条）";

    public RuleSet(string id, string name, IEnumerable<ImageClickRule>? rules = null)
    {
        Id = id;
        _name = name;
        Rules = new ObservableCollection<ImageClickRule>(rules ?? []);
        Rules.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Display));
    }

    public void Rename(string name)
    {
        if (SetProperty(ref _name, name, nameof(Name)))
            OnPropertyChanged(nameof(Display));
    }

    public RuleSet Duplicate(string name) => new(Guid.NewGuid().ToString("N"), name,
        Rules.Select(rule => new ImageClickRule(rule.Target, rule.Threshold, rule.MaxTriggerCount, rule.IsCommandCard, rule.PreferTopmostMatch, rule.IsNoblePhantasm)));
}
