using System.IO.Compression;
using System.Net.Http.Headers;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using ImGuiNET;
using StArray.ModManager.Manager;

namespace AsyncInput.Mobile;

/// <summary>
/// Checks the public GitHub Release and replaces only AsyncInput.dll.
/// The replacement becomes active after the next game restart.
/// </summary>
internal sealed class GitHubUpdateService : IDisposable
{
    // These values are intentionally centralized so the repository can be
    // reviewed/changed before the first public push.
    private const string LogTag = "AsyncInput/Updater";
    private const string RepositoryOwner = "iidamie";
    private const string RepositoryName = "AsyncInput_Mobile";
    private const string ModId = "AsyncInput";
    private const long MaxPackageBytes = 32L * 1024 * 1024;
    private const long MaxFileBytes = 16L * 1024 * 1024;

    private readonly string _modDirectory;
    private readonly string _currentVersion;
    private readonly HttpClient _client;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _stateLock = new();

    private UpdateState _state;
    private string _status = "";
    private ReleaseInfo? _release;
    private string _notification = "";
    private DateTime _notificationUntilUtc;
    private bool _disposed;

    internal GitHubUpdateService(string modDirectory, string currentVersion)
    {
        _modDirectory = Path.GetFullPath(modDirectory);
        _currentVersion = currentVersion;
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("AsyncInput-Mobile", currentVersion));
        _client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    internal void StartAutomaticCheck() => StartCheck();

    internal void CheckNow() => StartCheck();

    internal void DownloadUpdate()
    {
        ReleaseInfo? release;
        lock (_stateLock)
        {
            if (_disposed || _state != UpdateState.Available || _release == null)
                return;
            release = _release;
            _state = UpdateState.Downloading;
            _status = $"正在下载 {release.Version}...";
        }

        _ = DownloadAndInstallAsync(release, _lifetime.Token);
    }

