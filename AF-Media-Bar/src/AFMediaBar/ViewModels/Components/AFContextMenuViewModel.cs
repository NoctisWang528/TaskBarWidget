using AFMediaBar.Classes.Models;
using AFMediaBar.Classes.Models.Updates;
using AFMediaBar.Classes.Services;
using AFMediaBar.Classes.Services.Localization;
using AFMediaBar.Classes.Services.Updates;
using System.Collections.ObjectModel;

namespace AFMediaBar.ViewModels.Components;

/// <summary>维护任务栏右键菜单的媒体选择与更新提示，不拥有媒体或更新服务。/ Holds media selection and update notice state for the taskbar menu; services belong to the host.</summary>
public partial class AFContextMenuViewModel : ObservableObject
{
    private readonly MediaSessionService _mediaSessionService;
    private readonly UpdateService _updateService;

    [ObservableProperty] private string _updateMenuHeader = string.Empty;

    [ObservableProperty] private bool _isUpdateMenuVisible;

    [ObservableProperty] private bool _isReloadTaskbarHostEnabled = true;

    [ObservableProperty] private ObservableCollection<MediaSessionOption> _sessions = [];

    public AFContextMenuViewModel(
        MediaSessionService mediaSessionService,
        UpdateService updateService,
        LocalizationService localization)
    {
        _mediaSessionService = mediaSessionService;
        _updateService = updateService;
        _updateService.UpdateStateChanged += ApplyUpdateState;
        localization.LanguageChanged += (_, _) => ApplyUpdateState(_updateService.CurrentState);
        ApplyUpdateState(_updateService.CurrentState);
    }

    [RelayCommand]
    private void SelectMediaSession(string? key)
    {
        _mediaSessionService.SelectSession(key ?? string.Empty);
    }

    [RelayCommand]
    private async Task ReconnectMediaSession()
    {
        await _mediaSessionService.ReconnectAsync();
    }

    private void ApplyUpdateState(UpdateState state)
    {
        IsUpdateMenuVisible = UpdatePresentationPolicy.ShouldShowTrayNotice(state);
        UpdateMenuHeader = IsUpdateMenuVisible ? UpdatePresentationPolicy.ResolveTrayHeader(state) : string.Empty;
    }
}
