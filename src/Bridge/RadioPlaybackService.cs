namespace Bridge;

// Exactly one server advances the common broadcast; phones never send automatic
// Next commands. Runs outside AudioEngine's pump to avoid waiting on itself.
public sealed class RadioPlaybackService(AudioEngine engine, PlaybackNavigator navigator, ILogger<RadioPlaybackService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long observed = -1, attempted = -1;
        var prepared = false;
        var retryAfter = DateTime.MinValue;
        CancellationTokenSource? preparationCancellation = null;
        Task<bool>? preparation = null;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var revision = engine.Revision;
                if (observed != revision)
                {
                    preparationCancellation?.Cancel();
                    if (preparation is not null) await preparation;
                    preparationCancellation?.Dispose(); preparationCancellation = null; preparation = null;
                    observed = revision; prepared = false; retryAfter = DateTime.MinValue;
                }
                if (!engine.RadioEnabled || engine.Current?.Provider != "youtube") continue;
                if (preparation?.IsCompleted == true)
                {
                    prepared = await preparation; preparation = null;
                    preparationCancellation?.Dispose(); preparationCancellation = null;
                    retryAfter = DateTime.UtcNow.AddSeconds(10);
                }
                if (engine.BackgroundReady && !prepared && preparation is null && DateTime.UtcNow >= retryAfter)
                {
                    preparationCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    preparation = PrepareAsync(revision, preparationCancellation.Token);
                }
                if (engine.NaturallyEndedRevision != revision || attempted == revision) continue;
                attempted = revision;
                try { await navigator.AdvanceAutomaticallyAsync(revision, stoppingToken); }
                catch (SelectionSupersededException) { attempted = -1; /* Manual controls/Stop win. */ }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception)
                {
                    log.LogWarning("Radio could not start the next track");
                    navigator.AutomaticFailed(revision); // One attempt per EOF; no retry loop.
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            preparationCancellation?.Cancel();
            if (preparation is not null) await preparation;
            preparationCancellation?.Dispose();
        }
    }

    private async Task<bool> PrepareAsync(long revision, CancellationToken token)
    {
        try { await navigator.PrepareRadioAsync(revision, token); return true; }
        catch (OperationCanceledException) { return false; }
        catch (SelectionSupersededException) { return false; }
        catch (ProviderException) { return false; }
        catch (Exception) { log.LogWarning("Radio could not prepare next-track metadata"); return false; }
    }
}
