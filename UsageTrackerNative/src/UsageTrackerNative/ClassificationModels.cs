namespace UsageTrackerNative;

public sealed class SubjectDefinition
{
    private const string LegacyMajorSubjectName = "默认大类";

    public string Name { get; set; } = string.Empty;
    public List<SubjectParentDefinition> Parents { get; set; } = new();
    public List<string> Children { get; set; } = new();

    public IEnumerable<string> GetAllSubjectNames()
    {
        yield return Name;
        foreach (var child in Children)
        {
            yield return child;
        }
        foreach (var parent in Parents)
        {
            foreach (var subject in parent.GetAllSubjectNames())
            {
                yield return subject;
            }
        }
    }

    public SubjectDefinition Clone() => new()
    {
        Name = Name,
        Parents = Parents.Select(x => x.Clone()).ToList(),
        Children = Children.ToList()
    };

    public SubjectDefinition Normalize()
    {
        Name = Name.Trim();
        Parents = Parents
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => x.Normalize())
            .DistinctBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Children = Children
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (Parents.Count == 0 && Children.Count > 0)
        {
            Parents.Add(new SubjectParentDefinition
            {
                Name = Name,
                Children = Children.ToList()
            }.Normalize());
            Name = LegacyMajorSubjectName;
            Children.Clear();
        }
        return this;
    }
}

public sealed class SubjectParentDefinition
{
    public string Name { get; set; } = string.Empty;
    public List<string> Children { get; set; } = new();

    public IEnumerable<string> GetAllSubjectNames()
    {
        yield return Name;
        foreach (var child in Children)
        {
            yield return child;
        }
    }

    public SubjectParentDefinition Clone() => new()
    {
        Name = Name,
        Children = Children.ToList()
    };

    public SubjectParentDefinition Normalize()
    {
        Name = Name.Trim();
        Children = Children
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return this;
    }
}
