using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Loops;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Prompting;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Core.Tools;
using PiSharp.Agent.Compaction;
using PiSharp.Agent.Loops;
using PiSharp.Agent.Resources;
using PiSharp.Agent.Resources.Prompting;
using PiSharp.Agent.Harness.LoopEvents;
using PiSharp.Extensions;
using PiSharp.Agent.Serialization;
using System.Runtime.CompilerServices;

namespace PiSharp.Agent.Harness;

public sealed class AgentHarness<TMetadata> where TMetadata : ISessionMetadata
{
    private readonly ISession<TMetadata> _session;
    private readonly AgentHarnessOptions<TMetadata> _options;
    private readonly List<AgentMessage> _steerQueue = [];
    private readonly List<AgentMessage> _followUpQueue = [];
    private readonly List<AgentMessage> _nextTurnQueue = [];
    private readonly List<PendingWrite> _pendingWrites = [];
    private readonly List<Func<AgentHarnessEvent, CancellationToken, Task>> _listeners = [];
    private ModelDescriptor _model;
    private ThinkingLevel _thinkingLevel;
    private AgentHarnessPhase _phase = AgentHarnessPhase.Idle;
    private CancellationTokenSource? _runAbort;
    private TaskCompletionSource? _runCompletion;
    private readonly Dictionary<string, IAgentTool> _tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _toolSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Skill> _baseSkills = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<SkillRegistration>> _extensionSkills = new(StringComparer.Ordinal);
    private string[]? _activeToolNames;
    private string[]? _selectedSkillNames;
    private readonly ExtensionRegistry? _extensions;
    private readonly LoopEventPipeline _loopEventPipeline;
    private readonly ILogger _logger;

    public AgentHarness(AgentHarnessOptions<TMetadata> options, ILoggerFactory? loggerFactory = null)
    {
        _options = options;
        _session = options.Session;
        _model = options.Model;
        _thinkingLevel = options.ThinkingLevel;
        _extensions = options.Extensions;
        _logger = loggerFactory?.CreateLogger<AgentHarness<TMetadata>>() ?? NullLogger<AgentHarness<TMetadata>>.Instance;
        foreach (var tool in options.Tools) _tools[tool.Name] = tool;
        foreach (var skill in options.Skills ?? []) _baseSkills[skill.Name] = skill;
        // All harness event kinds flow through the same ordered policy path.
        _loopEventPipeline = new LoopEventPipeline([
            new PersistenceStage(),
            new PhaseTransitionStage(),
            new ToolMiddlewareStage(),
            new ExtensionDispatchStage(),
            new ListenerNotificationStage()
        ]);
    }

    public ISession<TMetadata> Session => _session;
    public AgentHarnessPhase Phase => _phase;
    public ModelDescriptor Model => _model;
    public SystemPromptDocument? LastPromptDocument { get; private set; }
    public ThinkingLevel ThinkingLevel => _thinkingLevel;
    public IReadOnlyList<string> AllToolNames => _tools.Keys.ToArray();
    public IReadOnlyList<string> ActiveToolNames => _activeToolNames ?? _options.ActiveToolNames ?? _tools.Keys.ToArray();
    public IReadOnlyList<Skill> Skills => CurrentSkills().ToArray();
    public IReadOnlyList<string> AllSkillNames => CurrentSkills().Select(skill => skill.Name).ToArray();
    public IReadOnlyList<string> SelectedSkillNames => _selectedSkillNames ?? AllSkillNames;
    public IReadOnlyList<string>? ExplicitSelectedSkillNames => _selectedSkillNames;
    public bool HasExtensionRegistry => _extensions is not null;

    public IDisposable Subscribe(Func<AgentHarnessEvent, CancellationToken, Task> listener)
    {
        _listeners.Add(listener);
        return new HarnessSubscription(() => _listeners.Remove(listener));
    }

    public void Steer(AgentMessage message)
    {
        EnsureInjectableMessage(message);
        _steerQueue.Add(message);
        EmitQueueUpdate();
    }

    public void FollowUp(AgentMessage message)
    {
        EnsureInjectableMessage(message);
        _followUpQueue.Add(message);
        EmitQueueUpdate();
    }

    public void QueueNextTurn(AgentMessage message)
    {
        EnsureInjectableMessage(message);
        _nextTurnQueue.Add(message);
        EmitQueueUpdate();
    }

    public Task DispatchSessionStartAsync(string reason, CancellationToken cancellationToken = default)
        => PublishOwnEventAsync(new AgentHarnessOwnEvent.SessionStart(reason), cancellationToken);

