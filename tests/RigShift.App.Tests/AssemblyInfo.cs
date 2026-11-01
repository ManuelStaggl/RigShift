// The app keeps one static Loc.Instance; every SettingsService.LoadAsync raises its PropertyChanged, and view models that
// listen to it (AutomationViewModel) rebuild on the raising thread. Two test classes running side by side would rebuild
// each other's view models, so the app tests run one collection at a time.
using Xunit.Sdk;
using Xunit.v3;

[assembly: Parallelization(Mode = ParallelMode.None)]
