using Azure.Storage.Queues;
using EventCreator.Configuration;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EventCreator.Clients;

/// <summary>
/// Implementation of the <see ref="IEventsQueueClient"/> using Azure Storage Queues.
/// </summary>
public class EventsQueueClient
{
    private readonly QueueStorageSettings _settings;

    private QueueClient _registrationQueueClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventsQueueClient"/> class.
    /// </summary>
    /// <param name="settings">The queue storage settings</param>
    public EventsQueueClient(QueueStorageSettings settings)
    {
        _settings = settings;
    }

    /// <inheritdoc/>
    public async Task EnqueueRegistration(string content)
    {
        QueueClient client = await GetRegistrationQueueClient();
        await client.SendMessageAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(content)));
    }


    private async Task<QueueClient> GetRegistrationQueueClient()
    {
        if (_registrationQueueClient == null)
        {
            _registrationQueueClient = new QueueClient(_settings.ConnectionString, _settings.RegistrationQueueName);
            await _registrationQueueClient.CreateIfNotExistsAsync();
        }

        return _registrationQueueClient;
    }
}
