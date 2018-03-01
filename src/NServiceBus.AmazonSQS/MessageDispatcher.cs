﻿namespace NServiceBus.Transports.SQS
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Amazon.S3;
    using Amazon.S3.Model;
    using Amazon.SQS;
    using Amazon.SQS.Model;
    using AmazonSQS;
    using DelayedDelivery;
    using Extensibility;
    using Logging;
    using Newtonsoft.Json;
    using Transport;

    class MessageDispatcher : IDispatchMessages
    {
        public MessageDispatcher(TransportConfiguration configuration, IAmazonS3 s3Client, IAmazonSQS sqsClient, QueueUrlCache queueUrlCache)
        {
            this.configuration = configuration;
            this.s3Client = s3Client;
            this.sqsClient = sqsClient;
            this.queueUrlCache = queueUrlCache;
        }

        public async Task Dispatch(TransportOperations outgoingMessages, TransportTransaction transaction, ContextBag context)
        {
            try
            {
                var operations = outgoingMessages.UnicastTransportOperations;
                var tasks = new Task[operations.Count];
                for (var i = 0; i < operations.Count; i++)
                {
                    tasks[i] = Dispatch(operations[i]);
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Logger.Error("Exception from Send.", e);
                throw;
            }
        }

        async Task Dispatch(UnicastTransportOperation transportOperation)
        {
            var delayWithConstraint = transportOperation.DeliveryConstraints.OfType<DelayDeliveryWith>().SingleOrDefault();
            var deliverAtConstraint = transportOperation.DeliveryConstraints.OfType<DoNotDeliverBefore>().SingleOrDefault();

            var delayDeliveryBy = TimeSpan.Zero;

            if (delayWithConstraint == null)
            {
                if (deliverAtConstraint != null)
                {
                    delayDeliveryBy = deliverAtConstraint.At - DateTime.UtcNow;
                }
            }
            else
            {
                delayDeliveryBy = delayWithConstraint.Delay;
            }

            if (!configuration.IsDelayedDeliveryEnabled && delayDeliveryBy > awsMaximumQueueDelayTime)
            {
                throw new NotSupportedException($"To send messages with a delay time greater than '{awsMaximumQueueDelayTime}', call '.UseTransport<SqsTransport>().UnrestrictedDelayedDelivery()'.");
            }

            var sqsTransportMessage = new TransportMessage(transportOperation.Message, transportOperation.DeliveryConstraints);
            var serializedMessage = JsonConvert.SerializeObject(sqsTransportMessage);

            if (serializedMessage.Length > 256 * 1024)
            {
                if (string.IsNullOrEmpty(configuration.S3BucketForLargeMessages))
                {
                    throw new Exception("Cannot send large message because no S3 bucket was configured. Add an S3 bucket name to your configuration.");
                }

                var key = $"{configuration.S3KeyPrefix}/{transportOperation.Message.MessageId}";

                using (var bodyStream = new MemoryStream(transportOperation.Message.Body))
                {
                    await s3Client.PutObjectAsync(new PutObjectRequest
                    {
                        BucketName = configuration.S3BucketForLargeMessages,
                        InputStream = bodyStream,
                        Key = key
                    }).ConfigureAwait(false);
                }

                sqsTransportMessage.S3BodyKey = key;
                sqsTransportMessage.Body = string.Empty;
                serializedMessage = JsonConvert.SerializeObject(sqsTransportMessage);
            }

            await SendMessage(serializedMessage, transportOperation.Destination, delayDeliveryBy, transportOperation.Message.MessageId)
                .ConfigureAwait(false);
        }

        async Task SendMessage(string message, string destination, TimeSpan delayDeliveryBy, string messageId)
        {
            try
            {
                SendMessageRequest sendMessageRequest;

                if (configuration.IsDelayedDeliveryEnabled && delayDeliveryBy > configuration.DelayedDeliveryQueueDelayTime)
                {
                    destination += TransportConfiguration.DelayedDeliveryQueueSuffix;
                    var queueUrl = await queueUrlCache.GetQueueUrl(QueueNameHelper.GetSqsQueueName(destination, configuration))
                        .ConfigureAwait(false);

                    // TODO: add AWSConfigs.ClockOffset for clock skew (verify how it works)
                    sendMessageRequest = new SendMessageRequest(queueUrl, message)
                    {
                        MessageAttributes = new Dictionary<string, MessageAttributeValue>
                        {
                            [TransportHeaders.DelayDueTime] = new MessageAttributeValue
                            {
                                StringValue = DateTimeExtensions.ToWireFormattedString(DateTime.UtcNow + delayDeliveryBy),
                                DataType = "String"
                            }
                        },
                        MessageDeduplicationId = messageId,
                        MessageGroupId = messageId
                    };
                }
                else
                {
                    var queueUrl = await queueUrlCache.GetQueueUrl(QueueNameHelper.GetSqsQueueName(destination, configuration))
                        .ConfigureAwait(false);

                    sendMessageRequest = new SendMessageRequest(queueUrl, message);

                    var delaySeconds = Convert.ToInt32(Math.Ceiling(delayDeliveryBy.TotalSeconds));

                    if (delaySeconds > 0)
                    {
                        sendMessageRequest.DelaySeconds = delaySeconds;
                    }
                }

                await sqsClient.SendMessageAsync(sendMessageRequest)
                    .ConfigureAwait(false);
            }
            catch (QueueDoesNotExistException e) when (destination.EndsWith(TransportConfiguration.DelayedDeliveryQueueSuffix, StringComparison.OrdinalIgnoreCase))
            {
                throw new QueueDoesNotExistException($"Destination '{destination}' doesn't support delayed messages longer than {configuration.DelayedDeliveryQueueDelayTime}. To enable support for longer delays, call '.UseTransport<SqsTransport>().UnrestrictedDelayedDelivery()' on the '{destination}' endpoint.", e);
            }
        }

        TransportConfiguration configuration;
        IAmazonSQS sqsClient;
        IAmazonS3 s3Client;
        QueueUrlCache queueUrlCache;

        static ILog Logger = LogManager.GetLogger(typeof(MessageDispatcher));
        static readonly TimeSpan awsMaximumQueueDelayTime = TimeSpan.FromMinutes(15);
    }
}