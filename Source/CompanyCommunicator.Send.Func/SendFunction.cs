// <copyright file="SendFunction.cs" company="Microsoft">
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// </copyright>

namespace Microsoft.Teams.Apps.CompanyCommunicator.Send.Func
{
    using System;
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Threading.Tasks;
    using AdaptiveCards;
    using Microsoft.Azure.WebJobs;
    using Microsoft.Bot.Builder;
    using Microsoft.Bot.Builder.Teams;
    using Microsoft.Bot.Schema;
    using Microsoft.Extensions.Localization;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Microsoft.Teams.Apps.CompanyCommunicator.Common.Extensions;
    using Microsoft.Teams.Apps.CompanyCommunicator.Common.Repositories.NotificationData;
    using Microsoft.Teams.Apps.CompanyCommunicator.Common.Repositories.SentNotificationData;
    using Microsoft.Teams.Apps.CompanyCommunicator.Common.Repositories.UserData;
    using Microsoft.Teams.Apps.CompanyCommunicator.Common.Resources;
    using Microsoft.Teams.Apps.CompanyCommunicator.Common.Services.AdaptiveCard;
    using Microsoft.Teams.Apps.CompanyCommunicator.Common.Services.MessageQueues.SendQueue;
    using Microsoft.Teams.Apps.CompanyCommunicator.Common.Services.Teams;
    using Microsoft.Teams.Apps.CompanyCommunicator.Send.Func.Services;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    /// <summary>
    /// Azure Function App triggered by messages from a Service Bus queue
    /// Used for sending messages from the bot.
    /// </summary>
    public class SendFunction
    {
        /// <summary>
        /// This is set to 10 because the default maximum delivery count from the service bus
        /// message queue before the service bus will automatically put the message in the Dead Letter
        /// Queue is 10.
        /// </summary>
        private static readonly int MaxDeliveryCountForDeadLetter = 10;

        private readonly int maxNumberOfAttempts;
        private readonly double sendRetryDelayNumberOfSeconds;
        private readonly INotificationService notificationService;
        private readonly ISendingNotificationDataRepository sendingNotificationDataRepository;
        private readonly INotificationDataRepository notificationDataRepository;
        private readonly IUserDataRepository userDataRepository;
        private readonly AdaptiveCardCreator adaptiveCardCreator;
        private readonly IMessageService messageService;
        private readonly ISendQueue sendQueue;
        private readonly IStringLocalizer<Strings> localizer;
        private readonly string appBaseUri;
        private readonly bool trackViewClickPII;

        /// <summary>
        /// Initializes a new instance of the <see cref="SendFunction"/> class.
        /// </summary>
        /// <param name="options">Send function options.</param>
        /// <param name="notificationService">The service to precheck and determine if the queue message should be processed.</param>
        /// <param name="messageService">Message service.</param>
        /// <param name="notificationRepo">Notification repository.</param>
        /// <param name="sendQueue">The send queue.</param>
        /// <param name="localizer">Localization service.</param>
        public SendFunction(
            IOptions<SendFunctionOptions> options,
            INotificationService notificationService,
            IMessageService messageService,
            ISendingNotificationDataRepository sendingNotificationDataRepository,
            INotificationDataRepository notificationDataRepository,
            IUserDataRepository userDataRepository,
            AdaptiveCardCreator adaptiveCardCreator,
            ISendQueue sendQueue,
            IStringLocalizer<Strings> localizer)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            this.maxNumberOfAttempts = options.Value.MaxNumberOfAttempts;
            this.sendRetryDelayNumberOfSeconds = options.Value.SendRetryDelayNumberOfSeconds;

            this.appBaseUri = options.Value.AppBaseUri;
            this.trackViewClickPII = options.Value.TrackViewClickPII;

            this.notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            this.messageService = messageService ?? throw new ArgumentNullException(nameof(messageService));
            this.sendingNotificationDataRepository = sendingNotificationDataRepository ?? throw new ArgumentNullException(nameof(sendingNotificationDataRepository));
            this.notificationDataRepository = notificationDataRepository ?? throw new ArgumentNullException(nameof(notificationDataRepository));
            this.userDataRepository = userDataRepository ?? throw new ArgumentNullException(nameof(userDataRepository));