    public void Abort() => _runAbort?.Cancel();
    public void SetActiveTools(IReadOnlyList<string> toolNames)
        => _activeToolNames = toolNames.Count == 0 ? null : toolNames.ToArray();
    public void SetSelectedSkills(IReadOnlyList<string>? skillNames)
        => _selectedSkillNames = skillNames is null ? null : skillNames.ToArray();
    public Task WaitForIdleAsync() => _runCompletion?.Task ?? Task.CompletedTask;

    public IDisposable RegisterTool(string sourceId, IAgentTool tool)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("Source id is required.", nameof(sourceId));
        if (string.IsNullOrWhiteSpace(tool.Name)) throw new ArgumentException("Tool name is required.", nameof(tool));
        _tools[tool.Name] = tool;
        _toolSources[tool.Name] = sourceId;
        if (_activeToolNames is not null && !_activeToolNames.Contains(tool.Name, StringComparer.Ordinal))
            _activeToolNames = _activeToolNames.Concat([tool.Name]).ToArray();
        return new HarnessSubscription(() => UnregisterTool(sourceId, tool.Name));
    }

    public bool UnregisterTool(string sourceId, string name)
    {
        if (!_toolSources.TryGetValue(name, out var owner) || !StringComparer.Ordinal.Equals(owner, sourceId)) return false;
        _toolSources.Remove(name);
        return _tools.Remove(name);
    }

    public int UnregisterToolsBySource(string sourceId)
    {
        var names = _toolSources.Where(pair => StringComparer.Ordinal.Equals(pair.Value, sourceId)).Select(pair => pair.Key).ToArray();
        foreach (var name in names) UnregisterTool(sourceId, name);
        return names.Length;
    }

    public IDisposable RegisterSkill(string sourceId, ExtensionSkillDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("Source id is required.", nameof(sourceId));
        if (string.IsNullOrWhiteSpace(definition.Name)) throw new ArgumentException("Skill name is required.", nameof(definition));
        var skill = new Skill(
            definition.Name,
            definition.Description,
            definition.Content,
            definition.FilePath,
            definition.DisableModelInvocation,
            definition.Globs,
            definition.AlwaysApply,
            definition.Hide,
            definition.Source,
            definition.SourcePriority,
            definition.Runner);
        if (!_extensionSkills.TryGetValue(skill.Name, out var stack)) _extensionSkills[skill.Name] = stack = [];
        stack.RemoveAll(item => StringComparer.Ordinal.Equals(item.SourceId, sourceId));
        var entry = new SkillRegistration(sourceId, skill);
        stack.Add(entry);
        return new HarnessSubscription(() => UnregisterSkill(sourceId, skill.Name));
    }

    public bool UnregisterSkill(string sourceId, string name)
    {
        if (!_extensionSkills.TryGetValue(name, out var stack)) return false;
        var before = stack.Count;
        stack.RemoveAll(item => StringComparer.Ordinal.Equals(item.SourceId, sourceId));
        if (stack.Count == 0) _extensionSkills.Remove(name);
        if (before == stack.Count) return false;
        if (_selectedSkillNames is not null && ResolveSkill(name) is null)
            _selectedSkillNames = _selectedSkillNames.Where(skill => !StringComparer.Ordinal.Equals(skill, name)).ToArray();
        return true;
    }

    public int UnregisterSkillsBySource(string sourceId)
    {
        var names = _extensionSkills.Values.SelectMany(stack => stack)
            .Where(item => StringComparer.Ordinal.Equals(item.SourceId, sourceId))
            .Select(item => item.Skill.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var name in names) UnregisterSkill(sourceId, name);
        return names.Length;
    }

    public async Task SetModelAsync(ModelDescriptor model, string source = "runtime", CancellationToken cancellationToken = default)
    {
        if (model == _model) return;
        var previous = _model;
        _model = model;
        if (_phase == AgentHarnessPhase.Idle) await _session.AppendModelChangeAsync(model.Provider, model.Id, cancellationToken);
        else _pendingWrites.Add(new PendingWrite.ModelChange(model.Provider, model.Id));
        await PublishOwnEventAsync(new AgentHarnessOwnEvent.ModelSelect(model, previous, source), cancellationToken);
    }

    public async Task SetThinkingLevelAsync(ThinkingLevel level, CancellationToken cancellationToken = default)
    {
        if (level == _thinkingLevel)
        {
            _logger.LogDebug(
                "Harness thinking level change skipped harnessId={HarnessId} currentLevel={CurrentLevel} requestedLevel={RequestedLevel}",
                RuntimeHelpers.GetHashCode(this),
                _thinkingLevel,
                level);
            return;
        }
        var previous = _thinkingLevel;
        _logger.LogDebug(
            "Harness thinking level change requested harnessId={HarnessId} previousLevel={PreviousLevel} nextLevel={NextLevel}",
            RuntimeHelpers.GetHashCode(this),
            previous,
            level);
        _thinkingLevel = level;
        _logger.LogDebug(
            "Harness thinking level state updated harnessId={HarnessId} previousLevel={PreviousLevel} currentLevel={CurrentLevel} phase={Phase}",
            RuntimeHelpers.GetHashCode(this),
            previous,
            _thinkingLevel,
            _phase);
        var serialized = level.ToString().ToLowerInvariant();
        await PublishOwnEventAsync(new AgentHarnessOwnEvent.ThinkingLevelSelect(level, previous), cancellationToken);
        await PublishOwnEventAsync(new AgentHarnessOwnEvent.ThinkingLevelChanged(level), cancellationToken);
        if (_phase == AgentHarnessPhase.Idle) await _session.AppendThinkingLevelChangeAsync(serialized, cancellationToken);
        else _pendingWrites.Add(new PendingWrite.ThinkingChange(serialized));
    }

    public async Task SetSessionNameAsync(string name, CancellationToken cancellationToken = default)
    {
        await _session.AppendSessionNameAsync(name, cancellationToken);
        await PublishOwnEventAsync(new AgentHarnessOwnEvent.SessionInfoChanged(name), cancellationToken);
    }

    public Task<AssistantMessage> PromptAsync(string text, CancellationToken cancellationToken)
        => PromptAsync(text, images: null, cancellationToken);

    public async Task<AssistantMessage> PromptAsync(string text, IReadOnlyList<ImageContent>? images = null, CancellationToken cancellationToken = default)
    {
        if (_phase != AgentHarnessPhase.Idle) throw new InvalidOperationException("Harness is busy");
        _phase = AgentHarnessPhase.Turn;
        _runCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runAbort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var turnState = await CreateTurnStateAsync(cancellationToken);
            var promptText = await ExpandSkillCommandAsync(text, cancellationToken);
            var systemPrompt = await BuildSystemPromptAsync(promptText, images, turnState, cancellationToken);
            var beforeStart = await DispatchBeforeAgentStartAsync(promptText, images, systemPrompt, turnState.ActiveTools, cancellationToken);
            systemPrompt = beforeStart.ModifiedSystemPrompt ?? systemPrompt;
            return await ExecuteTurnAsync(turnState, promptText, images, cancellationToken, systemPrompt, beforeStart.ModifiedMessages);
        }
        finally { _phase = AgentHarnessPhase.Idle; _runCompletion.TrySetResult(); _runAbort?.Dispose(); _runAbort = null; }
    }

    public async Task CompactAsync(string? customInstructions = null, CancellationToken cancellationToken = default)
    {
        if (_phase != AgentHarnessPhase.Idle) throw new InvalidOperationException("compact() requires idle harness");
        _phase = AgentHarnessPhase.Compaction;
        try
        {
            var branch = await _session.GetBranchAsync(cancellationToken: cancellationToken);
            var prep = CompactionService.Prepare(branch, CompactionService.Default);
            if (prep is null) throw new InvalidOperationException("Nothing to compact");
            await PublishOwnEventAsync(new AgentHarnessOwnEvent.CompactionStart("manual"), cancellationToken);
            await PublishOwnEventAsync(new AgentHarnessOwnEvent.SessionBeforeCompact(prep, branch.Cast<object>().ToArray(), customInstructions, cancellationToken), cancellationToken);
            var completion = string.IsNullOrWhiteSpace(customInstructions) ? _options.CompletionAsync : CompletionWithCustomInstructions(customInstructions);
            var result = await CompactionService.CompactAsync(prep, completion, cancellationToken);
            var compactionId = await _session.AppendCompactionAsync(result.Summary, result.FirstKeptEntryId, result.TokensBefore, null, false, cancellationToken);
            var compaction = await _session.GetEntryAsync(compactionId, cancellationToken);
            await PublishOwnEventAsync(new AgentHarnessOwnEvent.SessionCompact(compaction!, false), cancellationToken);
            await PublishOwnEventAsync(new AgentHarnessOwnEvent.CompactionEnd("manual", compaction, Aborted: false, WillRetry: false, ErrorMessage: null), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await PublishOwnEventAsync(new AgentHarnessOwnEvent.CompactionEnd("manual", Result: null, Aborted: true, WillRetry: false, ErrorMessage: "Compaction cancelled"), CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Compaction failed");
            await PublishOwnEventAsync(new AgentHarnessOwnEvent.CompactionEnd("manual", Result: null, Aborted: false, WillRetry: false, ErrorMessage: ex.Message), CancellationToken.None);
            throw;
        }
        finally { _phase = AgentHarnessPhase.Idle; }
    }

    public async Task NavigateTreeAsync(string targetId, bool summarize = false, CancellationToken cancellationToken = default)
    {
        if (_phase != AgentHarnessPhase.Idle) throw new InvalidOperationException("navigateTree() requires idle harness");
        _phase = AgentHarnessPhase.BranchSummary;
        try
        {
            var oldLeaf = await _session.GetLeafIdAsync(cancellationToken);
            if (oldLeaf == targetId) return;
            await PublishOwnEventAsync(new AgentHarnessOwnEvent.SessionBeforeTree(new { TargetId = targetId, Summarize = summarize, OldLeafId = oldLeaf }, cancellationToken), cancellationToken);
            string? newLeaf = null;
            BranchSummaryEntry? summaryEntry = null;
            var entries = await BranchSummarizationService.CollectEntriesAsync(_session, oldLeaf, targetId, cancellationToken);
            if (summarize && entries.Count > 0)
            {
                var summary = await BranchSummarizationService.GenerateSummaryAsync(entries, _options.CompletionAsync, cancellationToken);
                var target = await _session.GetEntryAsync(targetId, cancellationToken);
                newLeaf = target is MessageEntry { Message: UserMessage } ? target.ParentId : targetId;
                var summaryId = await _session.MoveToAsync(newLeaf, new BranchSummaryEntry { Id = string.Empty, ParentId = newLeaf, Timestamp = DateTimeOffset.UtcNow, FromId = oldLeaf ?? "root", Summary = summary.Summary, FromHook = false }, cancellationToken);
                summaryEntry = summaryId is null ? null : await _session.GetEntryAsync(summaryId, cancellationToken) as BranchSummaryEntry;
            }
            else
            {
                var target = await _session.GetEntryAsync(targetId, cancellationToken);
                if (target is MessageEntry { Message: UserMessage }) newLeaf = target.ParentId;
                else newLeaf = targetId;
                await _session.MoveToAsync(newLeaf, cancellationToken: cancellationToken);
            }
            await PublishOwnEventAsync(new AgentHarnessOwnEvent.SessionTree(newLeaf, oldLeaf, summaryEntry, false), cancellationToken);
        }
        finally { _phase = AgentHarnessPhase.Idle; }
    }
    private async Task<string> ExpandSkillCommandAsync(string text, CancellationToken cancellationToken)
    {
        const string prefix = "/skill:";
        if (!text.StartsWith(prefix, StringComparison.Ordinal)) return text;
        var remainder = text[prefix.Length..];
        var separator = remainder.IndexOf(' ');
        var name = separator < 0 ? remainder : remainder[..separator];
        var args = separator < 0 ? string.Empty : remainder[(separator + 1)..];
        if (string.IsNullOrWhiteSpace(name)) return text;
        var skill = ResolveSkill(name);
        if (skill is null) return text;
        var additionalInstructions = string.IsNullOrWhiteSpace(args) ? null : args;
        var argList = ArgList(args);
        if (skill.Runner is null) return SkillManager.FormatInvocation(skill, additionalInstructions);

        await PublishOwnEventAsync(new AgentHarnessOwnEvent.SkillExecutionStart(skill.Name, additionalInstructions, argList), cancellationToken);
        try
        {
            var result = await skill.Runner(new ExtensionSkillRunContext(skill.Name, skill.Content, additionalInstructions, argList), cancellationToken);
            await PublishOwnEventAsync(new AgentHarnessOwnEvent.SkillExecutionEnd(skill.Name, additionalInstructions, argList, result.Details, false), cancellationToken);
            return string.IsNullOrWhiteSpace(result.Content) ? SkillManager.FormatInvocation(skill, additionalInstructions) : result.Content;
        }
        catch (Exception ex)
        {
            await PublishOwnEventAsync(new AgentHarnessOwnEvent.SkillExecutionEnd(skill.Name, additionalInstructions, argList, null, true, ex.Message), CancellationToken.None);
            _logger.LogWarning(ex, "Skill runner '{SkillName}' failed; falling back to markdown injection", skill.Name);
            return SkillManager.FormatInvocation(skill, additionalInstructions);
        }
    }

    private static IReadOnlyList<string> ArgList(string args)
        => string.IsNullOrWhiteSpace(args) ? [] : args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private async Task<AgentHarnessTurnState> CreateTurnStateAsync(CancellationToken cancellationToken)
    {
        var context = await _session.BuildContextAsync(cancellationToken);
        var selected = _activeToolNames ?? _options.ActiveToolNames;
        var activeTools = selected is null
            ? _tools.Values.ToArray()
            : selected.Select(name => _tools.GetValueOrDefault(name)).OfType<IAgentTool>().ToArray();
        return new AgentHarnessTurnState(context.Messages, activeTools, _model, _thinkingLevel, _session.Metadata);
    }

    private AgentCompletionAsync CompletionWithCustomInstructions(string customInstructions)
        => async (model, context, options, cancellationToken) =>
        {
            var messages = context.Messages.Concat([AgentMessages.User($"Additional compaction instructions:\n{customInstructions}")]).ToArray();
            return await _options.CompletionAsync(model, new AgentContext(context.SystemPrompt, messages, context.Tools), options, cancellationToken);
        };

    private async Task<AssistantMessage> ExecuteTurnAsync(
        AgentHarnessTurnState turnState,
        string text,
        IReadOnlyList<ImageContent>? images,
        CancellationToken cancellationToken,
        string systemPrompt,
        IReadOnlyList<AgentMessage>? injectedMessages)
    {
        var content = new List<MessageContent> { new TextContent(text) };
        if (images is not null) content.AddRange(images);
        var messages = new List<AgentMessage>();
        if (injectedMessages is not null)
        {
            foreach (var message in injectedMessages)
            {
                EnsureInjectableMessage(message);
                messages.Add(message);
            }
        }
        messages.Add(AgentMessages.User(content));
        if (_nextTurnQueue.Count > 0)
        {
            messages.InsertRange(0, _nextTurnQueue);
            _nextTurnQueue.Clear();
            EmitQueueUpdate();
        }
        var interceptors = _extensions?.StreamDeltaInterceptors ?? [];
        var config = new AgentLoopConfig(_model, _options.StreamAsync)
        {
            GetSteeringMessages = DrainSteeringQueueAsync,
            GetFollowUpMessages = DrainFollowUpQueueAsync,
            BeforeToolCall = _extensions is null ? null : (ctx, token) => RunBeforeToolMiddlewareAsync(ctx, token),
            AfterToolCall = _extensions is null ? null : (ctx, token) => RunAfterToolMiddlewareAsync(ctx, token),
            ToolExecution = _options.ToolExecution,
            StreamOptions = _options.StreamOptions,
            ThinkingLevel = _thinkingLevel,
            OnStreamDelta = interceptors.Count == 0 ? null : (delta, token) => RunStreamDeltaInterceptorsAsync(delta, token),
            PrepareStreamMessages = interceptors.Count == 0 ? null : (messages, ctx, token) => RunPrepareStreamMessagesAsync(messages, ctx, token)
        };
        var results = await AgentLoop.RunAgentLoopAsync(messages, new AgentContext(systemPrompt, turnState.Messages, turnState.ActiveTools), config, HandleLoopEventAsync, _runAbort?.Token ?? cancellationToken);
        return results.OfType<AssistantMessage>().LastOrDefault() ?? throw new InvalidOperationException("No assistant message in results");
    }

    private async Task<StreamDeltaDecision?> RunStreamDeltaInterceptorsAsync(StreamDeltaContext delta, CancellationToken cancellationToken)
    {
        if (_extensions is null) return null;
        foreach (var registration in _extensions.StreamDeltaInterceptors)
        {
            var decision = await registration.Value.InterceptDeltaAsync(delta, cancellationToken);
            if (decision is not null) return decision;
        }
        return null;
    }

    private async Task<IReadOnlyList<AgentMessage>> RunPrepareStreamMessagesAsync(
        IReadOnlyList<AgentMessage> messages,
        AgentContext context,
        CancellationToken cancellationToken)
    {
        if (_extensions is null) return messages;
        var current = messages;
        foreach (var registration in _extensions.StreamDeltaInterceptors)
        {
            current = await registration.Value.PrepareMessagesAsync(current, context, cancellationToken);
        }
        return current;
    }

    private async Task<string> BuildSystemPromptAsync(
        string prompt,
        IReadOnlyList<ImageContent>? images,
        AgentHarnessTurnState turnState,
        CancellationToken cancellationToken)
    {
        var baseContext = _options.SystemPromptContext
            ?? (_options.SystemPrompt is not null ? SystemPromptBuildOptionsMapper.ToContext(_options.SystemPrompt) : CreateDefaultPromptContext());
        var activeTools = turnState.ActiveTools
            .Select(tool => new PiSharp.Agent.Core.Prompting.ToolPromptInfo(tool.Name, ToolPromptSnippet(tool), tool.PromptGuidelines))
            .ToArray();
        var activeToolNames = turnState.ActiveTools.Select(tool => tool.Name).ToArray();
        var context = baseContext with
        {
            Tools = activeTools,
            SelectedToolNames = activeToolNames,
            Skills = CurrentSkills().Select(skill => new PromptSkillInfo(skill.Name, skill.Description, skill.FilePath, skill.DisableModelInvocation)).ToArray(),
            SelectedSkillNames = _selectedSkillNames
        };
        var composer = ResolveSystemPromptComposer();
        var document = composer.Compose(context);
        document = await DispatchBeforePromptRenderAsync(prompt, images, context, document, turnState.ActiveTools, cancellationToken);
        LastPromptDocument = document;
        return composer.Render(document);
    }

    private ISystemPromptComposer ResolveSystemPromptComposer()
        => _options.SystemPromptComposerFactory?.Invoke()
            ?? _options.SystemPromptComposer
            ?? SystemPromptComposer.CreateDefault();

    private SystemPromptCompositionContext CreateDefaultPromptContext()
        => new(
            Cwd: Directory.GetCurrentDirectory(),
            CurrentDate: DateOnly.FromDateTime(DateTime.Now),
            Mode: PromptMode.Default,
            Tools: _tools.Values.Select(tool => new PiSharp.Agent.Core.Prompting.ToolPromptInfo(tool.Name, ToolPromptSnippet(tool), tool.PromptGuidelines)).ToArray(),
            SelectedToolNames: _options.ActiveToolNames ?? _tools.Keys.ToArray(),
            ExplicitGuidelines: [],
            CustomPrompt: null,
            AppendPrompt: null,
            ContextFiles: [],
            Skills: CurrentSkills().Select(skill => new PromptSkillInfo(skill.Name, skill.Description, skill.FilePath, skill.DisableModelInvocation)).ToArray(),
            DocumentationPaths: new PromptDocumentationPaths("README.md", "docs", "examples"),
            SelectedSkillNames: _selectedSkillNames);

    private static string? ToolPromptSnippet(IAgentTool tool)
        => string.IsNullOrWhiteSpace(tool.PromptSnippet) ? tool.Description : tool.PromptSnippet;

    private async Task<BeforeToolCallResult?> RunBeforeToolMiddlewareAsync(BeforeToolCallContext context, CancellationToken cancellationToken)
    {
        var input = ToolInput(context.Args);
        var harnessEvent = new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.ToolCall(context.ToolCall.Id, context.ToolCall.Name, input));
        var pipelineContext = CreateEventContext(harnessEvent, HarnessEventKind.BeforeToolMiddleware, beforeToolCall: context);
        await _loopEventPipeline.ExecuteAsync(pipelineContext, cancellationToken);
        return pipelineContext.BeforeToolCallResult;
    }

    private async Task<AfterToolCallResult?> RunAfterToolMiddlewareAsync(AfterToolCallContext context, CancellationToken cancellationToken)
    {
        var input = ToolInput(context.Args);
        var harnessEvent = new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.ToolResult(context.ToolCall.Id, context.ToolCall.Name, input, context.Result.Content, context.Result.Details ?? new { }, context.IsError));
        var pipelineContext = CreateEventContext(harnessEvent, HarnessEventKind.AfterToolMiddleware, afterToolCall: context);
        await _loopEventPipeline.ExecuteAsync(pipelineContext, cancellationToken);
        return pipelineContext.AfterToolCallResult;
    }

    private Task HandleLoopEventAsync(AgentEvent e, CancellationToken cancellationToken)
        => _loopEventPipeline.ExecuteAsync(
            CreateEventContext(new AgentHarnessEvent.Core(e), HarnessEventKind.CoreLoop),
            cancellationToken);
    /// <summary>
    /// Publishes a harness-owned event through the loop-event pipeline
    /// (extension dispatch + listener notification), making it visible to
    /// in-process extension handlers and harness subscribers (the daemon wire
    /// and TS bridge). Custom events are validated before dispatch.
    /// </summary>
    public async Task PublishOwnEventAsync(AgentHarnessOwnEvent ownEvent, CancellationToken cancellationToken)
    {
        if (ownEvent is AgentHarnessOwnEvent.CustomEvent customEvent)
            ValidateCustomEvent(customEvent);

        var context = CreateEventContext(new AgentHarnessEvent.Own(ownEvent), HarnessEventKind.Own);
        if (context.IsThinkingLevelOwnEvent)
        {
            _logger.LogDebug(
                "Publishing own harness event harnessId={HarnessId} event={EventName} listenerCount={ListenerCount} extensionHandlerCount={ExtensionHandlerCount}",
                context.HarnessId,
                context.EventName,
                context.ListenerCount,
                context.ExtensionHandlerCount);
        }

        await _loopEventPipeline.ExecuteAsync(context, cancellationToken);

        if (context.IsThinkingLevelOwnEvent)
        {
            _logger.LogDebug(
                "Published own harness event harnessId={HarnessId} event={EventName}",
                context.HarnessId,
                context.EventName);
        }
    }

    private static void ValidateCustomEvent(AgentHarnessOwnEvent.CustomEvent customEvent)
    {
        AgentSessionEvent.ValidateCustomEventName(customEvent.Name);
        try
        {
            AgentJsonSerializer.Serialize(customEvent.Payload);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new InvalidOperationException($"Custom event '{customEvent.Name}' payload is not JSON-serializable: {ex.Message}", ex);
        }
    }

    private async Task QueueWriteOrAppendAsync(AgentMessage message)
    {
        if (_phase == AgentHarnessPhase.Idle) await _session.AppendMessageAsync(message);
        else _pendingWrites.Add(new PendingWrite.MessageWrite(message));
    }

    private HarnessEventContext CreateEventContext(
        AgentHarnessEvent @event,
        HarnessEventKind kind,
        ExtensionEvent? extensionEvent = null,
        BeforeToolCallContext? beforeToolCall = null,
        AfterToolCallContext? afterToolCall = null)
    {
        var mappedExtensionEvent = extensionEvent ?? ExtensionEventMapper.Map(@event);
        var extensionHandlerCount = _extensions?.Handlers.Count(handler => StringComparer.Ordinal.Equals(handler.Value.EventName, mappedExtensionEvent.Name)) ?? 0;
        return new(
            @event,
            kind,
            QueueWriteOrAppendAsync,
            FlushWritesAsync,
            phase => _phase = phase,
            _extensions is null ? null : DispatchExtensionEventAsync,
            _extensions?.Middleware ?? [],
            _listeners.ToArray(),
            _logger,
            RuntimeHelpers.GetHashCode(this),
            extensionHandlerCount,
            mappedExtensionEvent,
            beforeToolCall,
            afterToolCall);
    }

    private async Task DispatchExtensionEventAsync(ExtensionEvent @event, CancellationToken cancellationToken)
    {
        var handlers = _extensions!.HandlersFor(@event.Name);
        if (RequiresOrderedExtensionDispatch(@event.Name))
        {
            foreach (var handler in handlers)
            {
                await handler.Value.Handler(@event, cancellationToken);
            }

            return;
        }

        await Task.WhenAll(handlers.Select(handler => InvokeExtensionHandlerAsync(handler.Value.Handler, @event, cancellationToken)));
    }

    private static async Task InvokeExtensionHandlerAsync(ExtensionEventHandler handler, ExtensionEvent @event, CancellationToken cancellationToken)
        => await handler(@event, cancellationToken);

    private static bool RequiresOrderedExtensionDispatch(string eventName)
        => eventName is ExtensionEventNames.BeforeAgentStart
            or ExtensionEventNames.BeforePromptRender
            or ExtensionEventNames.Input
            or ExtensionEventNames.SessionBeforeSwitch
            or ExtensionEventNames.SessionBeforeFork
            or ExtensionEventNames.ResourcesDiscover
            or ExtensionEventNames.UserBash;

    private async Task FlushWritesAsync(CancellationToken cancellationToken)
    {
        if (_pendingWrites.Count == 0) return;

        var writes = _pendingWrites.ToArray();
        await _session.AppendEntriesAsync(writes.Select(ToSessionEntry).ToArray(), cancellationToken);
        _pendingWrites.RemoveRange(0, writes.Length);
    }

    private static SessionTreeEntry ToSessionEntry(PendingWrite write)
        => write switch
        {
            PendingWrite.MessageWrite message => new MessageEntry { Message = message.Message, Id = string.Empty, ParentId = null, Timestamp = default },
            PendingWrite.ModelChange model => new ModelChangeEntry { Provider = model.Provider, ModelId = model.ModelId, Id = string.Empty, ParentId = null, Timestamp = default },
            PendingWrite.ThinkingChange thinking => new ThinkingLevelChangeEntry { ThinkingLevel = thinking.Level, Id = string.Empty, ParentId = null, Timestamp = default },
            _ => throw new InvalidOperationException($"Unsupported pending write type {write.GetType().Name}.")
        };

    private async Task<SystemPromptDocument> DispatchBeforePromptRenderAsync(
        string prompt,
        IReadOnlyList<ImageContent>? images,
        SystemPromptCompositionContext context,
        SystemPromptDocument document,
        IReadOnlyList<IAgentTool> resources,
        CancellationToken cancellationToken)
    {
        if (_extensions is null) return document;

        var applier = new PromptDocumentPatchApplier();
        var payload = new PromptDocumentHookPayload(prompt, applier.ToSectionDtos(document), document.Diagnostics, resources);
        var harnessEvent = new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.BeforePromptRender(prompt, images, context, document, resources));
        var extensionEvent = new ExtensionEvent(ExtensionEventNames.BeforePromptRender, harnessEvent, payload);
        var pipelineContext = CreateEventContext(harnessEvent, HarnessEventKind.Own, extensionEvent: extensionEvent);
        await _loopEventPipeline.ExecuteAsync(pipelineContext, cancellationToken);

        try
        {
            var modified = pipelineContext.ExtensionEvent.ModifiedPromptDocument ?? document;
            return pipelineContext.ExtensionEvent.ModifiedPromptDocumentPatch is null
                ? modified
                : applier.Apply(
                    modified,
                    pipelineContext.ExtensionEvent.ModifiedPromptDocumentPatch,
                    new PromptContributionSource("extension:prompt-document-hook", PromptContributionSourceKind.Extension));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Prompt document hook patch failed");
            var diagnostic = new PromptDiagnostic("prompt_document_hook_failed", $"Prompt document hook patch failed: {exception.Message}");
            return document with { Diagnostics = document.Diagnostics.Concat([diagnostic]).ToArray() };
        }
    }

    private async Task<ExtensionEvent> DispatchBeforeAgentStartAsync(
        string prompt,
        IReadOnlyList<ImageContent>? images,
        string systemPrompt,
        IReadOnlyList<IAgentTool> resources,
        CancellationToken cancellationToken)
    {
        var harnessEvent = new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.BeforeAgentStart(prompt, images, systemPrompt, resources));
        var pipelineContext = CreateEventContext(harnessEvent, HarnessEventKind.BeforeAgentStart);
        await _loopEventPipeline.ExecuteAsync(pipelineContext, cancellationToken);
        return pipelineContext.ExtensionEvent;
    }

    private void EmitQueueUpdate()
    {
        _ = PublishOwnEventAsync(
            new AgentHarnessOwnEvent.QueueUpdate(_steerQueue.ToArray(), _followUpQueue.ToArray(), _nextTurnQueue.ToArray()),
            CancellationToken.None);
    }

    private Task<IReadOnlyList<AgentMessage>> DrainSteeringQueueAsync(CancellationToken cancellationToken)
        => DrainQueueAsync(_steerQueue);

    private Task<IReadOnlyList<AgentMessage>> DrainFollowUpQueueAsync(CancellationToken cancellationToken)
        => DrainQueueAsync(_followUpQueue);

    private static void EnsureInjectableMessage(AgentMessage message)
    {
        if (message is ToolResultMessage)
            throw new InvalidOperationException("ToolResultMessage can only be created by the active tool execution pipeline and cannot be injected into a turn.");
    }

    private Task<IReadOnlyList<AgentMessage>> DrainQueueAsync(List<AgentMessage> queue)
    {
        if (queue.Count == 0) return Task.FromResult<IReadOnlyList<AgentMessage>>([]);
        var drained = queue.ToArray();
        queue.Clear();
        EmitQueueUpdate();
        return Task.FromResult<IReadOnlyList<AgentMessage>>(drained);
    }

    private static IReadOnlyDictionary<string, object?> ToolInput(JsonElement args)
        => JsonSerializer.Deserialize<Dictionary<string, object?>>(args.GetRawText()) ?? new Dictionary<string, object?>();

    private IEnumerable<Skill> CurrentSkills()
    {
        var extensionWinners = _extensionSkills.Values.Select(stack => stack[^1].Skill).ToArray();
        var extensionNames = extensionWinners.Select(skill => skill.Name).ToHashSet(StringComparer.Ordinal);
        return _baseSkills.Values.Where(skill => !extensionNames.Contains(skill.Name)).Concat(extensionWinners);
    }

    private Skill? ResolveSkill(string name)
        => _extensionSkills.TryGetValue(name, out var stack) && stack.Count > 0
            ? stack[^1].Skill
            : _baseSkills.GetValueOrDefault(name);

    private sealed class HarnessSubscription(Action unsubscribe) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) unsubscribe();
        }
    }

    private sealed record SkillRegistration(string SourceId, Skill Skill);

    internal sealed record AgentHarnessTurnState(IReadOnlyList<AgentMessage> Messages, IReadOnlyList<IAgentTool> ActiveTools, ModelDescriptor Model, ThinkingLevel ThinkingLevel, ISessionMetadata Metadata);
    internal abstract record PendingWrite
    {
        public sealed record MessageWrite(AgentMessage Message) : PendingWrite;
        public sealed record ModelChange(string Provider, string ModelId) : PendingWrite;
        public sealed record ThinkingChange(string Level) : PendingWrite;
    }
}

public enum AgentHarnessPhase { Idle, Turn, Compaction, BranchSummary }
