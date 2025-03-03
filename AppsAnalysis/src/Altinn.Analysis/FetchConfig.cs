namespace Altinn.Analysis;

public sealed record FetchConfig(
    string Directory,
    string Username,
    string Password,
    int MaxParallelism,
    bool ClearDirectory,
    string AltinnUrl
);