            this.adaptiveCardCreator = adaptiveCardCreator ?? throw new ArgumentNullException(nameof(adaptiveCardCreator));
            this.sendQueue = sendQueue ?? throw new ArgumentNullException(nameof(sendQueue));
            this.localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        }

        /// <summary>
        /// Azure Function App triggered by messages from a Service Bus queue
        /// Used for sending messages from the bot.
        /// </summary>
        /// <param name="myQueueItem">The Service Bus queue item.</param>
        /// <param name="deliveryCount">The deliver count.</param>
        /// <param name="enqueuedTimeUtc">The enqueued time.</param>
        /// <param name="messageId">The message ID.</param>
        /// <param name="log">The logger.</param>
        /// <param name="context">The execution context.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        [FunctionName("SendMessageFunction")]
        public async Task Run(
            [ServiceBusTrigger(
                SendQueue.QueueName,
                Connection = SendQueue.ServiceBusConnectionConfigurationKey)]
            string myQueueItem,
            int deliveryCount,
            DateTime enqueuedTimeUtc,
            string messageId,
            ILogger log,
            ExecutionContext context)
        {
            log.LogInformation($"C# ServiceBus queue trigger function processed message: {myQueueItem}");

            var messageContent = JsonConvert.DeserializeObject<SendQueueMessageContent>(myQueueItem);

            try
            {
                // Check if notification is canceled.
                var isCanceled = await this.notificationService.IsNotificationCanceled(messageContent);
                if (isCanceled)
                {
                    // No-op in case notification is canceled.
                    return;
                }

                // Check if recipient is a guest user.
                if (messageContent.IsRecipientGuestUser())
                {
                    await this.notificationService.UpdateSentNotification(
                        notificationId: messageContent.NotificationId,
                        activityId: string.Empty,
                        recipientId: messageContent.RecipientData.RecipientId,
                        totalNumberOfSendThrottles: 0,
                        statusCode: SentNotificationDataEntity.NotSupportedStatusCode,
                        allSendStatusCodes: $"{SentNotificationDataEntity.NotSupportedStatusCode},",
                        errorMessage: this.localizer.GetString("GuestUserNotSupported"));
                    return;
                }

                // Check if notification is pending.
                var isPending = await this.notificationService.IsPendingNotification(messageContent);
                if (!isPending)
                {
                    // Notification is either already sent or failed and shouldn't be retried.
                    return;
                }

                // Check if conversationId is set to send message.
                if (string.IsNullOrWhiteSpace(messageContent.GetConversationId()))
                {
                    await this.notificationService.UpdateSentNotification(
                        notificationId: messageContent.NotificationId,
                        activityId: string.Empty,
                        recipientId: messageContent.RecipientData.RecipientId,
                        totalNumberOfSendThrottles: 0,
                        statusCode: SentNotificationDataEntity.FinalFaultedStatusCode,
                        allSendStatusCodes: $"{SentNotificationDataEntity.FinalFaultedStatusCode},",
                        errorMessage: this.localizer.GetString("AppNotInstalled"));
                    return;
                }

                // Check if the system is throttled.
                var isThrottled = await this.notificationService.IsSendNotificationThrottled();
                if (isThrottled)
                {
                    // Re-Queue with delay.
                    await this.sendQueue.SendDelayedAsync(messageContent, this.sendRetryDelayNumberOfSeconds);
                    return;
                }

                // Send message.
                var messageActivity = await this.GetMessageActivity(messageContent);

                var response = await this.messageService.SendMessageAsync(
                    message: messageActivity,
                    serviceUrl: messageContent.GetServiceUrl(),
                    conversationId: messageContent.GetConversationId(),
                    maxAttempts: this.maxNumberOfAttempts,
                    logger: log);

                // Process response.
                await this.ProcessResponseAsync(messageContent, response, log);
            }
            catch (InvalidOperationException exception)
            {
                // Bad message shouldn't be requeued.
                log.LogError(exception, $"InvalidOperationException thrown. Error message: {exception.Message}");
            }
            catch (Exception exception)
            {
                var exceptionMessage = $"{exception.GetType()}: {exception.Message}";
                log.LogError(exception, $"Failed to send message. ErrorMessage: {exceptionMessage}");

                // Update status code depending on delivery count.
                var statusCode = SentNotificationDataEntity.FaultedAndRetryingStatusCode;
                if (deliveryCount >= SendFunction.MaxDeliveryCountForDeadLetter)
                {
                    // Max deliveries attempted. No further retries.
                    statusCode = SentNotificationDataEntity.FinalFaultedStatusCode;
                }

                // Update sent notification table.
                await this.notificationService.UpdateSentNotification(
                    notificationId: messageContent.NotificationId,
                    activityId: string.Empty,
                    recipientId: messageContent.RecipientData.RecipientId,
                    totalNumberOfSendThrottles: 0,
                    statusCode: statusCode,
                    allSendStatusCodes: $"{statusCode},",
                    errorMessage: this.localizer.GetString("Failed"),
                    exception: exception.ToString());

                throw;
            }
        }

