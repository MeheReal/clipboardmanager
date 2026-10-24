namespace ClipboardManager.Services;

public static class UpdateConfiguration
{
    // Set these to the owner and repository name of your public GitHub repository.
    // Repository: https://github.com/MeheReal/clipboardmanager
    public const string GitHubOwner = "MeheReal";
    public const string GitHubRepository = "clipboardmanager";

    public const string ReleaseAssetName = "ClipboardManager.exe";

    public static bool IsConfigured =>
        !GitHubOwner.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase) &&
        !GitHubRepository.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(GitHubOwner) &&
        !string.IsNullOrWhiteSpace(GitHubRepository);

    public static Uri LatestReleaseApiUri
    {
        get
        {
            if (!IsConfigured)
            {
                throw new InvalidOperationException(
                    "Set GitHubOwner and GitHubRepository in Services/UpdateConfiguration.cs before using Check for updates.");
            }

            var owner = Uri.EscapeDataString(GitHubOwner);
            var repository = Uri.EscapeDataString(GitHubRepository);
            return new Uri($"https://api.github.com/repos/{owner}/{repository}/releases/latest");
        }
    }
}