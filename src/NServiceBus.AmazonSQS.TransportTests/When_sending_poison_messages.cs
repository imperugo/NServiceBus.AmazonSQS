﻿namespace NServiceBus.TransportTests
{
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using NUnit.Framework;
    using Transport;
    using AmazonSQS;
    using Settings;
    using AmazonSQS.AcceptanceTests;
    using Amazon.SQS.Model;
    using Configuration.AdvancedExtensibility;

    public class When_sending_poison_messages : NServiceBusTransportTest
    {
        [TestCase(TransportTransactionMode.None)]
        [TestCase(TransportTransactionMode.ReceiveOnly)]
        public async Task Should_move_to_error_queue(TransportTransactionMode transactionMode)
        {
            var onMessageCalled = false;
            var onErrorCalled = false;
            var cancellationTokenSource = new CancellationTokenSource();

            OnTestTimeout(() => cancellationTokenSource.Cancel());

            await StartPump(
                context =>
                {
                    onMessageCalled = true;
                    return Task.FromResult(0);
                },
                context =>
                {
                    onErrorCalled = true;
                    return Task.FromResult(ErrorHandleResult.Handled);
                }, transactionMode);

            await SendPoisonMessage(InputQueueName);

            await CheckErrorQueue(ErrorQueueName, cancellationTokenSource.Token);
            
            Assert.False(onErrorCalled, "Poison message should not invoke onError");
            Assert.False(onMessageCalled, "Poison message should not invoke onMessage");
        }

        string PoisonMessageBody = "this is a poison message that won't deserialize to valid json";

        async Task SendPoisonMessage(string inputQueueName)
        {
            var transportConfiguration = new TransportExtensions<SqsTransport>(new SettingsHolder());
            transportConfiguration = transportConfiguration.ConfigureSqsTransport(SetupFixture.SqsQueueNamePrefix);
            var connectionConfiguration = new ConnectionConfiguration(transportConfiguration.GetSettings());
            using (var sqsClient = AwsClientFactory.CreateSqsClient(connectionConfiguration))
            {
                var getQueueUrlResponse = await sqsClient.GetQueueUrlAsync(new GetQueueUrlRequest
                {
                    QueueName = QueueNameHelper.GetSqsQueueName(inputQueueName, connectionConfiguration)
                }).ConfigureAwait(false);

                await sqsClient.SendMessageAsync(new SendMessageRequest
                {
                    QueueUrl = getQueueUrlResponse.QueueUrl,
                    MessageBody = PoisonMessageBody
                }).ConfigureAwait(false);
            }
        }

        async Task CheckErrorQueue(string errorQueueName, CancellationToken cancellationToken)
        {
            var transportConfiguration = new TransportExtensions<SqsTransport>(new SettingsHolder());
            transportConfiguration = transportConfiguration.ConfigureSqsTransport(SetupFixture.SqsQueueNamePrefix);
            var connectionConfiguration = new ConnectionConfiguration(transportConfiguration.GetSettings());
            using (var sqsClient = AwsClientFactory.CreateSqsClient(connectionConfiguration))
            {
                var getQueueUrlResponse = await sqsClient.GetQueueUrlAsync(new GetQueueUrlRequest
                {
                    QueueName = QueueNameHelper.GetSqsQueueName(errorQueueName, connectionConfiguration)
                }, cancellationToken).ConfigureAwait(false);

                var messageReceived = false;
                ReceiveMessageResponse receiveMessageResponse = null;

                while (messageReceived == false && !cancellationToken.IsCancellationRequested)
                {
                    receiveMessageResponse = await sqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
                    {
                        QueueUrl = getQueueUrlResponse.QueueUrl,
                        WaitTimeSeconds = 20
                    }, cancellationToken).ConfigureAwait(false);

                    if (receiveMessageResponse.Messages.Any())
                    {
                        messageReceived = true;
                    }  
                }
                
                Assert.NotNull(receiveMessageResponse);
                Assert.AreEqual(1, receiveMessageResponse.Messages.Count);
                Assert.AreEqual(PoisonMessageBody, receiveMessageResponse.Messages.Single().Body);
            }
        }
    }
}