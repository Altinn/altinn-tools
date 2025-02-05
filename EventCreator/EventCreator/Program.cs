// See https://aka.ms/new-console-template for more information
using EventCreator.Clients;
using EventCreator.Configuration;
using Microsoft.Extensions.Configuration;


Console.WriteLine("Hello, World!");

var builder = new ConfigurationBuilder()
                .AddJsonFile($"appsettings.json", true, true);

var config = builder.Build();

QueueStorageSettings queueStorageSettings = new();
config.GetRequiredSection("QueueStorageSettings").Bind(queueStorageSettings);

EventsQueueClient eventsQueueClient = new EventsQueueClient(queueStorageSettings);

await eventsQueueClient.EnqueueRegistration("this is a test");