        /// <summary>
        /// Process send notification response.
        /// </summary>
        /// <param name="messageContent">Message content.</param>
        /// <param name="sendMessageResponse">Send notification response.</param>
        /// <param name="log">Logger.</param>
        private async Task ProcessResponseAsync(
            SendQueueMessageContent messageContent,
            SendMessageResponse sendMessageResponse,
            ILogger log)
        {
            var statusReason = string.Empty;
            if (sendMessageResponse.ResultType == SendMessageResult.Succeeded)
            {
                log.LogInformation($"Successfully sent the message." +
                    $"\nRecipient Id: {messageContent.RecipientData.RecipientId}");
            }
            else
            {
                log.LogError($"Failed to send message." +
                    $"\nRecipient Id: {messageContent.RecipientData.RecipientId}" +
                    $"\nResult: {sendMessageResponse.ResultType}." +
                    $"\nErrorMessage: {sendMessageResponse.ErrorMessage}.");

                statusReason = this.localizer.GetString("Failed");
            }

            await this.notificationService.UpdateSentNotification(
                    notificationId: messageContent.NotificationId,
                    activityId: sendMessageResponse.ActivityId,
                    recipientId: messageContent.RecipientData.RecipientId,
                    totalNumberOfSendThrottles: sendMessageResponse.TotalNumberOfSendThrottles,
                    statusCode: sendMessageResponse.StatusCode,
                    allSendStatusCodes: sendMessageResponse.AllSendStatusCodes,
                    errorMessage: statusReason,
                    exception: sendMessageResponse.ErrorMessage);

            // Throttled
            if (sendMessageResponse.ResultType == SendMessageResult.Throttled)
            {
                // Set send function throttled.
                await this.notificationService.SetSendNotificationThrottled(this.sendRetryDelayNumberOfSeconds);

                // Requeue.
                await this.sendQueue.SendDelayedAsync(messageContent, this.sendRetryDelayNumberOfSeconds);
                return;
            }
        }

