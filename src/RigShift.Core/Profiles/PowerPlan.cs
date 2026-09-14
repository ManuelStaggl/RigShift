namespace RigShift.Core.Profiles;

/// <summary>A Windows power plan. The name is only for display; the plan is found by its id.</summary>
public sealed record PowerPlan(Guid Id, string Name);
