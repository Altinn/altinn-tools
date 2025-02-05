using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EventCreator.Configuration;

/// <summary>
/// Configuration object used to hold settings for the queue storage.
/// </summary>
public class QueueStorageSettings
{
    /// <summary>
    /// ConnectionString for the storage account
    /// </summary>
    public string ConnectionString { get; set; }

    /// <summary>
    /// Name of the queue to push incoming events to, before persisting to db.
    /// </summary>
    public string RegistrationQueueName { get; set; }
}
