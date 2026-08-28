using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MyFramework.BuildStudio;
using MyFramework.BuildStudio.Core;

namespace MyFramework.BuildStudio.App;

public sealed partial class MainWindow : Window
{
    readonly BuildHistoryRepository _history = new();
    BuildStudioSettings _settings = BuildStudioSettings.Load();
    ProjectDocument? _project;
    MfBuildProfile? _profile;
    PreflightReport? _preflight;
    CancellationTokenSource? _buildCancellation;
    string? _selectedProjectRoot;
    IReadOnlyList<BuildHistoryItem> _historyItems = [];
    IReadOnlyList<MfBuildProfile> _visibleProfiles = [];
    bool _updatingSelection;

    public MainWindow()
    {
        InitializeComponent();
        UnityRootBox.Text = _settings.unityHubRoot;
        DefaultOutputBox.Text = _settings.defaultOutputRoot;
        RetainBuildsBox.Value = _settings.retainBuilds;
        refreshHistory();
        string? recent = _settings.recentProjects.FirstOrDefault(Directory.Exists);
        if (recent is not null) _ = loadProject(recent, false);
    }

    async void OpenProject(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "选择 Unity 项目", AllowMultiple = false });
        if (folders.Count == 0) return;
        await loadProject(folders[0].Path.LocalPath, true);
    }

    async Task loadProject(string root, bool remember)
    {
        _selectedProjectRoot = Path.GetFullPath(root);
        try
        {
            _project = ProjectStructureStore.LoadProject(_selectedProjectRoot);
            HeaderProjectName.Text = _project.Structure.project.displayName;
            HeaderProjectMeta.Text = $"{_project.Structure.project.kind} · " +
                                     $"Unity {_project.Structure.unity.version} · " +
                                     $"{_project.Structure.structureHash[..12]}";
            SideProjectName.Text = _project.Structure.project.displayName;
            SideProjectMeta.Text = _project.ProjectRoot;
            _visibleProfiles = BuildProfileCatalog.VisibleProfiles(
                _project.Structure.profiles);
            configureProfileSelectors();
            GenerateStructureButton.IsEnabled = true;
            if (remember)
            {
                _settings.RememberProject(_project.ProjectRoot);
                _settings.Save();
            }
            await profileUpdated(true);
        }
        catch (Exception exception)
        {
            _project = null;
            _profile = null;
            _visibleProfiles = [];
            HeaderProjectName.Text = "项目尚未就绪";
            HeaderProjectMeta.Text = exception.Message;
            SideProjectName.Text = Path.GetFileName(_selectedProjectRoot);
            SideProjectMeta.Text = _selectedProjectRoot;
            GenerateStructureButton.IsEnabled = Directory.Exists(Path.Combine(
                _selectedProjectRoot, "Assets"));
            StartButton.IsEnabled = false;
            appendLog("[error] " + exception.Message);
        }
    }

    async void GenerateStructure(object? sender, RoutedEventArgs e)
    {
        if (_selectedProjectRoot is null) return;
        GenerateStructureButton.IsEnabled = false;
        try
        {
            StageText.Text = "正在生成结构文件";
            await StructureGeneratorRunner.GenerateAsync(_selectedProjectRoot,
                empty(UnityRootBox.Text));
            await loadProject(_selectedProjectRoot, true);
            appendLog("[success] MyFrameworkProject.json 已生成");
        }
        catch (Exception exception) { appendLog("[error] " + exception.Message); }
        finally { GenerateStructureButton.IsEnabled = true; }
    }

    async void ActionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelection) return;
        _updatingSelection = true;
        string? previousTarget = (PlatformCombo.SelectedItem as BuildPlatformOption)?.Target;
        BuildActionOption? action = ActionCombo.SelectedItem as BuildActionOption;
        IReadOnlyList<BuildPlatformOption> platforms = action is null
            ? [] : BuildProfileCatalog.Platforms(_visibleProfiles, action.Id);
        PlatformCombo.ItemsSource = platforms;
        int index = previousTarget is null ? -1 : platforms.ToList().FindIndex(value =>
            value.Target == previousTarget);
        PlatformCombo.SelectedIndex = index >= 0 ? index : platforms.Count > 0 ? 0 : -1;
        selectProfile();
        _updatingSelection = false;
        await profileUpdated(true);
    }

    async void PlatformChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelection) return;
        selectProfile();
        await profileUpdated(true);
    }

    async void EnvironmentChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_updatingSelection) await preflight();
    }

    async void UploadChanged(object? sender, RoutedEventArgs e)
    {
        if (_updatingSelection) return;
        refreshSummary();
        await preflight();
    }

    async void RunPreflight(object? sender, RoutedEventArgs e) => await preflight();

    async Task preflight()
    {
        if (_project is null || _profile is null) return;
        try
        {
            _preflight = await BuildPreflight.RunAsync(_project, _profile,
                empty(UnityRootBox.Text), buildArguments());
            PreflightList.ItemsSource = _preflight.Items.Select(item =>
                $"{(item.Ok ? "✓" : item.Severity == PreflightSeverity.Error ? "✕" : "!")} " +
                $"{item.Label}  {item.Detail}").ToArray();
            StartButton.IsEnabled = _preflight.CanBuild && _buildCancellation is null;
            StageText.Text = _preflight.CanBuild ? "预检通过" : "预检失败";
        }
        catch (Exception exception)
        {
            StartButton.IsEnabled = false;
            PreflightList.ItemsSource = new[] { "✕ " + exception.Message };
        }
    }

    async void StartBuild(object? sender, RoutedEventArgs e)
    {
        if (_project is null || _profile is null || _preflight?.Unity is null) return;
        await preflight();
        if (_preflight?.CanBuild != true) return;
        _buildCancellation = new CancellationTokenSource();
        StartButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        LogBox.Text = string.Empty;
        BuildProgressBar.IsIndeterminate = true;
        string finalStatus = string.Empty;
        try
        {
            string? output = empty(OutputBox.Text) ?? empty(DefaultOutputBox.Text);
            MfBuildJob job = BuildJobFactory.Create(_project, _profile,
                EnvironmentCombo.SelectedItem?.ToString() ?? "test",
                output, empty(VersionBox.Text), (long)(BuildNumberBox.Value ?? 0),
                CleanCheck.IsChecked == true, DevelopmentCheck.IsChecked == true,
                _project.Structure.modules.Where(value => !value.optional)
                    .Select(value => value.id), buildArguments());
            Progress<BuildProgress> progress = new(value =>
            {
                StageText.Text = value.Stage + " · " + value.State;
                if (value.Progress >= 0)
                {
                    BuildProgressBar.IsIndeterminate = false;
                    BuildProgressBar.Value = value.Progress * 100;
                }
                appendLog($"[{value.Stage}] {value.Message}");
            });
            MfBuildReceipt receipt = await new UnityBuildRunner().RunAsync(new BuildRunRequest
            {
                Project = _project,
                Unity = _preflight.Unity,
                Job = job,
            }, progress, _buildCancellation.Token);
            _history.Add(receipt);
            finalStatus = receipt.ok ? "构建成功" : receipt.status == "canceled" ?
                "已取消" : "构建失败";
            StageText.Text = finalStatus;
            appendLog(receipt.ok ? "[success] 构建完成" : "[failed] " + receipt.error);
            refreshHistory();
        }
        catch (Exception exception)
        {
            finalStatus = "构建失败";
            appendLog("[error] " + exception.Message);
        }
        finally
        {
            _buildCancellation.Dispose();
            _buildCancellation = null;
            CancelButton.IsEnabled = false;
            BuildProgressBar.IsIndeterminate = false;
            await preflight();
            if (!string.IsNullOrEmpty(finalStatus)) StageText.Text = finalStatus;
        }
    }

    void CancelBuild(object? sender, RoutedEventArgs e) => _buildCancellation?.Cancel();

    async void CheckBaseRequirement(object? sender, RoutedEventArgs e)
    {
        if (_project is null || _profile is null) return;
        await preflight();
        if (_preflight?.Unity is null) return;
        CheckBaseButton.IsEnabled = false;
        BaseRequirementText.Text = "正在启动 Unity 分析改动…";
        try
        {
            Progress<BuildProgress> progress = new(value =>
            {
                StageText.Text = value.Stage + " · " + value.State;
                appendLog($"[{value.Stage}] {value.Message}");
            });
            string environment = EnvironmentCombo.SelectedItem?.ToString() ?? "test";
            BaseRequirementReport result = await BaseRequirementRunner.RunAsync(_project,
                _preflight.Unity, _profile.target, environment, progress);
            string baseline = result.BaselineFound
                ? $"基准：{result.BaselineEnvironment} / {result.BaselineTarget} / " +
                  $"Base {result.BaselineBaseId}。\n"
                : $"基准：{environment} / {_profile.target} 尚无成功 Base 快照。\n";
            BaseRequirementText.Text = (result.RequiresBasePackage ? "需要新 Base。" :
                "不需要新 Base。") + $" 共比较 {result.ChangeCount} 项差异。\n" +
                baseline + result.Recommendation + "\n报告：" + result.ReportPath;
        }
        catch (Exception exception)
        {
            BaseRequirementText.Text = "检测失败：" + exception.Message;
            appendLog("[base-check] " + exception.Message);
        }
        finally
        {
            CheckBaseButton.IsEnabled = _project is not null &&
                                        BaseRequirementRunner.IsSupported(_project);
        }
    }
    void RefreshHistory(object? sender, RoutedEventArgs e) => refreshHistory();

    void refreshHistory()
    {
        _historyItems = _history.List(_settings.retainBuilds);
        HistoryList.ItemsSource = _historyItems.Select(item =>
            $"{item.StartedAtUtc}   {item.Status.ToUpperInvariant(),-9}   " +
            $"{item.ProjectId} / {item.ProfileId} / {item.Target}   " +
            $"{TimeSpan.FromMilliseconds(item.DurationMs):g}\n{item.JobId}   {item.OutputRoot}").ToArray();
    }

    void OpenHistoryOutput(object? sender, RoutedEventArgs e)
    {
        BuildHistoryItem? item = selectedHistory();
        if (item is null) return;
        MfBuildReceipt receipt = BuildStudioJson.Deserialize<MfBuildReceipt>(item.ReceiptJson);
        string? path = Directory.Exists(item.OutputRoot) ? item.OutputRoot :
            receipt.values.GetValueOrDefault("jobDirectory");
        openPath(path, "产物目录不存在");
    }

    void OpenHistoryLog(object? sender, RoutedEventArgs e)
    {
        BuildHistoryItem? item = selectedHistory();
        if (item is null) return;
        MfBuildReceipt receipt = BuildStudioJson.Deserialize<MfBuildReceipt>(item.ReceiptJson);
        receipt.values.TryGetValue("unityLog", out string? path);
        openPath(path, "Unity 日志不存在");
    }

    BuildHistoryItem? selectedHistory()
    {
        int index = HistoryList.SelectedIndex;
        if (index >= 0 && index < _historyItems.Count) return _historyItems[index];
        appendLog("[history] 请先选择一条构建记录");
        return null;
    }

    void openPath(string? path, string missingMessage)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !File.Exists(path) && !Directory.Exists(path))
        {
            appendLog("[history] " + missingMessage);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception) { appendLog("[history] " + exception.Message); }
    }

    void SaveSettings(object? sender, RoutedEventArgs e)
    {
        _settings.unityHubRoot = UnityRootBox.Text?.Trim() ?? string.Empty;
        _settings.defaultOutputRoot = DefaultOutputBox.Text?.Trim() ?? string.Empty;
        _settings.retainBuilds = (int)(RetainBuildsBox.Value ?? 100);
        _settings.Save();
        appendLog("[settings] 本机设置已保存");
    }

    void ShowBuild(object? sender, RoutedEventArgs e) => showPage(BuildPage);
    void ShowHistory(object? sender, RoutedEventArgs e) { refreshHistory(); showPage(HistoryPage); }
    void ShowSettings(object? sender, RoutedEventArgs e) => showPage(SettingsPage);

    void showPage(Control visible)
    {
        BuildPage.IsVisible = ReferenceEquals(visible, BuildPage);
        HistoryPage.IsVisible = ReferenceEquals(visible, HistoryPage);
        SettingsPage.IsVisible = ReferenceEquals(visible, SettingsPage);
    }

    void appendLog(string line)
    {
        Dispatcher.UIThread.Post(() =>
        {
            LogBox.Text = (LogBox.Text + line + Environment.NewLine);
            LogBox.CaretIndex = LogBox.Text.Length;
        });
    }

    static string? empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    void configureProfileSelectors()
    {
        _updatingSelection = true;
        IReadOnlyList<BuildActionOption> actions = BuildProfileCatalog.Actions(_visibleProfiles);
        ActionCombo.ItemsSource = actions;
        ActionCombo.SelectedIndex = actions.Count > 0 ? 0 : -1;
        IReadOnlyList<BuildPlatformOption> platforms = actions.Count == 0 ? [] :
            BuildProfileCatalog.Platforms(_visibleProfiles, actions[0].Id);
        PlatformCombo.ItemsSource = platforms;
        PlatformCombo.SelectedIndex = platforms.Count > 0 ? 0 : -1;
        selectProfile();
        _updatingSelection = false;
    }

    void selectProfile()
    {
        BuildActionOption? action = ActionCombo.SelectedItem as BuildActionOption;
        BuildPlatformOption? platform = PlatformCombo.SelectedItem as BuildPlatformOption;
        _profile = action is null || platform is null ? null :
            BuildProfileCatalog.Find(_visibleProfiles, action.Id, platform.Target);
    }

    async Task profileUpdated(bool resetUpload)
    {
        if (_profile is null || _project is null) return;
        _updatingSelection = true;
        string? previousEnvironment = EnvironmentCombo.SelectedItem?.ToString();
        IReadOnlyList<string> environments = _profile.allowedEnvironments.Count > 0
            ? _profile.allowedEnvironments : ["test", "prod"];
        EnvironmentCombo.ItemsSource = environments;
        int environmentIndex = previousEnvironment is null ? -1 : environments.ToList()
            .FindIndex(value => value == previousEnvironment);
        if (environmentIndex < 0) environmentIndex = environments.ToList().FindIndex(value =>
            value == "test");
        EnvironmentCombo.SelectedIndex = environmentIndex >= 0 ? environmentIndex : 0;
        UploadCheck.IsEnabled = BuildProfileCatalog.SupportsUpload(_profile);
        if (resetUpload)
            UploadCheck.IsChecked = BuildProfileCatalog.DefaultUpload(_profile);
        if (!UploadCheck.IsEnabled) UploadCheck.IsChecked = false;
        DevelopmentCheck.IsEnabled = _profile.supportsDevelopment;
        CleanCheck.IsEnabled = _profile.supportsCleanBuild;
        CheckBaseButton.IsEnabled = BaseRequirementRunner.IsSupported(_project);
        BaseRequirementText.Text = CheckBaseButton.IsEnabled
            ? "点击检测后，Unity 会根据当前 Git 改动判断是否必须重新打 Base。"
            : "当前项目未提供 Base 必要性检测器。";
        _updatingSelection = false;
        refreshSummary();
        refreshConfiguration();
        await preflight();
    }

    void refreshSummary()
    {
        if (_profile is null || _project is null) return;
        string upload = !BuildProfileCatalog.SupportsUpload(_profile) ? "不适用" :
            UploadCheck.IsChecked == true ? "是，完成后发布 Latest" : "否，只保留本地产物";
        string caveat = _profile.action == "integrated" && UploadCheck.IsChecked != true
            ? "\n提示：新 Base 未上传首个 Release 时，只适合检查产物，联网启动会找不到对应热更新。"
            : string.Empty;
        SummaryText.Text = $"动作：{ActionCombo.SelectedItem}\n平台：{PlatformCombo.SelectedItem}\n" +
                           $"环境：{EnvironmentCombo.SelectedItem}\n上传：{upload}\n" +
                           $"说明：{_profile.description}\n" +
                           $"产物：{string.Join("、", _profile.outputKinds)}\nBundle：" +
                           $"{_project.Structure.content.bundleRoots.Count} 个根\n" +
                           $"Hot：{_project.Structure.managedCode.hotAssemblies.Count} 个程序集" +
                           caveat;
    }

    void refreshConfiguration()
    {
        if (_profile is null || _project is null) return;
        ProjectConfigurationSnapshot snapshot = ProjectConfigurationReader.Read(_project,
            _profile);
        if (string.IsNullOrEmpty(snapshot.FilePath))
        {
            ConfigurationFileText.Text = "该动作不需要热更新或服务器配置。";
            ConfigurationList.ItemsSource = Array.Empty<string>();
            return;
        }
        ConfigurationFileText.Text = snapshot.Error is not null
            ? "配置读取异常：" + snapshot.Error
            : snapshot.Exists ? "来源：" + snapshot.FilePath : "配置文件不存在：" + snapshot.FilePath;
        ConfigurationList.ItemsSource = ProjectConfigurationReader.Describe(_profile, snapshot)
            .Select(item => $"{(item.Configured ? "✓" : "○")} {item.Label}  {item.Detail}")
            .ToArray();
    }

    IReadOnlyDictionary<string, string> buildArguments() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["upload"] = UploadCheck.IsEnabled && UploadCheck.IsChecked == true
                ? "true" : "false",
            ["environment"] = EnvironmentCombo.SelectedItem?.ToString() ?? "test",
        };
}
