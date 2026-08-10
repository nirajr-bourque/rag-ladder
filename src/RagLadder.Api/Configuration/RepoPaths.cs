namespace RagLadder.Api.Configuration;

/// <summary>
/// Configured paths (models, corpus, config, data) are written relative to the repository root so
/// they read naturally, but the working directory depends on how the app was launched. This
/// resolves them against the directory holding RagLadder.sln, so `dotnet run` works from anywhere.
/// </summary>
public static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "RagLadder.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }
        return Directory.GetCurrentDirectory();
    }

    public static string Resolve(string path) =>
        string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(Root, path));

    public static void ResolveAll(RagLadderOptions options)
    {
        options.Storage.DataDirectory = Resolve(options.Storage.DataDirectory);
        options.Storage.CorpusDirectory = Resolve(options.Storage.CorpusDirectory);
        options.Storage.RecordingsDirectory = Resolve(options.Storage.RecordingsDirectory);
        options.Embedding.ModelPath = Resolve(options.Embedding.ModelPath);
        options.Embedding.VocabPath = Resolve(options.Embedding.VocabPath);
        options.Rerank.ModelPath = Resolve(options.Rerank.ModelPath);
        options.Rerank.VocabPath = Resolve(options.Rerank.VocabPath);
        options.Domain.OntologyPath = Resolve(options.Domain.OntologyPath);
        options.Domain.DiminutivesPath = Resolve(options.Domain.DiminutivesPath);
    }
}
