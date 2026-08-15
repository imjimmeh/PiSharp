using System.Drawing;
using System.Reflection;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;
using PiSharp.Tui.Interactive;
using PiSharp.Tui.Interactive.Components;
using Terminal.Gui;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class PromptEditorTests
{
    [Fact]
    public void PromptEditorUsesNetDriverVisibleTerminalCursorWhenFocused()
    {
        var prompt = new PromptEditor();

        Assert.Equal(CursorVisibility.Default, prompt.CursorVisibility);
    }

    [Fact]
    public void PromptEditorDoesNotKeepShiftTabEditorBinding()
    {
        var prompt = new PromptEditor();

        Assert.False(prompt.KeyBindings.TryGet(Key.Tab.WithShift, out _));
    }

    [Fact]
    public void PositionCursorReturnsPromptCaretPointWithoutWritingAnsi()
    {
        var output = new StringWriter();
        var original = Console.Out;
        try
        {
            Console.SetOut(output);
            var prompt = new PromptEditor { Frame = new Rectangle(0, 0, 5, 3) };
            prompt.SetPromptText("abcdef");

            var cursor = prompt.PositionCursor();

            if (cursor is not null) Assert.Equal(prompt.CursorPosition, cursor);
            Assert.DoesNotContain(AnsiTerminalScreenSession.ShowCursor, output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(AnsiTerminalScreenSession.HideCursor, output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Fact]
    public void PromptEditorCursorPositionMatchesNativeTextViewForSmallViewport()
    {
        var prompt = new PromptEditor { Frame = new Rectangle(0, 0, 5, 1) };
        prompt.SetPromptText("abcdef");

        Assert.Equal(NativeCursorPosition("abcdef", 5, 1), prompt.CursorPosition);
    }

    [Fact]
    public void PromptEditorCursorMatchesNativeTextViewAfterWordWrappedTyping()
    {
        var prompt = new PromptEditor { Frame = new Rectangle(0, 0, 14, 8) };
        var native = new TextView
        {
            Frame = new Rectangle(0, 0, 14, 8),
            WordWrap = true,
            Multiline = true
        };
        const string text = "hello world this wraps";

        foreach (var ch in text)
        {
            prompt.NewKeyDownEvent(new Key((KeyCode)ch));
            native.InsertText(ch.ToString());
        }

        Assert.Equal(text, prompt.PromptText);
        Assert.Equal(text, native.Text.ToString());
        Assert.Equal(native.CursorPosition, prompt.CursorPosition);
        var positioned = prompt.PositionCursor();
        if (positioned is not null) Assert.Equal(prompt.CursorPosition, positioned);
    }

    [Fact]
    public void CursorPositionFollowsSoftWrappedLine()
    {
        var prompt = new PromptEditor { Frame = new Rectangle(0, 0, 5, 3) };

        prompt.SetPromptText("abcdef");

        Assert.Equal(NativeCursorPosition("abcdef", 5, 3), prompt.CursorPosition);
    }

    [Fact]
    public void CursorWrapsUsingEditableTextColumnWidthOnEveryLine()
    {
        var prompt = new PromptEditor { Frame = new Rectangle(0, 0, 5, 6) };

        prompt.SetPromptText("abcdefghijklm");

        Assert.Equal(NativeCursorPosition("abcdefghijklm", 5, 6), prompt.CursorPosition);
    }

    [Fact]
    public void MoveEndKeepsCursorPositionOnSoftWrappedLine()
    {
        var prompt = new PromptEditor { Frame = new Rectangle(0, 0, 5, 3) };
        prompt.SetPromptText("abcdef");
        prompt.NewKeyDownEvent(Key.CursorLeft);

        prompt.MoveEnd();

        Assert.Equal(NativeCursorPosition("abcdef", 5, 3), prompt.CursorPosition);
    }

    [Fact]
    public void PrintableKeysAreInsertedByTextView()
    {
        var prompt = new PromptEditor { Frame = new Rectangle(0, 0, 80, 3) };

        prompt.NewKeyDownEvent(new Key((KeyCode)'a'));
        prompt.NewKeyDownEvent(new Key((KeyCode)'b'));

        Assert.Equal("ab", prompt.PromptText);
        Assert.Equal(new Point(2, 0), prompt.CursorPosition);
    }

    [Fact]
    public void InsertTextUsesTextViewForTabsAndNewlines()
    {
        var prompt = new PromptEditor { Frame = new Rectangle(0, 0, 80, 5) };
        const string text = "def foo():\n\treturn 1";

        prompt.InsertText(text);

        Assert.Equal(text, prompt.PromptText);
        Assert.Equal(text.Length, prompt.CursorOffset);
    }

    [Fact]
    public void MouseClickUsesTextViewCursorForSubsequentInsertion()
    {
        var prompt = new PromptEditor { Frame = new Rectangle(0, 0, 10, 3) };
        prompt.SetPromptText("abcd");

        prompt.NewMouseEvent(new MouseEventArgs
        {
            Flags = MouseFlags.Button1Clicked,
            Position = new Point(1, 0),
            ScreenPosition = new Point(1, 0),
            View = prompt
        });
        prompt.NewKeyDownEvent(new Key((KeyCode)'X'));

        Assert.Equal("axbcd", prompt.PromptText);
    }

    [Fact]
    public void ShiftEnterInsertsNewlineAtPromptCursor()
    {
        var prompt = new PromptEditor();
        prompt.SetPromptText("ab");
        Assert.Equal(2, prompt.CursorOffset);
        prompt.NewKeyDownEvent(Key.CursorLeft);
        Assert.Equal(1, prompt.CursorOffset);

        prompt.NewKeyDownEvent(Key.Enter.WithShift);
        prompt.NewKeyDownEvent(new Key((KeyCode)'X'));
        prompt.NewKeyDownEvent(new Key((KeyCode)'Y'));

        Assert.Equal("a\nxyb", prompt.PromptText);
    }

    [Fact]
    public void CtrlEnterInsertsNewlineAtPromptCursor()
    {
        string? submittedText = null;
        var prompt = new PromptEditor();
        prompt.Submitted += (text, _) =>
        {
            submittedText = text;
            return Task.CompletedTask;
        };
        prompt.SetPromptText("ab");
        prompt.NewKeyDownEvent(Key.CursorLeft);

        prompt.NewKeyDownEvent(Key.Enter.WithCtrl);
        prompt.NewKeyDownEvent(new Key((KeyCode)'X'));

        Assert.Null(submittedText);
        Assert.Equal("a\nxb", prompt.PromptText);
    }

    [Fact]
    public void ShiftedApostropheInsertsAtSignForKeyboardLayoutsWhereAtUsesApostropheKey()
    {
        var prompt = new PromptEditor();

        prompt.NewKeyDownEvent(new Key((KeyCode)'\'').WithShift);

        Assert.Equal("@", prompt.PromptText);
    }

    [Theory]
    [InlineData('1', "!")]
    [InlineData('/', "?")]
    [InlineData(';', ":")]
    [InlineData(',', "<")]
    [InlineData('.', ">")]
    public void ShiftedPunctuationInsertsShiftedSymbol(char unshiftedKey, string expectedText)
    {
        var prompt = new PromptEditor();

        prompt.NewKeyDownEvent(new Key((KeyCode)unshiftedKey).WithShift);

        Assert.Equal(expectedText, prompt.PromptText);
    }

    [Fact]
    public void CursorPositionFollowsExplicitNewline()
    {
        var prompt = new PromptEditor { Frame = new Rectangle(0, 0, 80, 3) };

        prompt.SetPromptText("one\ntwo");

        Assert.Equal(new Point(3, 1), prompt.CursorPosition);
    }

    [Fact]
    public void VerticalNavigationMovesBetweenMultilineInputRows()
    {
        var prompt = new PromptEditor { Frame = new Rectangle(0, 0, 80, 3) };
        prompt.SetPromptText("one\ntwo");

        prompt.NewKeyDownEvent(Key.CursorUp);
        prompt.NewKeyDownEvent(new Key((KeyCode)'X'));

        Assert.Equal("onex\ntwo", prompt.PromptText);
    }

    [Fact]
    public void VerticalNavigationMovesBetweenSoftWrappedRowsWithoutExplicitNewline()
    {
        var prompt = new PromptEditor { Frame = new Rectangle(0, 0, 5, 3) };
        prompt.SetPromptText("abcdef");

        prompt.NewKeyDownEvent(Key.CursorUp);
        prompt.NewKeyDownEvent(new Key((KeyCode)'X'));

        Assert.Equal("abxcdef", prompt.PromptText);
    }

    [Fact]
    public void VerticalNavigationKeepsSuggestionsPrecedence()
    {
        var prompt = new PromptEditor
        {
            CompleteText = text => text.StartsWith("/m", StringComparison.Ordinal) ? ["/model", "/memory"] : []
        };
        prompt.SetPromptText("/m\nbody");

        prompt.NewKeyDownEvent(Key.CursorDown);

        Assert.Equal(1, prompt.SelectedSuggestionIndex);
        Assert.Equal("/memory", prompt.SelectedSuggestion);
    }

    [Fact]
    public void VerticalNavigationMovesFileReferenceSuggestionsForAtCommand()
    {
        var prompt = new PromptEditor
        {
            CompleteText = text => text.Contains('@', StringComparison.Ordinal) ? ["@README.md", "@src/"] : []
        };
        prompt.SetPromptText("Read @");

        prompt.NewKeyDownEvent(Key.CursorDown);

        Assert.Equal(1, prompt.SelectedSuggestionIndex);
        Assert.Equal("@src/", prompt.SelectedSuggestion);
    }

    [Fact]
    public void VerticalNavigationMovesInlineSelectionSuggestionsWhenPromptIsEmpty()
    {
        var transcriptScrolls = new List<int>();
        var prompt = new PromptEditor
        {
            AllowEmptySubmit = true,
            CompleteText = _ => ["openai/gpt-4o", "openai/gpt-4.1"]
        };
        prompt.TranscriptScrollRequested += transcriptScrolls.Add;
        prompt.SetPromptText(string.Empty);

        prompt.NewKeyDownEvent(Key.CursorDown);

        Assert.Equal(1, prompt.SelectedSuggestionIndex);
        Assert.Equal("openai/gpt-4.1", prompt.SelectedSuggestion);
        Assert.Empty(transcriptScrolls);
    }

    [Fact]
    public void InlineSelectionArrowDoesNotScrollTranscriptBeforeSuggestionsLoad()
    {
        var transcriptScrolls = new List<int>();
        var prompt = new PromptEditor
        {
            AllowEmptySubmit = true,
            CompleteText = _ => []
        };
        prompt.TranscriptScrollRequested += transcriptScrolls.Add;
        prompt.SetPromptText(string.Empty);

        prompt.NewKeyDownEvent(Key.CursorDown);

        // While an inline selection is active, arrow keys belong to the picker — they must
        // never be hijacked into transcript scrolling, even before async suggestions arrive.
        Assert.Empty(transcriptScrolls);
    }

    [Fact]
    public void EscapeDismissesVisibleSuggestionsBeforeClearingPromptText()
    {
        var prompt = new PromptEditor
        {
            CompleteText = text => text.Contains('@', StringComparison.Ordinal) ? ["@README.md", "@src/"] : []
        };
        prompt.SetPromptText("Read @");

        prompt.NewKeyDownEvent(Key.Esc);

        Assert.Equal("Read @", prompt.PromptText);
        Assert.Empty(prompt.Suggestions);
        Assert.Equal(-1, prompt.SelectedSuggestionIndex);
    }

    [Fact]
    public void InsertTextNormalizesCarriageReturns()
    {
        var prompt = new PromptEditor();

        prompt.InsertText("a\r\nb\rc");

        Assert.Equal("a\nb\nc", prompt.PromptText);
    }

    [Fact]
    public async Task EnterSubmitRestoresPromptTextWhenHandlerFails()
    {
        var prompt = new PromptEditor();
        prompt.Submitted += (_, _) => throw new InvalidOperationException("submit failed before dispatch");
        prompt.SetPromptText("hello world");

        prompt.NewKeyDownEvent(Key.Enter);
        await Task.Delay(1);

        Assert.Equal("hello world", prompt.PromptText);
        Assert.False(prompt.IsSubmitting);
    }

    [Fact]
    public async Task BracketedPasteInsertsMultilineContentWithoutSubmitting()
    {
        var submitted = false;
        var prompt = new PromptEditor();
        prompt.Submitted += (_, _) =>
        {
            submitted = true;
            return Task.CompletedTask;
        };

        foreach (var ch in "\u001b[200~one\ntwo\u001b[201~")
        {
            prompt.NewKeyDownEvent(ch == '\n' ? new Key((KeyCode)'\n') : new Key((KeyCode)ch));
        }
        await Task.Delay(1);

        Assert.False(submitted);
        Assert.Equal("one\ntwo", prompt.PromptText);
        Assert.Equal(prompt.PromptText.Length, prompt.CursorOffset);
    }

    [Fact]
    public async Task BracketedPasteSurvivesTerminalGuiMarkerCoercionToInsertKey()
    {
        var submitted = false;
        var prompt = new PromptEditor();
        prompt.Submitted += (_, _) =>
        {
            submitted = true;
            return Task.CompletedTask;
        };

        prompt.NewKeyDownEvent(new Key(KeyCode.Insert));

        foreach (var ch in "one\ntwo")
        {
            prompt.NewKeyDownEvent(ch switch
            {
                '\u001b' => Key.Esc,
                '\n' => new Key((KeyCode)'\n'),
                _ => new Key((KeyCode)ch)
            });
        }

        prompt.NewKeyDownEvent(new Key(KeyCode.Insert));
        await Task.Delay(1);

        Assert.False(submitted);
        Assert.Equal("one\ntwo", prompt.PromptText);
        Assert.Equal(prompt.PromptText.Length, prompt.CursorOffset);
    }

    [Fact]
    public async Task ClipboardPasteInsertsMultilineContentWithoutSubmitting()
    {
        var submitted = false;
        var originalClipboard = Clipboard.Contents;
        try
        {
            Clipboard.Contents = "one\ntwo";
            var prompt = new PromptEditor();
            prompt.Submitted += (_, _) =>
            {
                submitted = true;
                return Task.CompletedTask;
            };

            prompt.NewKeyDownEvent(Key.V.WithCtrl);
            await Task.Delay(1);

            Assert.False(submitted);
            Assert.Equal("one\ntwo", prompt.PromptText);
            Assert.Equal(prompt.PromptText.Length, prompt.CursorOffset);
        }
        finally
        {
            Clipboard.Contents = originalClipboard;
        }
    }

    [Fact]
    public async Task UnmarkedTerminalPasteCompletesMultilineClipboardWithoutSubmitting()
    {
        var submitted = false;
        var originalClipboard = Clipboard.Contents;
        try
        {
            Clipboard.Contents = "one\ntwo";
            var prompt = new PromptEditor();
            prompt.Submitted += (_, _) =>
            {
                submitted = true;
                return Task.CompletedTask;
            };

            foreach (var ch in "one") prompt.NewKeyDownEvent(new Key((KeyCode)ch));
            prompt.NewKeyDownEvent(Key.Enter);

            Assert.Equal("one\ntwo", prompt.PromptText);

            foreach (var ch in "two") prompt.NewKeyDownEvent(new Key((KeyCode)ch));
            await Task.Delay(1);

            Assert.False(submitted);
            Assert.Equal("one\ntwo", prompt.PromptText);
            Assert.Equal(prompt.PromptText.Length, prompt.CursorOffset);
        }
        finally
        {
            Clipboard.Contents = originalClipboard;
        }
    }

    [Fact]
    public void PrintableKeyRefreshesSuggestionsOnce()
    {
        var calls = 0;
        var prompt = new PromptEditor { Complete = (_, _) => { calls++; return []; } };

        prompt.NewKeyDownEvent(new Key((KeyCode)'@'));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PromptSuggestionsIgnoreStaleAsyncCompletionResults()
    {
        var completions = new Dictionary<string, TaskCompletionSource<IReadOnlyList<PromptCompletion>>>(StringComparer.Ordinal);
        var prompt = new PromptEditor
        {
            AsyncCompletionDebounceDelay = TimeSpan.Zero,
            CompleteAsync = (text, _, _) =>
            {
                var completion = new TaskCompletionSource<IReadOnlyList<PromptCompletion>>(TaskCreationOptions.RunContinuationsAsynchronously);
                completions[text] = completion;
                return completion.Task;
            }
        };

        prompt.SetPromptText("@a");
        prompt.SetPromptText("@ab");
        completions["@ab"].SetResult([new PromptCompletion("@ab-result", "@ab-result", Prefix: "@ab")]);
        await WaitForConditionAsync(() => prompt.Suggestions.SequenceEqual(["@ab-result"]), TimeSpan.FromSeconds(1));
        completions["@a"].SetResult([new PromptCompletion("@a-result", "@a-result", Prefix: "@a")]);
        await Task.Delay(25);

        Assert.Equal(["@ab-result"], prompt.Suggestions);
    }

    [Fact]
    public async Task PromptAsyncCompletionDebounceAvoidsImmediateInvocationForEveryEdit()
    {
        var calls = 0;
        var prompt = new PromptEditor
        {
            AsyncCompletionDebounceDelay = TimeSpan.FromMilliseconds(50),
            CompleteAsync = (text, _, _) =>
            {
                calls++;
                return Task.FromResult<IReadOnlyList<PromptCompletion>>([new PromptCompletion(text, text, Prefix: text)]);
            }
        };

        foreach (var text in new[] { "@", "@a", "@ab", "@abc" })
        {
            prompt.SetPromptText(text);
        }

        Assert.Equal(0, calls);
        await WaitForConditionAsync(() => calls == 1, TimeSpan.FromSeconds(1));
        Assert.Equal(["@abc"], prompt.Suggestions);
    }

    [Fact]
    public async Task PromptAsyncCompletionPostsSuggestionUpdatesBeforeApplyingThem()
    {
        var posts = new Queue<Action>();
        var prompt = new PromptEditor
        {
            AsyncCompletionDebounceDelay = TimeSpan.Zero,
            PostSuggestionUpdate = action => posts.Enqueue(action),
            CompleteAsync = (text, _, _) => Task.FromResult<IReadOnlyList<PromptCompletion>>([new PromptCompletion($"{text}-result", $"{text}-result")])
        };

        prompt.SetPromptText("@a");
        await WaitForConditionAsync(() => posts.Count == 1, TimeSpan.FromSeconds(1));

        Assert.Empty(prompt.Suggestions);
        posts.Dequeue()();

        Assert.Equal(["@a-result"], prompt.Suggestions);
    }

    [Fact]
    public async Task PromptAsyncCompletionKeepsCanceledTokenUsableUntilWorkCompletes()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registrationSucceeded = false;
        var registrationDisposed = false;
        var prompt = new PromptEditor
        {
            AsyncCompletionDebounceDelay = TimeSpan.Zero,
            CompleteAsync = async (text, _, token) =>
            {
                if (text == "@a")
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task;
                    try
                    {
                        using var registration = token.Register(() => { });
                        registrationSucceeded = true;
                    }
                    catch (ObjectDisposedException)
                    {
                        registrationDisposed = true;
                    }
                }

                return [new PromptCompletion(text, text)];
            }
        };

        prompt.SetPromptText("@a");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        prompt.SetPromptText("@ab");
        releaseFirst.SetResult();
        await WaitForConditionAsync(() => registrationSucceeded || registrationDisposed, TimeSpan.FromSeconds(1));

        Assert.True(registrationSucceeded);
        Assert.False(registrationDisposed);
    }

    [Fact]
    public async Task PromptAsyncCompletionDoesNotRepopulateSuggestionsAfterSubmit()
    {
        var posts = new Queue<Action>();
        var completion = new TaskCompletionSource<IReadOnlyList<PromptCompletion>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompt = new PromptEditor
        {
            AsyncCompletionDebounceDelay = TimeSpan.Zero,
            PostSuggestionUpdate = action => posts.Enqueue(action),
            CompleteAsync = (_, _, _) => completion.Task
        };
        prompt.Submitted += (_, _) => Task.CompletedTask;

        prompt.SetPromptText("@a");
        await prompt.SubmitAsync();
        completion.SetResult([new PromptCompletion("@a-result", "@a-result", Prefix: "@a")]);
        await Task.Delay(25);
        while (posts.Count > 0) posts.Dequeue()();

        Assert.Empty(prompt.Suggestions);
        Assert.Equal(string.Empty, prompt.PromptText);
    }

    [Fact]
    public void ControlKeyDoesNotRefreshSuggestionsWhenEditorTextDoesNotChange()
    {
        var calls = 0;
        var prompt = new PromptEditor { Complete = (_, _) => { calls++; return []; } };

        prompt.NewKeyDownEvent(Key.C.WithCtrl);

        Assert.Equal(0, calls);
    }

    [Fact]
    public void CtrlCIsSuppressedByPromptWithoutEditingText()
    {
        var prompt = new PromptEditor();
        prompt.SetPromptText("to-clear");
        var key = Key.C.WithCtrl;

        prompt.NewKeyDownEvent(key);

        Assert.True(key.Handled);
        Assert.Equal("to-clear", prompt.PromptText);
    }

    [Fact]
    public void PendingEditorTextIsConsumedAfterApplyingToPrompt()
    {
        var type = typeof(TuiHost).Assembly.GetType("PiSharp.Tui.Interactive.TuiPendingEditorText", true)!;
        var method = type.GetMethod("Apply", BindingFlags.Static | BindingFlags.Public)!;
        var state = TuiRenderState.Empty("sid", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null)
            .SetEditorText(string.Empty);
        var prompt = new PromptEditor(PromptEditorKeyMap.CreateEmpty());
        prompt.SetPromptText("/help");

        state = (TuiRenderState)method.Invoke(null, [state, prompt])!;
        prompt.InsertText("/");

        Assert.Null(state.EditorText);
        Assert.Equal("/", prompt.PromptText);
    }

    // ---- Phase 4A behavioral probe tests ----

    [Fact]
    public void CursorLeftDecrementsCursorOffset()
    {
        var prompt = new PromptEditor();
        prompt.SetPromptText("abc");
        Assert.Equal(3, prompt.CursorOffset);

        prompt.NewKeyDownEvent(Key.CursorLeft);
        Assert.Equal(2, prompt.CursorOffset);

        prompt.NewKeyDownEvent(Key.CursorLeft);
        Assert.Equal(1, prompt.CursorOffset);

        prompt.NewKeyDownEvent(Key.CursorLeft);
        Assert.Equal(0, prompt.CursorOffset);

        prompt.NewKeyDownEvent(Key.CursorLeft);
        Assert.Equal(0, prompt.CursorOffset);
    }

    [Fact]
    public void CursorRightMatchesNativeTextViewBehavior()
    {
        var prompt = new PromptEditor();
        var native = new TextView { WordWrap = true, Multiline = true };
        prompt.SetPromptText("abc");
        native.InsertText("abc");
        prompt.NewKeyDownEvent(Key.CursorLeft);
        prompt.NewKeyDownEvent(Key.CursorLeft);
        prompt.NewKeyDownEvent(Key.CursorLeft);
        native.NewKeyDownEvent(Key.CursorLeft);
        native.NewKeyDownEvent(Key.CursorLeft);
        native.NewKeyDownEvent(Key.CursorLeft);

        Assert.Equal(native.CursorPosition, prompt.CursorPosition);

        prompt.NewKeyDownEvent(Key.CursorRight);
        native.NewKeyDownEvent(Key.CursorRight);
        Assert.Equal(native.CursorPosition, prompt.CursorPosition);

        prompt.NewKeyDownEvent(Key.CursorRight);
        native.NewKeyDownEvent(Key.CursorRight);
        Assert.Equal(native.CursorPosition, prompt.CursorPosition);

        prompt.NewKeyDownEvent(Key.CursorRight);
        native.NewKeyDownEvent(Key.CursorRight);
        Assert.Equal(native.CursorPosition, prompt.CursorPosition);

        prompt.NewKeyDownEvent(Key.CursorRight);
        native.NewKeyDownEvent(Key.CursorRight);
        Assert.Equal(native.CursorPosition, prompt.CursorPosition);
    }

    [Fact]
    public void BackspaceDeletesCharacterLeftOfCursor()
    {
        var prompt = new PromptEditor();
        prompt.SetPromptText("abcdef");
        Assert.Equal(6, prompt.CursorOffset);

        prompt.NewKeyDownEvent(Key.Backspace);
        Assert.Equal("abcde", prompt.PromptText);
        Assert.Equal(5, prompt.CursorOffset);

        prompt.NewKeyDownEvent(Key.CursorLeft);
        prompt.NewKeyDownEvent(Key.CursorLeft);
        Assert.Equal(3, prompt.CursorOffset);

        prompt.NewKeyDownEvent(Key.Backspace);
        Assert.Equal("abde", prompt.PromptText);
        Assert.Equal(2, prompt.CursorOffset);
    }

    [Fact]
    public void DeleteDeletesCharacterRightOfCursor()
    {
        var prompt = new PromptEditor();
        prompt.SetPromptText("abcdef");
        prompt.NewKeyDownEvent(Key.CursorLeft);
        prompt.NewKeyDownEvent(Key.CursorLeft);
        Assert.Equal(4, prompt.CursorOffset);

        prompt.NewKeyDownEvent(Key.Delete);
        Assert.Equal("abcdf", prompt.PromptText);
        Assert.Equal(4, prompt.CursorOffset);

        prompt.NewKeyDownEvent(Key.Delete);
        Assert.Equal("abcd", prompt.PromptText);
        Assert.Equal(4, prompt.CursorOffset);
    }

    [Fact]
    public void EnterSubmitsTextWhenShiftIsNotHeld()
    {
        string? submittedText = null;
        var prompt = new PromptEditor();
        prompt.Submitted += (text, _) =>
        {
            submittedText = text;
            return Task.CompletedTask;
        };
        prompt.SetPromptText("hello");

        prompt.NewKeyDownEvent(Key.Enter);

        Assert.Equal("hello", submittedText);
        Assert.Equal(string.Empty, prompt.PromptText);
    }

    [Fact]
    public void EnterDoesNotSubmitWhenShiftIsHeld()
    {
        string? submittedText = null;
        var prompt = new PromptEditor();
        prompt.Submitted += (text, _) =>
        {
            submittedText = text;
            return Task.CompletedTask;
        };
        prompt.SetPromptText("hello");

        prompt.NewKeyDownEvent(Key.Enter.WithShift);

        Assert.Null(submittedText);
        Assert.Contains("\n", prompt.PromptText, StringComparison.Ordinal);
    }

    [Fact]
    public void EnterDoesNotSubmitEmptyTextByDefault()
    {
        string? submittedText = null;
        var prompt = new PromptEditor();
        prompt.Submitted += (text, _) =>
        {
            submittedText = text;
            return Task.CompletedTask;
        };

        prompt.NewKeyDownEvent(Key.Enter);

        Assert.Null(submittedText);
    }

    [Fact]
    public void EnterSubmitsEmptyTextWhenAllowed()
    {
        string? submittedText = null;
        var prompt = new PromptEditor { AllowEmptySubmit = true };
        prompt.Submitted += (text, _) =>
        {
            submittedText = text;
            return Task.CompletedTask;
        };

        prompt.NewKeyDownEvent(Key.Enter);

        Assert.NotNull(submittedText);
        Assert.Equal(string.Empty, submittedText);
    }

    [Fact]
    public void TabAcceptsFirstSuggestion()
    {
        var prompt = new PromptEditor
        {
            CompleteText = text => text.StartsWith("/m", StringComparison.Ordinal) ? ["/model", "/memory"] : []
        };
        prompt.SetPromptText("/m");

        prompt.NewKeyDownEvent(Key.Tab);

        Assert.Equal("/model ", prompt.PromptText);
        Assert.Equal("/model ".Length, prompt.CursorOffset);
    }

    [Fact]
    public void TabAcceptsSuggestionReplacingFullPromptWhenPrefixMatchesAll()
    {
        var prompt = new PromptEditor
        {
            CompleteText = text => text.EndsWith("@", StringComparison.Ordinal) ? ["@README.md", "@src/"] : []
        };
        prompt.SetPromptText("Read @");

        prompt.NewKeyDownEvent(Key.Tab);

        Assert.Equal("@README.md ", prompt.PromptText);
    }

    [Fact]
    public void UpNavigatesHistoryWhenPromptIsEmpty()
    {
        var prompt = new PromptEditor();
        prompt.RecordSubmittedPrompt("first");
        prompt.RecordSubmittedPrompt("second");

        prompt.NewKeyDownEvent(Key.CursorUp);

        Assert.Equal("second", prompt.PromptText);
        Assert.Equal("second".Length, prompt.CursorOffset);
    }

    [Fact]
    public void UpFromEmptyNavigatesToLastHistoryEntry()
    {
        var prompt = new PromptEditor();
        prompt.RecordSubmittedPrompt("first");
        prompt.RecordSubmittedPrompt("second");

        prompt.NewKeyDownEvent(Key.CursorUp);

        Assert.Equal("second", prompt.PromptText);
        Assert.Equal("second".Length, prompt.CursorOffset);
    }

    [Fact]
    public void HistoryNavigationChainsMultipleMovesWithoutSpuriousReset()
    {
        var prompt = new PromptEditor();
        prompt.RecordSubmittedPrompt("first");
        prompt.RecordSubmittedPrompt("second");

        prompt.NewKeyDownEvent(Key.CursorUp);
        Assert.Equal("second", prompt.PromptText);

        prompt.NewKeyDownEvent(Key.CursorUp);
        Assert.Equal("first", prompt.PromptText);

        prompt.NewKeyDownEvent(Key.CursorDown);
        Assert.Equal("second", prompt.PromptText);
    }

    [Fact]
    public void UpNavigatesSuggestionsWhenCompletionsAreVisible()
    {
        var prompt = new PromptEditor
        {
            CompleteText = text => text.StartsWith("/m", StringComparison.Ordinal) ? ["/model", "/memory"] : []
        };
        prompt.SetPromptText("/m");

        prompt.NewKeyDownEvent(Key.CursorDown);

        Assert.Equal(1, prompt.SelectedSuggestionIndex);
        Assert.Equal("/memory", prompt.SelectedSuggestion);
    }

    [Fact]
    public void PositionCursorDoesNotAlterViewFrame()
    {
        var prompt = new PromptEditor { Frame = new Rectangle(10, 5, 10, 6) };
        prompt.SetPromptText("abcdefghijklmnopqrstuvwxyz"); // 26 chars, wraps across 3 lines at width 10

        var frameBefore = prompt.Frame;
        prompt.PositionCursor();
        var frameAfter = prompt.Frame;

        Assert.Equal(frameBefore.X, frameAfter.X);
        Assert.Equal(frameBefore.Y, frameAfter.Y);
    }

    private static Point NativeCursorPosition(string text, int width, int height)
    {
        var native = new TextView
        {
            Frame = new Rectangle(0, 0, width, height),
            WordWrap = true,
            Multiline = true
        };
        native.InsertText(text);
        return native.CursorPosition;
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopAt = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < stopAt)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.True(condition(), "Expected condition to become true before timeout.");
    }
}
