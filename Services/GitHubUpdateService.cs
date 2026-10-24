using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClipboardManager.Services;

public sealed class GitHubUpdateService
{
    private const long MaximumDownloadBytes = 512L * 1024 * 1024;
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public Version CurrentVersion => NormalizeVersion(typeof(App).Assembly.GetName().Version ?? new Version(1, 0, 0, 0));

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await HttpClient.GetAsync(
            UpdateConfiguration.LatestReleaseApiUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"GitHub could not find a published release for {UpdateConfiguration.GitHubOwner}/{UpdateConfiguration.GitHubRepository}. " +
                "Make sure the repository is public and has a published release.");
        }

        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean())
            throw new InvalidDataException("GitHub returned a draft release. Publish it before checking for updates.");

        if (root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean())
            throw new InvalidDataException("The latest GitHub release is marked as a prerelease.");

        var tagName = ReadRequiredString(root, "tag_name");
        var latestVersion = ParseReleaseVersion(tagName);
        var currentVersion = CurrentVersion;
        if (latestVersion.CompareTo(currentVersion) <= 0)
            return new UpdateCheckResult(currentVersion, null);

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The GitHub release does not contain a release asset list.");

        JsonElement? matchingAsset = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out var name) &&
                string.Equals(name.GetString(), UpdateConfiguration.ReleaseAssetName, StringComparison.OrdinalIgnoreCase))
            {
                matchingAsset = asset;
                break;
            }
        }

        if (matchingAsset is null)
        {
            throw new InvalidDataException(
                $"Release {tagName} does not include the required asset '{UpdateConfiguration.ReleaseAssetName}'.");
        }

        var releaseAsset = matchingAsset.Value;
        var downloadUrl = ReadRequiredString(releaseAsset, "browser_download_url");
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var downloadUri) ||
            downloadUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(downloadUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The release asset does not have a valid HTTPS GitHub download URL.");
        }

        var size = releaseAsset.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : 0;
        if (size <= 0 || size > MaximumDownloadBytes)
            throw new InvalidDataException("The release executable has an invalid or unexpectedly large file size.");

        var digest = ReadRequiredString(releaseAsset, "digest");
        if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("GitHub did not provide a SHA-256 digest for this release asset.");
        }

        var sha256 = digest["sha256:".Length..];
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("GitHub returned an invalid SHA-256 digest for this release asset.");

        return new UpdateCheckResult(
            currentVersion,
            new UpdateRelease(tagName, latestVersion, downloadUri, sha256.ToUpperInvariant(), size));
    }

    public async Task<string> DownloadAndVerifyAsync(
        UpdateRelease release,
        CancellationToken cancellationToken = default)
    {
        var updateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClipboardManager",
            "Updates");
        Directory.CreateDirectory(updateDirectory);

        var safeVersion = string.Concat(release.TagName.Select(character =>
            char.IsLetterOrDigit(character) || character is '.' or '-' ? character : '_'));
        var downloadPath = Path.Combine(updateDirectory, $"ClipboardManager-{safeVersion}-{Guid.NewGuid():N}.exe");
        var partialPath = downloadPath + ".partial";

        try
        {
            using var response = await HttpClient.GetAsync(
                release.DownloadUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                long totalBytes = 0;
                int bytesRead;
                while ((bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) != 0)
                {
                    totalBytes += bytesRead;
                    if (totalBytes > release.AssetSize || totalBytes > MaximumDownloadBytes)
                        throw new InvalidDataException("The downloaded update is larger than the release metadata allows.");

                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                }

                await destination.FlushAsync(cancellationToken);
                if (totalBytes != release.AssetSize)
                    throw new InvalidDataException("The downloaded update size does not match the GitHub release metadata.");
            }

            await using (var verifyStream = File.OpenRead(partialPath))
            {
                var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(verifyStream, cancellationToken));
                if (!string.Equals(actualHash, release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The downloaded update failed its SHA-256 integrity check.");
            }

            File.Move(partialPath, downloadPath);
            return downloadPath;
        }
        catch
        {
            TryDelete(partialPath);
            TryDelete(downloadPath);
            throw;
        }
    }

    public void LaunchInstaller(string downloadedExecutable, string currentExecutable, int processId, string expectedSha256)
    {
        var sourcePath = Path.GetFullPath(downloadedExecutable);
        var targetPath = Path.GetFullPath(currentExecutable);

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The verified update download could not be found.", sourcePath);
        if (!Path.GetFileName(targetPath).Equals("ClipboardManager.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Self-update is supported when running the published single-file ClipboardManager.exe. " +
                "Install or launch that executable and try again.");
        }

        var targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("The application executable directory could not be determined.");
        if (!Directory.Exists(targetDirectory))
            throw new DirectoryNotFoundException("The application executable directory no longer exists.");

        var script = """
            $ErrorActionPreference = 'Stop'
            $processIdToWait = __PROCESS_ID__
            $sourcePath = __SOURCE_PATH__
            $targetPath = __TARGET_PATH__
            $expectedSha256 = __EXPECTED_SHA256__
            $stagedPath = $targetPath + '.update-new'
            $backupPath = $targetPath + '.update-backup'

            try {
                $deadline = (Get-Date).AddMinutes(2)
                while ((Get-Process -Id $processIdToWait -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
                    Start-Sleep -Milliseconds 250
                }
                if (Get-Process -Id $processIdToWait -ErrorAction SilentlyContinue) {
                    throw 'Clipboard Manager did not close in time.'
                }

                if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
                    throw 'The downloaded update file is missing.'
                }

                if (Test-Path -LiteralPath $stagedPath) {
                    Remove-Item -LiteralPath $stagedPath -Force
                }
                Copy-Item -LiteralPath $sourcePath -Destination $stagedPath -Force

                $stagedHash = (Get-FileHash -LiteralPath $stagedPath -Algorithm SHA256).Hash
                if ($stagedHash -ne $expectedSha256) {
                    throw 'The staged update failed its SHA-256 integrity check.'
                }

                if (Test-Path -LiteralPath $targetPath -PathType Leaf) {
                    if (Test-Path -LiteralPath $backupPath) {
                        Remove-Item -LiteralPath $backupPath -Force
                    }
                    [System.IO.File]::Replace($stagedPath, $targetPath, $backupPath, $true)
                } else {
                    [System.IO.File]::Move($stagedPath, $targetPath)
                }

                Start-Process -FilePath $targetPath -WorkingDirectory (Split-Path -Parent $targetPath)
                if (Test-Path -LiteralPath $backupPath) {
                    Remove-Item -LiteralPath $backupPath -Force
                }
                Remove-Item -LiteralPath $sourcePath -Force -ErrorAction SilentlyContinue
            } catch {
                if (Test-Path -LiteralPath $backupPath) {
                    try {
                        if (Test-Path -LiteralPath $targetPath) {
                            $failedPath = $targetPath + '.update-failed'
                            [System.IO.File]::Replace($backupPath, $targetPath, $failedPath, $true)
                            Remove-Item -LiteralPath $failedPath -Force -ErrorAction SilentlyContinue
                        } else {
                            [System.IO.File]::Move($backupPath, $targetPath)
                        }
                    } catch {}
                }

                $message = 'The update could not be installed.' + [Environment]::NewLine + [Environment]::NewLine + $_.Exception.Message
                try {
                    Add-Type -AssemblyName System.Windows.Forms
                    [System.Windows.Forms.MessageBox]::Show(
                        $message,
                        'Clipboard Manager update failed',
                        [System.Windows.Forms.MessageBoxButtons]::OK,
                        [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
                } catch {}

                if (Test-Path -LiteralPath $targetPath -PathType Leaf) {
                    Start-Process -FilePath $targetPath -WorkingDirectory (Split-Path -Parent $targetPath) -ErrorAction SilentlyContinue
                }
            }
            """;

        script = script
            .Replace("__PROCESS_ID__", processId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__SOURCE_PATH__", ToPowerShellLiteral(sourcePath), StringComparison.Ordinal)
            .Replace("__TARGET_PATH__", ToPowerShellLiteral(targetPath), StringComparison.Ordinal)
            .Replace("__EXPECTED_SHA256__", ToPowerShellLiteral(expectedSha256.ToUpperInvariant()), StringComparison.Ordinal);

        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var powershellPath = Path.Combine(windowsDirectory, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershellPath))
            throw new FileNotFoundException("Windows PowerShell could not be found to install the update.", powershellPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = powershellPath,
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encodedCommand}",
            WorkingDirectory = targetDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using var installer = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The update installer process could not be started.");
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(3)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ClipboardManager", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static Version ParseReleaseVersion(string tagName)
    {
        var versionText = tagName.Trim();
        if (versionText.StartsWith('v') || versionText.StartsWith('V'))
            versionText = versionText[1..];

        var suffixIndex = versionText.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
            versionText = versionText[..suffixIndex];

        if (!Version.TryParse(versionText, out var version) || version.Build < 0)
            throw new InvalidDataException($"Release tag '{tagName}' is not a supported version. Use tags such as v1.2.3.");

        return NormalizeVersion(version);
    }

    private static Version NormalizeVersion(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"The GitHub release response is missing '{propertyName}'.");
        }

        return value.GetString()!;
    }

    private static string ToPowerShellLiteral(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Could not remove temporary updater file '{path}'.", ex);
        }
    }
}

public sealed record UpdateCheckResult(Version CurrentVersion, UpdateRelease? Release);

public sealed record UpdateRelease(
    string TagName,
    Version Version,
    Uri DownloadUri,
    string Sha256,
    long AssetSize);