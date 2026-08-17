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
            ProfileCombo.ItemsSource = _project.Structure.profiles.Select(profile =>
                new ProfileItem(profile)).ToArray();
            ProfileCombo.SelectedIndex = 0;
            GenerateStructureButton.IsEnabled = true;
            if (remember)
            {
                _settings.RememberProject(_project.ProjectRoot);
                _settings.Save();
            }
            await preflight();
        }
        catch (Exception exception)
        {
            _project = null;
            _profile = null;
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

    async void ProfileChanged(object? sender, SelectionChangedEventArgs e)
    {
        _profile = (ProfileCombo.SelectedItem as ProfileItem)?.Profile;
        if (_profile is not null && _project is not null)
        {
            SummaryText.Text = $"类型：{_profile.displayName}\n动作：{_profile.action}\n" +
                               $"平台：{_profile.target}\nBundle：" +
                               $"{_project.Structure.content.bundleRoots.Count} 个根\n" +
                               $"Hot：{_project.Structure.managedCode.hotAssemblies.Count} 个程序集\n" +
                               $"模块：{_project.Structure.modules.Count} 个";
            DevelopmentCheck.IsEnabled = _profile.supportsDevelopment;
            CleanCheck.IsEnabled = _profile.supportsCleanBuild;
            await preflight();
        }
    }

    async void RunPreflight(object? sender, RoutedEventArgs e) => await preflight();

    async Task preflight()
    {
        if (_project is null || _profile is null) return;
        try
        {
            _preflight = await BuildPreflight.RunAsync(_project, _profile,
                empty(UnityRootBox.Text));
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
                (EnvironmentCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "test",
                output, empty(VersionBox.Text), (long)(BuildNumberBox.Value ?? 0),
                CleanCheck.IsChecked == true, DevelopmentCheck.IsChecked == true,
                _project.Structure.modules.Where(value => !value.optional)
                    .Select(value => value.id));
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

    sealed record ProfileItem(MfBuildProfile Profile)
    {
        public override string ToString() => $"{Profile.displayName} · {Profile.target}";
    }
}
