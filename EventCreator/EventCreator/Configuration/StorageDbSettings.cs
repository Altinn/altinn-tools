namespace EventCreator.Configuration;

/// <summary>
/// Settings for Postgres database
/// </summary>
public class StorageDbSettings
{
    /// <summary>
    /// Connection string for the postgres db
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;
}
