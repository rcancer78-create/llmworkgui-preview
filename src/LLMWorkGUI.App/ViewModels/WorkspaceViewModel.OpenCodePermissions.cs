using System.Collections.ObjectModel;
using System.Windows.Input;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.OpenCode.Sessions;

namespace LLMWorkGUI.App.ViewModels;

public sealed partial class WorkspaceViewModel
{
    private OpenCodeJournalEntry? _permissionEntry;
    private OpenCodePendingPermission? _selectedOpenCodePermission;
    private bool _isReplyingOpenCodePermission;
    private string _openCodePermissionStatus = string.Empty;

    public ObservableCollection<OpenCodePendingPermission> OpenCodePendingPermissions { get; } = new();
    public bool HasPendingOpenCodePermissions => OpenCodePendingPermissions.Count > 0;
    public ICommand AllowOpenCodePermissionOnceCommand { get; private set; } = null!;
    public ICommand DenyOpenCodePermissionCommand { get; private set; } = null!;
    public string OpenCodePermissionStatus
    {
        get => _openCodePermissionStatus;
        private set => SetProperty(ref _openCodePermissionStatus, value);
    }
    public OpenCodePendingPermission? SelectedOpenCodePermission
    {
        get => _selectedOpenCodePermission;
        set
        {
            if (value is not null && !OpenCodePendingPermissions.Contains(value)) return;
            if (SetProperty(ref _selectedOpenCodePermission, value)) RelayCommand.RaiseCanExecuteChanged();
        }
    }

    private void InitializeOpenCodePermissionCommands()
    {
        AllowOpenCodePermissionOnceCommand = new RelayCommand(() => _ = ReplyOpenCodePermissionAsync(true),
            () => CanReplyOpenCodePermission(true));
        DenyOpenCodePermissionCommand = new RelayCommand(() => _ = ReplyOpenCodePermissionAsync(false),
            () => CanReplyOpenCodePermission(false));
    }

    private bool CanReplyOpenCodePermission(bool allow) => _turnInProgress && IsSessionConfirmed
        && !_isReplyingOpenCodePermission && _permissionEntry is { } entry
        && entry.SessionId == LocalSessionId && entry.NativeSessionId == NativeSessionId
        && SelectedOpenCodePermission is { } permission && permission.SessionId == entry.NativeSessionId
        && (allow ? permission.CanAllowOnce && !RequiresReplacementSession : permission.CanDeny);

    public async Task ReplyOpenCodePermissionAsync(bool allow)
    {
        if (!CanReplyOpenCodePermission(allow) || _sessionLifecycleService is null) return;
        var entry = _permissionEntry!;
        var selected = SelectedOpenCodePermission!;
        _isReplyingOpenCodePermission = true;
        var replyAttempted = false;
        RelayCommand.RaiseCanExecuteChanged();
        try
        {
            _instanceGuard?.EnsureSupervisorPermitted();
            replyAttempted = true;
            var accepted = await _sessionLifecycleService.ReplyPermissionAsync(entry.NativeSessionId, selected.ReceiptId,
                allow ? OpenCodePermissionReply.Once : OpenCodePermissionReply.Reject);
            if (_permissionEntry?.ExecutionId == entry.ExecutionId)
                OpenCodePermissionStatus = accepted ? "Нативный ответ принят. Ожидается исход выполнения."
                    : "Ответ не подтверждён или запрос изменился. Повторная отправка автоматически не выполняется.";
        }
        catch (Exception)
        {
            if (_permissionEntry?.ExecutionId == entry.ExecutionId)
                OpenCodePermissionStatus = replyAttempted
                    ? OpenCodeTurnNotice.PermissionReplyUncertain(entry.Route.Id, entry.Route.NativeModelId, entry.NativeSessionId)
                    : OpenCodeTurnNotice.PermissionReplyNotSent(entry.Route.Id, entry.Route.NativeModelId, entry.NativeSessionId);
        }
        finally
        {
            _isReplyingOpenCodePermission = false;
            if (_permissionEntry?.ExecutionId == entry.ExecutionId)
            {
                try
                {
                    UpdateOpenCodePermissions(_sessionLifecycleService.GetPendingPermissions(entry.NativeSessionId)
                        .Where(p => p.SessionId == entry.NativeSessionId).ToArray());
                }
                catch (Exception)
                {
                    UpdateOpenCodePermissions(Array.Empty<OpenCodePendingPermission>());
                    if (!replyAttempted)
                        OpenCodePermissionStatus = OpenCodeTurnNotice.PermissionReplyNotSent(
                            entry.Route.Id, entry.Route.NativeModelId, entry.NativeSessionId);
                }
            }
            RelayCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task<TurnResult> ObserveOpenCodePermissionsAsync(OpenCodeJournalEntry entry,
        WorkspaceTurnViewModel turn, OpenCodePromptRequest request)
    {
        _permissionEntry = entry;
        Task<TurnResult>? execution = null;
        try
        {
            execution = _sessionLifecycleService!.ExecuteTurnAsync(entry.NativeSessionId, request);
            var waiting = false;
            while (!execution.IsCompleted)
            {
                await Task.WhenAny(execution, Task.Delay(150));
                if (execution.IsCompleted) break;
                var pending = _sessionLifecycleService.GetPendingPermissions(entry.NativeSessionId)
                    .Where(p => p.SessionId == entry.NativeSessionId).ToArray();
                var nowWaiting = pending.Length > 0;
                if (waiting != nowWaiting)
                {
                    await _executionJournal!.SetWaitingApprovalAsync(entry, nowWaiting);
                    waiting = nowWaiting;
                    turn.Status = waiting ? "WaitingApproval" : "Running";
                    _statusBar?.UpdateSessionState(null, null, null, turn.Status);
                }
                // Enable reply commands only after the durable waiting projection is retained.
                UpdateOpenCodePermissions(pending);
            }
            return await execution;
        }
        catch
        {
            // Failure to retain an honest approval projection must not leave a detached writer unobserved.
            if (execution is not null && !execution.IsCompleted)
            {
                try { await _sessionLifecycleService!.CancelTurnAsync(entry.NativeSessionId).WaitAsync(TimeSpan.FromSeconds(35)); }
                catch (Exception) { /* Caller retains ownership as ambiguous; no retry or unlock. */ }
                _ = execution.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            }
            throw;
        }
        finally
        {
            _permissionEntry = null;
            UpdateOpenCodePermissions(Array.Empty<OpenCodePendingPermission>());
            OpenCodePermissionStatus = string.Empty;
        }
    }

    private void UpdateOpenCodePermissions(IReadOnlyList<OpenCodePendingPermission> pending)
    {
        var selectedId = SelectedOpenCodePermission?.ReceiptId;
        for (var i = 0; i < pending.Count; i++)
        {
            if (i >= OpenCodePendingPermissions.Count) OpenCodePendingPermissions.Add(pending[i]);
            else if (OpenCodePendingPermissions[i] != pending[i]) OpenCodePendingPermissions[i] = pending[i];
        }
        while (OpenCodePendingPermissions.Count > pending.Count)
            OpenCodePendingPermissions.RemoveAt(OpenCodePendingPermissions.Count - 1);
        SelectedOpenCodePermission = OpenCodePendingPermissions.FirstOrDefault(p => p.ReceiptId == selectedId)
            ?? OpenCodePendingPermissions.FirstOrDefault();
        OnPropertyChanged(nameof(HasPendingOpenCodePermissions));
        RelayCommand.RaiseCanExecuteChanged();
    }
}
