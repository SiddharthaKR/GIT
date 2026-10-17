namespace PrivateGit.Services;
public sealed record ChangeView(string Repository, string? Before, string After, ChangeSet Changes);
