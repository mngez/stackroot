# Complete Application Translation and Localization

This plan outlines the steps to complete the translation of the Stackroot application to Arabic (and support dynamic language switching) across all main pages and dialogs using the keys already defined in `en.xaml` and `ar.xaml`.

## User Review Required

> [!NOTE]
> Since the translation keys are already defined in `en.xaml` and `ar.xaml` for all views, we will systematically update the XAML files to replace hardcoded strings with `{DynamicResource Loc.X}` references. We will also add a `LanguageChanged` event to `LocalizationManager` to update the navigation bar and view model properties dynamically.

## Open Questions

None. The translation keys are already fully specified in the language resource dictionaries (`en.xaml` and `ar.xaml`).

## Proposed Changes

### Localization Infrastructure

#### [MODIFY] [LocalizationManager.cs](file:///c:/Users/omarf/WWW/dev/src/Stackroot.App/Localization/LocalizationManager.cs)
- Add `public static event EventHandler? LanguageChanged;`
- Raise the event in `Apply(string? languageCode)` after loading the resource dictionary and updating the flow direction.

### Main Navigation & Shell View Model

#### [MODIFY] [ShellViewModel.cs](file:///c:/Users/omarf/WWW/dev/src/Stackroot.App/ViewModels/ShellViewModel.cs)
- Subscribe to `LocalizationManager.LanguageChanged` in the constructor.
- Rebuild the `MainNavigationItems` and `BottomNavigationItems` lists dynamically when the language changes, calling `RaisePropertyChanged` to refresh the UI.
- Use `LocalizationManager.Get` for navigation titles.

### View Models (Dynamic Labels)

#### [MODIFY] [SiteRowViewModel.cs](file:///c:/Users/omarf/WWW/dev/src/Stackroot.App/ViewModels/SiteRowViewModel.cs)
- Use `LocalizationManager.Get` to return localized labels for `EnableDisableLabel` and `ToggleButtonLabel` ("Disable", "Enable", "Disabling…", "Enabling…").

#### [MODIFY] [SiteManageViewModel.cs](file:///c:/Users/omarf/WWW/dev/src/Stackroot.App/ViewModels/SiteManageViewModel.cs)
- Use `LocalizationManager.Get` to return localized labels for `EnableDisableLabel` and `ToggleButtonLabel`.

### Views and Pages (XAML Changes)

We will update the following main pages to replace hardcoded text with `DynamicResource Loc.X`:
- `DashboardPage.xaml`
- `SitesPage.xaml`
- `PhpPage.xaml`
- `NodePage.xaml`
- `ServicesPage.xaml`
- `ToolsPage.xaml`
- `DatabasesPage.xaml`
- `ProcessesPage.xaml`
- `ScheduledTasksPage.xaml`
- `LogsPage.xaml`
- `PerformancePage.xaml`
- `DownloadsPage.xaml`

We will also update the dialog views:
- `AddSiteDialog.xaml`
- `AddGlobalProcessDialog.xaml`
- `CreateDatabaseDialog.xaml`
- `CronTaskDialog.xaml`
- `CustomCommandsDialog.xaml`
- `DatabaseBackupsDialog.xaml`
- `DatabasesSettingsDialog.xaml`
- `DevSslPathsDialog.xaml`
- `EditSiteDialog.xaml`
- `LaravelInstallDialog.xaml`
- `MailpitSettingsDialog.xaml`
- `NginxHttpSettingsDialog.xaml`
- `NodeSettingsDialog.xaml`
- `PhpExtensionsDialog.xaml`
- `PhpMyAdminSettingsDialog.xaml`
- `PhpRedisAdminSettingsDialog.xaml`
- `PhpRuntimeSettingsDialog.xaml`
- `PhpVersionSettingsDialog.xaml`
- `PickDatabaseForRestoreDialog.xaml`
- `ServiceSettingsDialog.xaml`
- `SitesSettingsDialog.xaml`
- `TestDnsSettingsDialog.xaml`
- `WordPressInstallDialog.xaml`

## Verification Plan

### Automated Tests
- Build the project using `dotnet build` to ensure there are no XAML parsing or compilation errors.

### Manual Verification
- Launch the application and change the language to Arabic in settings.
- Verify that the layout flips to Right-to-Left (RTL).
- Verify that all pages, dialogs, and navigation sidebar titles translate to Arabic.
- Switch back to English and ensure it goes back to Left-to-Right (LTR) and English.
