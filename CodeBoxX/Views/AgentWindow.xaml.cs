using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CodeBoxX.Models;
using CodeBoxX.Services;

namespace CodeBoxX.Views;

public partial class AgentWindow : Window
{
    private readonly GeminiService _gemini;
    private readonly ProjectAgentService _agent;
    private readonly Func<string?> _workspacePathProvider;
    private readonly Func<bool> _hasApiKey;
    private readonly Action _focusTerminal;
    private readonly Func<Task> _runActiveFile;
    private readonly Func<Task> _buildWorkspace;
    private readonly Func<IReadOnlyList<string>, bool> _prepareApply;
    private readonly Action<IReadOnlyList<string>> _workspaceChanged;
    private readonly ObservableCollection<AiChatMessage> _messages = [];
    private readonly ObservableCollection<AgentFileChange> _modifiedFiles = [];
    private AgentWorkspaceSnapshot? _workspaceSnapshot;
    private AgentProposal? _pendingProposal;
    private CancellationTokenSource? _requestCancellation;
    private string _lastUserRequest = string.Empty;
    private bool _workspaceAccessGranted;
    private bool _isBusy;

    public AgentWindow(
        GeminiService gemini,
        ProjectAgentService agent,
        Func<string?> workspacePathProvider,
        Func<bool> hasApiKey,
        Action focusTerminal,
        Func<Task> runActiveFile,
        Func<Task> buildWorkspace,
        Func<IReadOnlyList<string>, bool> prepareApply,
        Action<IReadOnlyList<string>> workspaceChanged)
    {
        InitializeComponent();
        _gemini = gemini;
        _agent = agent;
        _workspacePathProvider = workspacePathProvider;
        _hasApiKey = hasApiKey;
        _focusTerminal = focusTerminal;
        _runActiveFile = runActiveFile;
        _buildWorkspace = buildWorkspace;
        _prepareApply = prepareApply;
        _workspaceChanged = workspaceChanged;
        ChatList.ItemsSource = _messages;
        ModifiedFilesList.ItemsSource = _modifiedFiles;
        RefreshApiStatus();
        RefreshProposalState();
    }

    public void ShowAgent()
    {
        Owner ??= Application.Current.MainWindow;
        RefreshApiStatus();
        if (!IsVisible) Show();
        Activate();
        PromptBox.Focus();
    }

    private void RefreshApiStatus()
    {
        ModelStatusText.Text = "Gemini 3.1 Flash-Lite";
        ConnectionStatusText.Text = _hasApiKey() ? "API key saved locally with Windows protection" : "API key required — open AI Settings";
    }

    private void AllowWorkspaceRead_Click(object sender, RoutedEventArgs e)
    {
        if (!_workspaceAccessGranted)
        {
            var result = MessageBox.Show(
                "Allow CodeBox X Agent to read a limited, redacted snapshot of the currently open workspace?\n\nIt will list up to 120 safe text files and send content from up to 24 safe text files to Gemini. API keys, tokens, passwords, .env files, protected folders, build outputs, and binary files are excluded or redacted. No file will be changed.",
                "Allow Agent Workspace Read",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
            {
                StatusText.Text = "Workspace access was not granted.";
                return;
            }
            _workspaceAccessGranted = true;
        }

        _workspaceSnapshot = _agent.CreateWorkspaceSnapshot(_workspacePathProvider());
        WorkspaceStatusText.Text = _workspaceSnapshot.UserMessage;
        StatusText.Text = _workspaceSnapshot.IsAvailable ? "Workspace context is ready for the Agent." : _workspaceSnapshot.UserMessage;
        if (_workspaceSnapshot.IsAvailable)
        {
            var workspaceMessage = _workspaceSnapshot.FileCount == 0
                ? "Workspace access is ready. This workspace is empty, so there are no files to analyze yet. You can still ask me to create a safe new file such as index.html; after sending that request, choose Plan Changes to review it before anything is written."
                : $"Workspace context is ready. I can review {_workspaceSnapshot.FileCount} safe project file(s) and received redacted content from {_workspaceSnapshot.IncludedContentFileCount} file(s). Ask a question, search the project, or request a change plan.";
            _messages.Add(new AiChatMessage { Role = "Assistant", Text = workspaceMessage });
            ScrollToLatest();
        }
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var prompt = PromptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            StatusText.Text = "Enter a question, project search term, or change request.";
            PromptBox.Focus();
            return;
        }
        _lastUserRequest = prompt;
        await SendAgentChatAsync(prompt);
    }

