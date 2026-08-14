using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Events;
using PiSharp.Extensions;

namespace PiSharp.Advisor;

/// <summary>
/// The advisor's background review engine. It is time-bounded, non-blocking and
/// best-effort: <see cref="OnTurnEnd"/> only queues a review and returns, the
/// watchdog bounds the completion against the configured timeout, at most one
/// review runs at a time (extra turns are coalesced), and any provider/timeout
/// failure surfaces as at most a single <c>kind:"error"/"timeout"</c> note rather
/// than throwing into the harness.
/// </summary>
public sealed class AdvisorWorker : IDisposable
{
    private const int MaxRecentNotes = 20;

    private readonly IExtensionCompletionApi _completion;
    private readonly string _sessionId;
    private readonly AdvisorTranscript _transcript;
    private readonly Action<ExtensionAdvisorEvent> _emit;

    private readonly object _gate = new();
    private readonly List<ExtensionAdvisorNote> _recentNotes = [];
    private volatile AdvisorOptions _options = AdvisorOptions.Default;
    private string? _provider;
    private string? _modelId;
    private bool _reviewing;
    private bool _pending;
    private bool _disposed;

    public AdvisorWorker(
        IExtensionCompletionApi completion,
        string sessionId,
        AdvisorTranscript transcript,
        Action<ExtensionAdvisorEvent> emit)
    {
        _completion = completion;
        _sessionId = sessionId;
        _transcript = transcript;
        _emit = emit;
    }

    /// <summary>The resolved provider (null when the model was a bare id).</summary>
    public string? Provider => _provider;

    /// <summary>The resolved advisor model id.</summary>
    public string? ModelId => _modelId;

    /// <summary>True while the advisor feature is enabled.</summary>
    public bool Enabled => _options.Enabled;

    /// <summary>Snapshot of the most recent emitted notes (for the slash command).</summary>
    public IReadOnlyList<ExtensionAdvisorNote> RecentNotes
    {
        get
        {
            lock (_gate) return _recentNotes.ToArray();
        }
    }

    /// <summary>
    /// Applies a fresh options+model. Splitting <c>provider/model</c> is done
    /// here; a bare id leaves the provider null (host infers it).
    /// </summary>
    public void Configure(AdvisorOptions options, string? model)
    {
        _options = options;
        _transcript.SetMaxTurns(options.MaxTranscriptTurns);

        if (string.IsNullOrWhiteSpace(model))
        {
            _provider = null;
            _modelId = null;
            return;
        }

        var slash = model.IndexOf('/');
        if (slash > 0)
        {
            _provider = model[..slash];
            _modelId = model[(slash + 1)..];
        }
        else
        {
            _provider = null;
            _modelId = model;
        }
    }

    /// <summary>Replaces the transcript from a <c>context</c> event.</summary>
    public void SetContext(IEnumerable<AgentMessage> messages) => _transcript.Reset(messages);

    /// <summary>Appends the just-finished turn's message to the transcript.</summary>
    public void AppendTurn(AgentMessage message) => _transcript.Append([message]);

    /// <summary>
    /// The <c>turn_end</c> entry point. Returns immediately; the review (if any)
    /// runs on a background task. When the feature is off this is a no-op.
    /// </summary>
    public void OnTurnEnd(string turnId)
    {
        if (!_options.Enabled) return;

        var model = _modelId;
        if (model is null)
        {
            Emit(new ExtensionAdvisorNote("error", "Advisor enabled but no 'model' is configured."), turnId);
            return;
        }

        RequestReview(turnId);
    }

    private void RequestReview(string turnId)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_reviewing)
            {
                // At most one queued re-review; further turns coalesce into it.
                if (_options.Coalesce) _pending = true;
                return;
            }
            _reviewing = true;
        }

        _ = RunReviewAsync(turnId);
    }

    private async Task RunReviewAsync(string turnId)
    {
        var provider = _provider;
        var model = _modelId!;
        var options = _options;

        using var timeoutCts = new CancellationTokenSource(options.TimeoutMs);

        try
        {
            var messages = _transcript.Messages.ToArray();
            var result = await _completion.CompleteAsync(
                provider ?? string.Empty,
                model,
                messages,
                systemPrompt: AdvisorPrompts.ReviewInstructions,
                options: new ExtensionCompleteRequest(
                    provider ?? string.Empty,
                    model,
                    TimeoutMs: options.TimeoutMs,
                    MaxTokens: options.MaxTokens),
                timeoutCts.Token).ConfigureAwait(false);

            switch (result.Status)
            {
                case ExtensionCompletionStatus.Ok:
                    if (string.IsNullOrWhiteSpace(result.Text))
                    {
                        Emit(new ExtensionAdvisorNote("note", "Advisor produced no output.", Model: model), turnId);
                    }
                    else
                    {
                        Emit(new ExtensionAdvisorNote(AdvisorNoteClassifier.Classify(result.Text), result.Text, Model: model), turnId);
                    }
                    break;

                case ExtensionCompletionStatus.Timeout:
                    Emit(TimeoutNote(model), turnId);
                    break;

                case ExtensionCompletionStatus.Cancelled:
                    break; // external cancellation — stay silent

                default:
                    Emit(new ExtensionAdvisorNote("error", result.Error ?? "Advisor completion failed.", Model: model), turnId);
                    break;
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !_disposed)
        {
            Emit(TimeoutNote(model), turnId);
        }
        catch (OperationCanceledException)
        {
            // External cancellation / shutdown — silent.
        }
        catch (Exception ex)
        {
            Emit(new ExtensionAdvisorNote("error", ex.Message, Model: model), turnId);
        }
        finally
        {
            bool rerun = false;
            lock (_gate)
            {
                _reviewing = false;
                if (_pending)
                {
                    _pending = false;
                    rerun = true;
                }
            }
            if (rerun && !_disposed) _ = RunReviewAsync(turnId);
        }
    }

    private static ExtensionAdvisorNote TimeoutNote(string model)
        => new("timeout", "Advisor review exceeded the time budget and was cancelled.", Model: model);

    private void Emit(ExtensionAdvisorNote note, string turnId)
    {
        lock (_gate)
        {
            _recentNotes.Add(note);
            if (_recentNotes.Count > MaxRecentNotes) _recentNotes.RemoveAt(0);
        }
        _emit(new ExtensionAdvisorEvent(_sessionId, turnId, note));
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
    }
}

/// <summary>The advisor review system prompt.</summary>
public static class AdvisorPrompts
{
    public const string ReviewInstructions =
        "You are a cautious second-opinion advisor. Review the recent conversation " +
        "and provide a short, concrete note on risks, mistakes, or blockers. " +
        "Prefix freely but keep it brief.";
}
