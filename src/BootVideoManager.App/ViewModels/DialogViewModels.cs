using CommunityToolkit.Mvvm.Input;

namespace BootVideoManager.App.ViewModels;

/// <summary>Yes/no confirmation displayed as an overlay.</summary>
public sealed partial class ConfirmDialogViewModel(
    string title,
    string message,
    string confirmText,
    bool isDestructive,
    Action<bool> complete) : ViewModelBase
{
    public string Title { get; } = title;

    public string Message { get; } = message;

    public string ConfirmText { get; } = confirmText;

    public bool IsDestructive { get; } = isDestructive;

    [RelayCommand]
    private void Confirm() => complete(true);

    [RelayCommand]
    private void Cancel() => complete(false);
}

/// <summary>Banner message; errors stay until dismissed, information fades out.</summary>
public sealed partial class NotificationViewModel(string message, bool isError, Action<NotificationViewModel> dismiss) : ViewModelBase
{
    public string Message { get; } = message;

    public bool IsError { get; } = isError;

    [RelayCommand]
    private void Dismiss() => dismiss(this);
}