    private async void AnalyzeWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureWorkspaceAccess()) return;
        await SendAgentChatAsync("Analyze the approved workspace. Summarize its structure, likely entry points, likely errors or risks, and the highest-value next steps. Do not propose or apply file changes unless I specifically ask for a change plan.");
    }

    private async void PlanChanges_Click(object sender, RoutedEventArgs e)
    {
        var request = PromptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(request)) request = _lastUserRequest;
        if (string.IsNullOrWhiteSpace(request))
        {
            StatusText.Text = "Describe the changes you want the Agent to plan first, or send a request in chat and then choose Plan Changes.";
            PromptBox.Focus();
            return;
        }
        if (!EnsureWorkspaceAccess()) return;
        if (_isBusy) return;

        _messages.Add(new AiChatMessage { Role = "You", Text = "Plan changes: " + request });
        ScrollToLatest();
        SetBusy(true);
        try
        {
            var result = await SendGeminiAsync(AiEditorAction.AgentPlan, request);
            if (!result.Success)
            {
                AddGeminiFailure(result.UserMessage);
                return;
            }
            if (!_agent.TryParseProposal(result.Text, _workspacePathProvider(), out var proposal, out var validationMessage))
            {
                _messages.Add(new AiChatMessage { Role = "Assistant", Text = validationMessage });
                StatusText.Text = validationMessage;
                return;
            }
            if (!proposal.HasChanges)
            {
                _messages.Add(new AiChatMessage { Role = "Assistant", Text = string.IsNullOrWhiteSpace(validationMessage) ? "The Agent returned no file changes to review." : validationMessage });
                StatusText.Text = "No Agent file changes were staged.";
                return;
            }

            _pendingProposal = proposal;
            RefreshProposalState();
            PromptBox.Clear();
            var warning = proposal.RequiresDeletionConfirmation ? " This proposal includes deletion and requires an additional confirmation." : string.Empty;
            _messages.Add(new AiChatMessage { Role = "Assistant", Text = $"Proposed change plan: {proposal.Summary}\n\n{proposal.Explanation}\n\nReview the Modified Files list, then choose Accept Changes or Reject Changes. No files have been changed.{warning}" });
            ConnectionStatusText.Text = "Last Gemini request succeeded";
            StatusText.Text = $"{proposal.Changes.Count} Agent change(s) are waiting for your review.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Agent planning was stopped. No files were changed.";
            _messages.Add(new AiChatMessage { Role = "Assistant", Text = "Agent planning was stopped. No files were changed." });
        }
        finally
        {
            SetBusy(false);
            ScrollToLatest();
        }
    }

    private async Task SendAgentChatAsync(string prompt)
    {
        if (_isBusy) return;
        _messages.Add(new AiChatMessage { Role = "You", Text = prompt });
        ScrollToLatest();
        SetBusy(true);
        try
        {
            var result = await SendGeminiAsync(AiEditorAction.AgentChat, prompt);
            if (result.Success)
            {
                _messages.Add(new AiChatMessage { Role = "Assistant", Text = result.Text });
                PromptBox.Clear();
                ConnectionStatusText.Text = "Last Gemini request succeeded";
                StatusText.Text = "Agent response ready.";
            }
            else
            {
                AddGeminiFailure(result.UserMessage);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Agent request was stopped.";
            _messages.Add(new AiChatMessage { Role = "Assistant", Text = "Agent request was stopped." });
        }
        finally
        {
            SetBusy(false);
            ScrollToLatest();
        }
    }

    private async Task<GeminiResult> SendGeminiAsync(AiEditorAction action, string prompt)
    {
        _requestCancellation?.Dispose();
        _requestCancellation = new CancellationTokenSource();
        var projectSummary = _workspaceAccessGranted && _workspaceSnapshot?.IsAvailable == true
            ? _workspaceSnapshot.Summary
            : "Workspace access has not been granted. Do not infer or claim any project files, project structure, or file contents.";
        return await _gemini.GenerateAsync(new AiRequestContext
        {
            Action = action,
            UserPrompt = prompt,
            ProjectSummary = projectSummary,
            CurrentFileName = null,
            CurrentLanguage = null,
            SelectedCode = null
        }, _requestCancellation.Token);
    }

    private bool EnsureWorkspaceAccess()
    {
        if (!_workspaceAccessGranted || _workspaceSnapshot?.IsAvailable != true) AllowWorkspaceRead_Click(this, new RoutedEventArgs());
        return _workspaceAccessGranted && _workspaceSnapshot?.IsAvailable == true;
    }

    private void SearchProject_Click(object sender, RoutedEventArgs e)
    {
        var query = PromptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            StatusText.Text = "Enter text in the Agent input, then choose Search Project.";
            PromptBox.Focus();
            return;
        }
        if (!EnsureWorkspaceAccess()) return;
        var hits = _agent.SearchWorkspace(_workspacePathProvider(), query);
        var message = hits.Count == 0
            ? $"No safe workspace matches were found for '{query}'."
            : $"Project search results for '{query}':\n" + string.Join(Environment.NewLine, hits.Select(hit => hit.DisplayName));
        _messages.Add(new AiChatMessage { Role = "Assistant", Text = message });
        StatusText.Text = hits.Count == 0 ? "No project matches found." : $"Found {hits.Count} project match(es).";
        ScrollToLatest();
    }

    private async void RunActiveFile_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Run the current active file using CodeBox X's existing Run Active File command? The Agent will not alter the command or file.", "Confirm Run Active File", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await _runActiveFile();
        StatusText.Text = "Run Active File was requested through CodeBox X.";
    }

    private async void BuildWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Check this workspace for a .NET project and build it only if one is found? This may create normal build outputs. If no .NET project exists, CodeBox X will not run a command. Any command will be shown in the integrated terminal.", "Confirm Workspace Build", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _buildWorkspace();
    }

    private void FocusTerminal_Click(object sender, RoutedEventArgs e)
    {
        _focusTerminal();
        StatusText.Text = "Integrated terminal focused. The Agent never sends terminal commands automatically.";
    }

    private void AcceptChanges_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingProposal is null) return;
        var proposal = _pendingProposal;
        var reviewMessage = $"Apply {proposal.Changes.Count} reviewed Agent change(s)?\n\n{proposal.ModifiedFilesSummary}\n\nThe previous versions will be retained for one-step Agent undo.";
        if (MessageBox.Show(reviewMessage, "Accept Agent Changes", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (proposal.RequiresDeletionConfirmation && MessageBox.Show("This proposal deletes one or more files. Confirm deletion after reviewing the file list.", "Confirm Agent File Deletion", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (proposal.RequiresLargeChangeConfirmation && MessageBox.Show("This proposal changes more than eight files. Confirm that you want to apply this project-wide change.", "Confirm Large Agent Change", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!_prepareApply(proposal.Changes.Select(change => change.RelativePath).ToList())) return;

        var result = _agent.ApplyAcceptedProposal(proposal, _workspacePathProvider());
        _messages.Add(new AiChatMessage { Role = "Assistant", Text = result.Message });
        StatusText.Text = result.Message;
        if (result.Success)
        {
            _workspaceChanged(result.ChangedPaths);
            _pendingProposal = null;
            RefreshProposalState();
        }
        ScrollToLatest();
    }

    private void RejectChanges_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingProposal is null) return;
        _pendingProposal = null;
        RefreshProposalState();
        _messages.Add(new AiChatMessage { Role = "Assistant", Text = "The reviewed Agent proposal was rejected. No files were changed." });
        StatusText.Text = "Agent proposal rejected. No files were changed.";
        ScrollToLatest();
    }

    private void UndoChanges_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Undo the most recently accepted Agent changes and restore the previous file contents?", "Undo Agent Changes", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var result = _agent.UndoLastChanges();
        _messages.Add(new AiChatMessage { Role = "Assistant", Text = result.Message });
        StatusText.Text = result.Message;
        if (result.Success) _workspaceChanged(result.ChangedPaths);
        ScrollToLatest();
    }

    private void ClearChat_Click(object sender, RoutedEventArgs e)
    {
        _messages.Clear();
        _lastUserRequest = string.Empty;
        StatusText.Text = "Agent chat cleared. Reviewed changes remain available until accepted or rejected.";
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _requestCancellation?.Cancel();
        StatusText.Text = "Stopping Agent request…";
    }

    private void RefreshProposalState()
    {
        _modifiedFiles.Clear();
        if (_pendingProposal is null)
        {
            ProposalSummaryText.Text = "No changes are waiting for review.";
            AcceptChangesButton.IsEnabled = false;
            RejectChangesButton.IsEnabled = false;
            return;
        }
        foreach (var change in _pendingProposal.Changes) _modifiedFiles.Add(change);
        ProposalSummaryText.Text = _pendingProposal.Summary;
        AcceptChangesButton.IsEnabled = !_isBusy;
        RejectChangesButton.IsEnabled = !_isBusy;
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        SendButton.IsEnabled = !busy;
        PlanChangesButton.IsEnabled = !busy;
        StopButton.IsEnabled = busy;
        RefreshProposalState();
        if (busy) StatusText.Text = "Gemini is working. Use Stop to cancel the request.";
    }

    private void AddGeminiFailure(string message)
    {
        _messages.Add(new AiChatMessage { Role = "Assistant", Text = message });
        ConnectionStatusText.Text = "Gemini request failed — check AI Settings";
        StatusText.Text = message;
    }

    private void ScrollToLatest()
    {
        if (_messages.Count > 0) ChatList.ScrollIntoView(_messages[^1]);
    }

    private void AgentWindow_Closing(object? sender, CancelEventArgs e)
    {
        _requestCancellation?.Cancel();
        e.Cancel = true;
        Hide();
    }
}
