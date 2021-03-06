using System;
using System.Security.Principal;
using System.Threading.Tasks;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Client;
using EventStore.Client.PersistentSubscriptions;
using Grpc.Core;

namespace EventStore.Core.Services.Transport.Grpc {
	partial class PersistentSubscriptions {
		public override async Task<UpdateResp> Update(UpdateReq request, ServerCallContext context) {
			var updatePersistentSubscriptionSource = new TaskCompletionSource<UpdateResp>();
			var settings = request.Options.Settings;
			var correlationId = Guid.NewGuid();

			var user = await GetUser(_authenticationProvider, context.RequestHeaders).ConfigureAwait(false);

			_queue.Publish(new ClientMessage.UpdatePersistentSubscription(
				correlationId,
				correlationId,
				new CallbackEnvelope(HandleUpdatePersistentSubscriptionCompleted),
				request.Options.StreamName,
				request.Options.GroupName,
				settings.ResolveLinks,
				new StreamRevision(settings.Revision).ToInt64(),
				(int)TimeSpan.FromTicks(settings.MessageTimeout).TotalMilliseconds,
				settings.ExtraStatistics,
				settings.MaxRetryCount,
				settings.HistoryBufferSize,
				settings.LiveBufferSize,
				settings.ReadBatchSize,
				(int)TimeSpan.FromTicks(settings.CheckpointAfter).TotalMilliseconds,
				settings.MinCheckpointCount,
				settings.MaxCheckpointCount,
				settings.MaxSubscriberCount,
				settings.NamedConsumerStrategy.ToString(),
				user,
				default,
				default));

			return await updatePersistentSubscriptionSource.Task.ConfigureAwait(false);

			void HandleUpdatePersistentSubscriptionCompleted(Message message) {
				if (message is ClientMessage.NotHandled notHandled && RpcExceptions.TryHandleNotHandled(notHandled, out var ex)) {
					updatePersistentSubscriptionSource.TrySetException(ex);
					return;
				}

				if (!(message is ClientMessage.UpdatePersistentSubscriptionCompleted completed)) {
					updatePersistentSubscriptionSource.TrySetException(
						RpcExceptions.UnknownMessage<ClientMessage.UpdatePersistentSubscriptionCompleted>(message));
					return;
				}

				switch (completed.Result) {
					case ClientMessage.UpdatePersistentSubscriptionCompleted.UpdatePersistentSubscriptionResult.Success:
						updatePersistentSubscriptionSource.TrySetResult(new UpdateResp());
						return;
					case ClientMessage.UpdatePersistentSubscriptionCompleted.UpdatePersistentSubscriptionResult.Fail:
						updatePersistentSubscriptionSource.TrySetException(RpcExceptions.PersistentSubscriptionFailed(request.Options.StreamName, request.Options.GroupName, completed.Reason));
						return;
					case ClientMessage.UpdatePersistentSubscriptionCompleted.UpdatePersistentSubscriptionResult
						.AccessDenied:
						updatePersistentSubscriptionSource.TrySetException(RpcExceptions.AccessDenied());
						return;
					default:
						updatePersistentSubscriptionSource.TrySetException(RpcExceptions.UnknownError(completed.Result));
						return;
				}
			}
		}
	}
}