    internal void DrawGui()
    {
        UpdateSnapshot snapshot = GetSnapshot();
        if (snapshot.State == UpdateState.Idle)
            return;

        ImGui.Separator();
        ImGui.TextUnformatted("GitHub 更新");
        switch (snapshot.State)
        {
            case UpdateState.Checking:
            case UpdateState.Downloading:
                ImGui.TextDisabled(snapshot.Status);
                break;
            case UpdateState.Available:
                ImGui.TextColored(new Vector4(1f, 0.78f, 0.25f, 1f), snapshot.Status);
                if (ImGui.Button("下载并安装更新##async-input-update"))
                    DownloadUpdate();
                break;
            case UpdateState.ReadyToRestart:
                ImGui.TextColored(new Vector4(0.45f, 1f, 0.65f, 1f), snapshot.Status);
                break;
            case UpdateState.UpToDate:
                ImGui.TextDisabled(snapshot.Status);
                break;
            case UpdateState.Failed:
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), snapshot.Status);
                break;
        }

        if (snapshot.State is not (UpdateState.Checking or UpdateState.Downloading or UpdateState.ReadyToRestart)
            && ImGui.Button("检查 GitHub 更新##async-input-check-update"))
        {
            CheckNow();
        }
    }

    internal void DrawForegroundNotification()
    {
        string notification;
        lock (_stateLock)
        {
            if (string.IsNullOrEmpty(_notification))
                return;
            if (DateTime.UtcNow >= _notificationUntilUtc)
            {
                _notification = "";
                return;
            }
            notification = _notification;
        }

        Vector2 display = ImGui.GetIO().DisplaySize;
        if (display.X < 180f || display.Y < 100f)
            return;

        ImGui.SetNextWindowPos(new Vector2(display.X * 0.5f, 18f),
            ImGuiCond.Always, new Vector2(0.5f, 0f));
        ImGui.SetNextWindowBgAlpha(0.94f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoInputs;
        if (ImGui.Begin("##AsyncInputUpdateNotification", flags))
            ImGui.TextWrapped(notification);
        ImGui.End();
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _lifetime.Cancel();
        _client.Dispose();
        _lifetime.Dispose();
    }

    private void StartCheck()
    {
        lock (_stateLock)
        {
            if (_disposed || _state is UpdateState.Checking or UpdateState.Downloading)
                return;
            _state = UpdateState.Checking;
            _status = "正在检查 GitHub 更新...";
            _release = null;
            _notification = "";
        }

        _ = CheckLatestAsync(_lifetime.Token);
    }

    private async Task CheckLatestAsync(CancellationToken token)
    {
        try
        {
            string url = $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest";
            using HttpResponseMessage response = await _client.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync(token);
            using JsonDocument document = await JsonDocument.ParseAsync(
                stream, cancellationToken: token);

            ReleaseInfo release = ParseRelease(document.RootElement);
            bool updateAvailable = CompareVersions(release.Version, _currentVersion) > 0;
            lock (_stateLock)
            {
                if (_disposed)
                    return;
                _release = updateAvailable ? release : null;
                _state = updateAvailable ? UpdateState.Available : UpdateState.UpToDate;
                _status = updateAvailable
                    ? $"发现新版本：{release.Version}"
                    : $"已是最新版本（{_currentVersion}）";
                if (updateAvailable)
                {
                    _notification = $"AsyncInput 有新版本：{release.Version}";
                    _notificationUntilUtc = DateTime.UtcNow.AddSeconds(4);
                }
            }

            if (updateAvailable)
            {
                Logger.Warn(LogTag, $"Update available: {_currentVersion} -> {release.Version}");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            lock (_stateLock)
            {
                if (_disposed)
                    return;
                _state = UpdateState.Failed;
                _status = $"更新检查失败：{exception.Message}";
            }
            Logger.Warn(LogTag, $"Release check failed: {exception.Message}");
        }
    }

    private async Task DownloadAndInstallAsync(ReleaseInfo release, CancellationToken token)
    {
        string id = Guid.NewGuid().ToString("N");
        string archivePath = Path.Combine(_modDirectory, $".async-input-update-{id}.zip");
        string stagingDirectory = Path.Combine(_modDirectory, $".async-input-update-{id}");

        try
        {
            Directory.CreateDirectory(_modDirectory);
            using HttpResponseMessage response = await _client.GetAsync(
                release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxPackageBytes)
                throw new InvalidDataException("更新包过大");

            await using (Stream input = await response.Content.ReadAsStreamAsync(token))
            await using (FileStream output = new(
                archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                FileOptions.Asynchronous))
            {
                await CopyWithLimitAsync(input, output, MaxPackageBytes, token);
            }

            Directory.CreateDirectory(stagingDirectory);
            await ExtractPackageAsync(archivePath, stagingDirectory, token);
            string stagedDll = Path.Combine(stagingDirectory, $"{ModId}.dll");
            ValidateAssembly(stagedDll);
            InstallStagedFile(stagedDll);

            lock (_stateLock)
            {
                if (!_disposed)
                {
                    _state = UpdateState.ReadyToRestart;
                    _status = $"已更新到 {release.Version}，重启游戏后生效";
                    _notification = _status;
                    _notificationUntilUtc = DateTime.UtcNow.AddSeconds(4);
                }
            }
            Logger.Info(LogTag, $"Updated AsyncInput to {release.Version}; restart required");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            lock (_stateLock)
            {
                if (!_disposed)
                {
                    _state = UpdateState.Failed;
                    _status = $"更新失败：{exception.Message}";
                }
            }
            Logger.Error(LogTag, $"Update installation failed: {exception}");
        }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private static ReleaseInfo ParseRelease(JsonElement root)
    {
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(tag))
            throw new InvalidDataException("GitHub Release 没有 tag");

        string version = NormalizeVersionText(tag);
        string expectedName = $"{ModId}-{version}.zip";
        string? downloadUrl = null;
        if (root.TryGetProperty("assets", out JsonElement assets)
            && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out JsonElement name)
                    || !string.Equals(name.GetString(), expectedName, StringComparison.Ordinal))
                    continue;
                if (asset.TryGetProperty("browser_download_url", out JsonElement url))
                    downloadUrl = url.GetString();
                break;
            }
        }

        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out Uri? packageUri)
            || packageUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException($"GitHub Release 中没有 {expectedName}");
        }

        return new ReleaseInfo(tag, version, packageUri);
    }

    private static async Task ExtractPackageAsync(
        string archivePath, string stagingDirectory, CancellationToken token)
    {
        await using FileStream stream = new(
            archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read);
        ZipArchiveEntry? entry = archive.GetEntry($"{ModId}/{ModId}.dll");
        if (entry == null)
            throw new InvalidDataException($"更新包缺少 {ModId}/{ModId}.dll");
        if (entry.Length < 0 || entry.Length > MaxFileBytes)
            throw new InvalidDataException("更新 DLL 过大");

        string destination = Path.Combine(stagingDirectory, $"{ModId}.dll");
        await using Stream input = entry.Open();
        await using FileStream output = new(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous);
        await input.CopyToAsync(output, 81920, token);
    }

    private void InstallStagedFile(string stagedPath)
    {
        string targetPath = Path.Combine(_modDirectory, $"{ModId}.dll");
        string backupPath = Path.Combine(
            _modDirectory, $".async-input-backup-{Guid.NewGuid():N}.dll");
        bool hadOriginal = File.Exists(targetPath);
        try
        {
            if (hadOriginal)
                File.Copy(targetPath, backupPath, true);
            File.Move(stagedPath, targetPath, true);
        }
        catch
        {
            try
            {
                if (hadOriginal && File.Exists(backupPath))
                    File.Copy(backupPath, targetPath, true);
                else if (!hadOriginal && File.Exists(targetPath))
                    File.Delete(targetPath);
            }
            catch (Exception restoreException)
            {
                Logger.Error(LogTag, $"Could not restore AsyncInput.dll: {restoreException.Message}");
            }
            throw;
        }
        finally
        {
            TryDeleteFile(backupPath);
        }
    }

    private static void ValidateAssembly(string path)
    {
        if (!File.Exists(path))
            throw new InvalidDataException("更新 DLL 没有正确解压");
        AssemblyName? assembly = AssemblyName.GetAssemblyName(path);
        if (!string.Equals(assembly.Name, ModId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新 DLL 不是 AsyncInput");
    }

    private static async Task CopyWithLimitAsync(
        Stream input, Stream output, long limit, CancellationToken token)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            total += count;
            if (total > limit)
                throw new InvalidDataException("更新包过大");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
    }

    private static int CompareVersions(string left, string right)
    {
        int[] leftParts = ExtractNumericParts(left);
        int[] rightParts = ExtractNumericParts(right);
        int count = Math.Max(leftParts.Length, rightParts.Length);
        for (int index = 0; index < count; index++)
        {
            int leftPart = index < leftParts.Length ? leftParts[index] : 0;
            int rightPart = index < rightParts.Length ? rightParts[index] : 0;
            if (leftPart != rightPart)
                return leftPart.CompareTo(rightPart);
        }
        return string.Compare(
            NormalizeVersionText(left), NormalizeVersionText(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private static int[] ExtractNumericParts(string value)
    {
        List<int> parts = new();
        int current = 0;
        bool reading = false;
        foreach (char character in value)
        {
            if (character is >= '0' and <= '9')
            {
                reading = true;
                current = Math.Min(999999, current * 10 + character - '0');
                continue;
            }
            if (reading)
            {
                parts.Add(current);
                current = 0;
                reading = false;
            }
        }
        if (reading)
            parts.Add(current);
        return parts.ToArray();
    }

    private static string NormalizeVersionText(string value)
    {
        string normalized = value.Trim();
        while (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[1..];
        return normalized;
    }

    private UpdateSnapshot GetSnapshot()
    {
        lock (_stateLock)
            return new UpdateSnapshot(_state, _status);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch { }
    }

    private sealed record ReleaseInfo(string Tag, string Version, Uri DownloadUrl);
    private readonly record struct UpdateSnapshot(UpdateState State, string Status);

    private enum UpdateState
    {
        Idle,
        Checking,
        UpToDate,
        Available,
        Downloading,
        ReadyToRestart,
        Failed,
    }
}
