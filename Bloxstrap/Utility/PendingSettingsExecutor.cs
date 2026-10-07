namespace Bloxstrap.Utility
{
    internal static class PendingSettingsExecutor
    {
        internal static bool TryExecute(out string? failedTask)
        {
            failedTask = null;
            foreach (var pair in App.PendingSettingTasks.ToArray())
            {
                if (!App.PendingSettingTasks.TryGetValue(pair.Key, out var queued) || !ReferenceEquals(queued, pair.Value))
                    continue;
                try
                {
                    if (pair.Value.Changed) pair.Value.Execute();
                    if (App.PendingSettingTasks.TryGetValue(pair.Key, out var current) && ReferenceEquals(current, pair.Value))
                        App.PendingSettingTasks.Remove(pair.Key);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("PendingSettingsExecutor", ex);
                    failedTask = pair.Key;
                    return false;
                }
            }
            failedTask = App.PendingSettingTasks.Keys.FirstOrDefault();
            return failedTask is null;
        }
    }
}