        private async Task<IMessageActivity> GetMessageActivity(SendQueueMessageContent message)
        {
            var notification = await this.notificationDataRepository.GetAsync(NotificationDataTableNames.SentNotificationsPartition, message.NotificationId);

            if (notification.MessageType == "CustomAC")
            {
                var customCard = await this.notificationDataRepository.GetCustomAdaptiveCardAsync(notification.Summary);
                var adaptiveCard = AdaptiveCard.FromJson(customCard);
                var acAttachment = new Attachment()
                {
                    ContentType = AdaptiveCard.ContentType,
                    Content = JsonConvert.DeserializeObject(adaptiveCard.Card.ToJson()),
                };
                var msg = MessageFactory.Attachment(acAttachment);
                if (message.RecipientData.RecipientType == RecipientDataType.User && notification.NotifyUser)
                {
                    msg.TeamsNotifyUser();
                }

                return msg;
            }

            // Download base64 data from blob convert to base64 string.
            if (!string.IsNullOrEmpty(notification.ImageBase64BlobName))
            {
                notification.ImageLink = await this.notificationDataRepository.GetImageAsync(notification.ImageLink, notification.ImageBase64BlobName);
            }

            List<Common.Services.AdaptiveCard.Entity> mentions = null;
            if (message.RecipientData.RecipientType == RecipientDataType.User && !string.IsNullOrWhiteSpace(notification.Summary) && notification.Summary.Contains("[user]"))
            {
                var userDataEntity = await this.userDataRepository.GetAsync(UserDataTableNames.UserDataPartition, message.RecipientData.RecipientId);
                var atUser = string.Format("<at>{0}</at>", userDataEntity.Name);
                notification.Summary = notification.Summary.Replace("[user]", atUser);
                var mentionEntity = new Common.Services.AdaptiveCard.Entity()
                {
                    type = "mention",
                    text = atUser,
                    mentioned = new Mentioned()
                    {
                        id = message.RecipientData.RecipientId,
                        name = userDataEntity.Name,
                    },
                };
                mentions = new List<Common.Services.AdaptiveCard.Entity>() { mentionEntity };
            }

            var card = this.adaptiveCardCreator.CreateAdaptiveCard(notification);
            if (mentions != null && notification.FullWidth)
            {
                card.AdditionalProperties.Add("msteams", new { width = "full", entities = mentions });
            }
            else if (mentions == null && notification.FullWidth)
            {
                card.AdditionalProperties.Add("msteams", new { width = "full" });
            }
            else if (mentions != null && !notification.FullWidth)
            {
                card.AdditionalProperties.Add("msteams", mentions);
            }

            if (message.RecipientData.RecipientType == RecipientDataType.User)
            {
                var uniqueUser = this.trackViewClickPII ? message.RecipientData.RecipientId : Guid.NewGuid().ToString();

                // TODO: do we need to encode this URI?
                // TODO: use & instead of custom delimiter? or it will be failed for Teams AC ????
                var trackImageUrl = $"{this.appBaseUri}/track?url={message.NotificationId}-{uniqueUser}.gif";

                var pixel = new AdaptiveImage()
                {
                    Url = new Uri(trackImageUrl, UriKind.RelativeOrAbsolute),
                    Spacing = AdaptiveSpacing.None,
                    AltText = string.Empty,
                };
                pixel.PixelHeight = 1;
                pixel.PixelWidth = 1;
                card.Body.Add(pixel);

                string buttonUrl = string.Empty;
                for (var i = 0; i < card.Actions.Count; i++)
                {
                    AdaptiveOpenUrlAction action = card.Actions[i] as AdaptiveOpenUrlAction;
                    if (action != null)
                    {
                        buttonUrl = $"{this.appBaseUri}/redirect?url={action.Url}&id={message.NotificationId}&userId={uniqueUser}";
                        action.Url = new Uri(buttonUrl, UriKind.RelativeOrAbsolute);
                    }
                }

                if (!string.IsNullOrWhiteSpace(buttonUrl))
                {
                    for (var i = 0; i < card.Actions.Count; i++)
                    {
                        AdaptiveSubmitAction submitAction = card.Actions[i] as AdaptiveSubmitAction;
                        if (submitAction != null)
                        {
                            submitAction.DataJson = JsonConvert.SerializeObject(
                            new { notificationId = notification.Id, trackClickUrl = buttonUrl });
                        }
                    }
                }
            }

            var adaptiveCardAttachment = new Attachment()
            {
                ContentType = AdaptiveCard.ContentType,
                Content = JsonConvert.DeserializeObject(card.ToJson()),
            };
            var messageActivity = MessageFactory.Attachment(adaptiveCardAttachment);

            #region Experimental options

            if (!string.IsNullOrWhiteSpace(notification.Author) && notification.OnBehalfOf)
            {
                var onBehalfOf = new OnBehalfOfEntity[1];
                onBehalfOf[0] = new OnBehalfOfEntity()
                {
                    ItemId = 0,
                    MentionType = "person",
                    Mri = "29:orgid:" + message.RecipientData.RecipientId,
                    DisplayName = notification.Author,
                };

                messageActivity.ChannelData = JObject.FromObject(new
                {
                    OnBehalfOf = onBehalfOf,
                });
            }
            #endregion

            messageActivity.Summary = notification.Title;
            if (message.RecipientData.RecipientType == RecipientDataType.User && notification.NotifyUser)
            {
                messageActivity.TeamsNotifyUser();
            }

            return messageActivity;
        }
    }
}
