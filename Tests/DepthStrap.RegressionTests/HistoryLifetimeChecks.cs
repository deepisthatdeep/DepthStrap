using System.Reflection;
using System.IO;
using System.Runtime.CompilerServices;
using Bloxstrap.Enums;
using Bloxstrap.Models.Entities;
using Bloxstrap.Roblox;
using Bloxstrap.UI.Elements.ContextMenu;
using Bloxstrap.UI.ViewModels.ContextMenu;

internal static class HistoryLifetimeChecks
{
    private static EventHandler? Handlers(ActivityWatcher watcher, string name) =>
        (EventHandler?)typeof(ActivityWatcher).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(watcher);

    private static void Refresh(ActivityWatcher watcher) => Handlers(watcher, nameof(ActivityWatcher.OnHistoryUpdated))?.Invoke(watcher, EventArgs.Empty);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateDisposed(ActivityWatcher watcher)
    {
        var model = new ServerHistoryViewModel(watcher, _ => Task.CompletedTask);
        HistoryChecks.Wait(model.HistoryLoadTask);
        model.Dispose(); model.Dispose();
        return new WeakReference(model);
    }

    internal static void Run(Action<bool, string> check)
    {
        using var watcher = new ActivityWatcher();
        watcher.History.Clear();
        var retired = Enumerable.Range(0, 4).Select(_ => CreateDisposed(watcher)).ToArray();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        check(retired.All(reference => !reference.IsAlive), "Disposed history models are collectible while their watcher remains alive");
        check(Handlers(watcher, nameof(ActivityWatcher.OnHistoryUpdated)) is null && Handlers(watcher, nameof(ActivityWatcher.OnGameLeave)) is null,
            "Disposal removes both watcher subscriptions and is safe to repeat");

        var window = new ServerHistory(watcher);
        var windowModel = (ServerHistoryViewModel)window.DataContext;
        HistoryChecks.Wait(windowModel.HistoryLoadTask);
        window.Close();
        check(Handlers(watcher, nameof(ActivityWatcher.OnHistoryUpdated)) is null,
            "Closing the actual history window disposes its view model");

        var pending = new Queue<TaskCompletionSource>();
        var started = new Queue<TaskCompletionSource>();
        var first = new TaskCompletionSource(); var firstStarted = new TaskCompletionSource();
        var second = new TaskCompletionSource(); var secondStarted = new TaskCompletionSource();
        pending.Enqueue(first); pending.Enqueue(second); started.Enqueue(firstStarted); started.Enqueue(secondStarted);
        int fetches = 0;
        watcher.History.Add(new ActivityData { UniverseId = 1, PlaceId = 1, JobId = "old", TimeJoined = DateTime.Now });
        using var live = new ServerHistoryViewModel(watcher, _ =>
        {
            fetches++;
            started.Dequeue().SetResult();
            return pending.Dequeue().Task;
        });
        HistoryChecks.Wait(firstStarted.Task);
        Task oldLoad = live.HistoryLoadTask;
        watcher.History = [new ActivityData { UniverseId = 2, PlaceId = 2, JobId = "new", TimeJoined = DateTime.Now }];
        Refresh(watcher);
        HistoryChecks.Wait(secondStarted.Task);
        second.SetResult();
        HistoryChecks.Wait(live.HistoryLoadTask);
        check(live.GameHistory?.Single().JobId == "new" && live.LoadState == GenericTriState.Successful,
            "The newest history request publishes its result");
        first.SetException(new IOException("Obsolete history request failed"));
        HistoryChecks.Wait(oldLoad);
        check(live.GameHistory?.Single().JobId == "new" && live.LoadState == GenericTriState.Successful && live.Error.Length == 0,
            "An older failed request cannot replace newer history or its successful state");

        var row = watcher.History.Single();
        row.UniverseDetails = new UniverseDetails();
        Refresh(watcher); HistoryChecks.Wait(live.HistoryLoadTask);
        Refresh(watcher); HistoryChecks.Wait(live.HistoryLoadTask);
        var deletes = (Delegate?)typeof(ActivityData).GetField(nameof(ActivityData.OnDeleteRequested), BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(row);
        check(deletes?.GetInvocationList().Length == 1, "Repeated history reloads keep only one delete subscription per displayed row");

        var closing = new TaskCompletionSource(); var closingStarted = new TaskCompletionSource();
        pending.Enqueue(closing); started.Enqueue(closingStarted);
        row.UniverseDetails = null;
        Refresh(watcher); HistoryChecks.Wait(closingStarted.Task);
        Task closingLoad = live.HistoryLoadTask;
        int notifications = 0;
        live.PropertyChanged += (_, _) => notifications++;
        live.Dispose();
        closing.SetResult(); HistoryChecks.Wait(closingLoad);
        Refresh(watcher);
        row.DeleteHistoryCommand.Execute(null);
        check(notifications == 0 && fetches == 3 && watcher.History.Contains(row),
            "Disposed history ignores in-flight completion, future watcher events and old row delete commands");
        check(Handlers(watcher, nameof(ActivityWatcher.OnHistoryUpdated)) is null && Handlers(watcher, nameof(ActivityWatcher.OnGameLeave)) is null,
            "Repeated open and close cycles leave no watcher handlers");
    }
}
