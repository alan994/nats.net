﻿// Copyright 2021 The NATS Authors
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Threading.Tasks;
using NATS.Client.Internals;
using static NATS.Client.ClientExDetail;
using static NATS.Client.Internals.Validator;

namespace NATS.Client.JetStream
{
    public class JetStream : JetStreamBase, IJetStream
    {
        protected internal JetStream(IConnection connection, JetStreamOptions options) : base(connection, options) {}

        private MsgHeader MergePublishOptions(MsgHeader headers, PublishOptions opts)
        {
            // never touch the user's original headers
            MsgHeader merged = headers == null ? null : new MsgHeader(headers);

            if (opts != null)
            {
                merged = MergeNum(merged, JetStreamConstants.ExpLastSeqHeader, opts.ExpectedLastSeq);
                merged = MergeNum(merged, JetStreamConstants.ExpLastSubjectSeqHeader, opts.ExpectedLastSubjectSeq);
                merged = MergeString(merged, JetStreamConstants.ExpLastIdHeader, opts.ExpectedLastMsgId);
                merged = MergeString(merged, JetStreamConstants.ExpStreamHeader, opts.ExpectedStream);
                merged = MergeString(merged, JetStreamConstants.MsgIdHeader, opts.MessageId);
            }

            return merged;
        }

        private MsgHeader MergeNum(MsgHeader h, string key, ulong value)
        {
            return value > 0 ? _MergeString(h, key, value.ToString()) : h;
        }

        private MsgHeader MergeString(MsgHeader h, string key, string value) 
        {
            return string.IsNullOrWhiteSpace(value) ? h : _MergeString(h, key, value);
        }

        private MsgHeader _MergeString(MsgHeader h, string key, string value) 
        {
            if (h == null) {
                h = new MsgHeader();
            }
            h.Set(key, value);
            return h;
        }

        private PublishAck ProcessPublishResponse(Msg resp, PublishOptions options) {
            if (resp.HasStatus) {
                throw new NATSJetStreamException("Error Publishing: " + resp.Status.Message);
            }

            PublishAck ack = new PublishAck(resp);
            string ackStream = ack.Stream;
            string pubStream = options?.Stream;
            // stream specified in options but different than ack should not happen but...
            if (!string.IsNullOrWhiteSpace(pubStream) && pubStream != ackStream) {
                throw new NATSJetStreamException("Expected ack from stream " + pubStream + ", received from: " + ackStream);
            }
            return ack;
        }

        private PublishAck PublishSyncInternal(string subject, byte[] data, MsgHeader hdr, PublishOptions options)
        {
            MsgHeader merged = MergePublishOptions(hdr, options);
            Msg msg = new Msg(subject, null, merged, data);

            if (JetStreamOptions.IsPublishNoAck)
            {
                Conn.Publish(msg);
                return null;
            }
            
            return ProcessPublishResponse(Conn.Request(msg), options);
        }

        private async Task<PublishAck> PublishAsyncInternal(string subject, byte[] data, MsgHeader hdr, PublishOptions options)
        {
            MsgHeader merged = MergePublishOptions(hdr, options);
            Msg msg = new Msg(subject, null, merged, data);

            if (JetStreamOptions.IsPublishNoAck)
            {
                Conn.Publish(msg);
                return null;
            }

            Duration timeout = options == null ? JetStreamOptions.RequestTimeout : options.StreamTimeout;

            var result = await Conn.RequestAsync(msg, timeout.Millis).ConfigureAwait(false);
            return ProcessPublishResponse(result, options);
        }

        public PublishAck Publish(string subject, byte[] data) 
            => PublishSyncInternal(subject, data, null, null);

        public PublishAck Publish(string subject, byte[] data, PublishOptions options) 
            => PublishSyncInternal(subject, data, null, options);

        public PublishAck Publish(Msg msg)
            => PublishSyncInternal(msg.Subject, msg.Data, msg.Header, null);

        public PublishAck Publish(Msg msg, PublishOptions publishOptions)
            => PublishSyncInternal(msg.Subject, msg.Data, msg.Header, publishOptions);

        public Task<PublishAck> PublishAsync(string subject, byte[] data)
            => PublishAsyncInternal(subject, data, null, null);

        public Task<PublishAck> PublishAsync(string subject, byte[] data, PublishOptions publishOptions)
            => PublishAsyncInternal(subject, data, null, publishOptions);

        public Task<PublishAck> PublishAsync(Msg msg)
            => PublishAsyncInternal(msg.Subject, msg.Data, msg.Header, null);

        public Task<PublishAck> PublishAsync(Msg msg, PublishOptions publishOptions)
            => PublishAsyncInternal(msg.Subject, msg.Data, msg.Header, publishOptions);
        
        // ----------------------------------------------------------------------------------------------------
        // Subscribe
        // ----------------------------------------------------------------------------------------------------
        Subscription CreateSubscription(string subject, string queueName,
            EventHandler<MsgHandlerEventArgs> userHandler, bool autoAck,
            PushSubscribeOptions pushSubscribeOptions, 
            PullSubscribeOptions pullSubscribeOptions)
        {
            // 1. Prepare for all the validation
            bool isPullMode = pullSubscribeOptions != null;

            SubscribeOptions so;
            string stream;
            string qgroup;
            ConsumerConfiguration userCC;

            if (isPullMode) {
                so = pullSubscribeOptions; // options must have already been checked to be non null
                stream = pullSubscribeOptions.Stream;

                userCC = so.ConsumerConfiguration;

                qgroup = null; // just to make compiler happy both paths set variable
                ValidateNotSupplied(userCC.DeliverGroup, JsSubPullCantHaveDeliverGroup);
                ValidateNotSupplied(userCC.DeliverSubject, JsSubPullCantHaveDeliverSubject);
            }
            else {
                so = pushSubscribeOptions ?? PushSubscribeOptions.Builder().Build();
                stream = so.Stream; // might be null, that's ok (see directBind)

                userCC = so.ConsumerConfiguration;

                ValidateNotSupplied(userCC.MaxPullWaiting, 0, JsSubPushCantHaveMaxPullWaiting);

                // figure out the queue name
                qgroup = ValidateMustMatchIfBothSupplied(userCC.DeliverGroup, queueName, JsSubQueueDeliverGroupMismatch);

                if (qgroup != null && string.IsNullOrWhiteSpace(userCC.DeliverGroup)) {
                    // the queueName was provided versus the config deliver group, so the user config must be set
                    userCC = ConsumerConfiguration.Builder(userCC).WithDeliverGroup(qgroup).Build();
                }
            }
            
            // 2A. Flow Control / heartbeat not always valid
            if (userCC.FlowControl || userCC.IdleHeartbeat.Millis > 0) {
                if (isPullMode) {
                    throw JsSubFcHbNotValidPull.Instance();
                }
                if (qgroup != null) {
                    throw JsSubFcHbHbNotValidQueue.Instance();
                }
            }

            // 2B. Did they tell me what stream? No? look it up.
            if (string.IsNullOrWhiteSpace(stream)) {
                stream = LookupStreamBySubject(subject);
                if (stream == null) {
                    throw JsSubNoMatchingStreamForSubject.Instance();
                }
            }

            ConsumerConfiguration serverCc = null;
            String consumerName = userCC.Durable;
            String inboxDeliver = userCC.DeliverSubject;
            
        // 3. Does this consumer already exist?
        if (consumerName != null) {
            ConsumerInfo serverInfo = LookupConsumerInfo(stream, consumerName);

            if (serverInfo != null) { // the consumer for that durable already exists
                serverCc = serverInfo.ConsumerConfiguration;

                if (isPullMode) {
                    if (!string.IsNullOrWhiteSpace(serverCc.DeliverSubject)) {
                        throw JsSubConsumerAlreadyConfiguredAsPush.Instance();
                    }
                }
                else if (string.IsNullOrWhiteSpace(serverCc.DeliverSubject)) {
                    throw JsSubConsumerAlreadyConfiguredAsPull.Instance();
                }
                else if (inboxDeliver != null && !inboxDeliver.Equals(serverCc.DeliverSubject)) {
                    throw JsSubExistingDeliverSubjectMismatch.Instance();
                }

                // durable already exists, make sure the filter subject matches
                String userFilterSubject = userCC.FilterSubject;
                if (userFilterSubject != null && !userFilterSubject.Equals(serverCc.FilterSubject)) {
                    throw JsSubSubjectDoesNotMatchFilter.Instance();
                }

                if (string.IsNullOrWhiteSpace(serverCc.DeliverGroup)) {
                    // lookedUp was null/empty, means existing consumer is not a queue consumer
                    if (qgroup == null) {
                        // ok fine, no queue requested and the existing consumer is also not a queue consumer
                        // we must check if the consumer is in use though
                        if (serverInfo.PushBound) {
                            throw JsSubConsumerAlreadyBound.Instance();
                        }
                    }
                    else { // else they requested a queue but this durable was not configured as queue
                        throw JsSubExistingConsumerNotQueue.Instance();
                    }
                }
                else if (qgroup == null) {
                    throw JsSubExistingConsumerIsQueue.Instance();
                }
                else if (!serverCc.DeliverGroup.Equals(qgroup)) {
                    throw JsSubExistingQueueDoesNotMatchRequestedQueue.Instance();
                }

                // check to see if the user sent a different version than the server has
                // modifications are not allowed
                // previous checks for deliver subject and filter subject matching are now
                // in the changes function
                if (UserIsModifiedVsServer(userCC, serverCc)) {
                    throw JsSubExistingConsumerCannotBeModified.Instance();
                }

                inboxDeliver = serverCc.DeliverSubject; // use the deliver subject as the inbox. It may be null, that's ok, we'll fix that later
            }
            else if (so.Bind) {
                throw JsSubConsumerNotFoundRequiredInBind.Instance();
            }
        }

            // 4. If no deliver subject (inbox) provided or found, make an inbox.
            if (string.IsNullOrWhiteSpace(inboxDeliver)) {
                inboxDeliver = Conn.NewInbox();
            }

            // 5. If consumer does not exist, create
            if (serverCc == null) {
                ConsumerConfiguration.ConsumerConfigurationBuilder ccBuilder = ConsumerConfiguration.Builder(userCC);

                // Pull mode doesn't maintain a deliver subject. It's actually an error if we send it.
                if (!isPullMode) {
                    ccBuilder.WithDeliverSubject(inboxDeliver);
                }

                string userFilterSubject = ccBuilder.FilterSubject;
                ccBuilder.WithFilterSubject(string.IsNullOrWhiteSpace(userFilterSubject) ? subject : userFilterSubject);

                // createOrUpdateConsumer can fail for security reasons, maybe other reasons?
                ConsumerInfo ci = AddOrUpdateConsumerInternal(stream, ccBuilder.Build());
                consumerName = ci.Name;
                serverCc = ci.ConsumerConfiguration;
            }

            // 6. create the subscription
            IAutoStatusManager asm = isPullMode
                ? (IAutoStatusManager)new PullAutoStatusManager()
                : new PushAutoStatusManager((Connection) Conn, so, serverCc, qgroup != null, userHandler == null);
            
            Subscription sub;
            if (isPullMode)
            {
                SyncSubscription CreateSubDelegate(Connection lConn, string lSubject, string lQueueNa)
                {
                    return new JetStreamPullSubscription(lConn, lSubject, asm, this, stream, consumerName, inboxDeliver);
                }

                sub = ((Connection)Conn).subscribeSync(inboxDeliver, queueName, CreateSubDelegate);
            }
            else if (userHandler == null) {
                SyncSubscription CreateSubDelegate(Connection lConn, string lSubject, string lQueue)
                {
                    return new JetStreamPushSyncSubscription(lConn, lSubject, lQueue, asm, this, stream, consumerName, inboxDeliver);
                }
                
                sub = ((Connection)Conn).subscribeSync(inboxDeliver, queueName, CreateSubDelegate); 
            }
            else
            {
                EventHandler<MsgHandlerEventArgs> handler;
                if (autoAck && serverCc.AckPolicy != AckPolicy.None)
                {
                    handler = (sender, args) => 
                    {
                        if (asm.Manage(args.Message)) { return; } // manager handled the message
                        userHandler.Invoke(sender, args);
                        if (args.Message.LastAck == null || args.Message.LastAck == AckType.AckProgress)
                        {
                            args.Message.Ack();
                        }
                    };
                }
                else
                {
                    handler = (sender, args) => 
                    {
                        if (asm.Manage(args.Message)) { return; } // manager handled the message
                        userHandler.Invoke(sender, args);
                    };
                }

                AsyncSubscription CreateAsyncSubDelegate(Connection lConn, string lSubject, string lQueue)
                {
                    return new JetStreamPushAsyncSubscription(lConn, lSubject, lQueue, asm, this, stream, consumerName, inboxDeliver);
                }
                
                sub = ((Connection)Conn).subscribeAsync(inboxDeliver, queueName, handler, CreateAsyncSubDelegate);
            }

            asm.SetSub(sub);
            return sub;
        }

        private static bool UserIsModifiedVsServer(ConsumerConfiguration user, ConsumerConfiguration server)
        {
            return user.FlowControl != server.FlowControl
                   || user.DeliverPolicy != server.DeliverPolicy
                   || user.AckPolicy != server.AckPolicy
                   || user.ReplayPolicy != server.ReplayPolicy

                   || CcNumeric.StartSeq.NotEquals(user.StartSeq, server.StartSeq)
                   || CcNumeric.MaxDeliver.NotEquals(user.MaxDeliver, server.MaxDeliver)
                   || CcNumeric.RateLimit.NotEquals(user.RateLimit, server.RateLimit)
                   || CcNumeric.MaxAckPending.NotEquals(user.MaxAckPending, server.MaxAckPending)
                   || CcNumeric.MaxPullWaiting.NotEquals(user.MaxPullWaiting, server.MaxPullWaiting)

                   || !Equals(user.StartTime, server.StartTime)
                   || !Equals(user.AckWait, server.AckWait)
                   || !Equals(user.IdleHeartbeat, server.IdleHeartbeat)
                   || !Equals(EmptyAsNull(user.Description), EmptyAsNull(server.Description))
                   || !Equals(EmptyAsNull(user.SampleFrequency), EmptyAsNull(server.SampleFrequency));
        }
            
        // protected internal so can be tested
        protected internal ConsumerInfo LookupConsumerInfo(string lookupStream, string lookupConsumer) {
            try {
                return GetConsumerInfoInternal(lookupStream, lookupConsumer);
            }
            catch (NATSJetStreamException e) {
                if (e.ApiErrorCode == JetStreamConstants.JsConsumerNotFoundErr) {
                    return null;
                }
                throw;
            }
        }

        private string LookupStreamBySubject(string subject)
        {
            byte[] body = JsonUtils.SimpleMessageBody(ApiConstants.Subject, subject); 
            Msg resp = RequestResponseRequired(JetStreamConstants.JsapiStreamNames, body, Timeout);
            StreamNamesReader snr = new StreamNamesReader();
            snr.Process(resp);
            return snr.Strings.Count == 1 ? snr.Strings[0] : null; 
        }

        public IJetStreamPullSubscription PullSubscribe(string subject, PullSubscribeOptions options)
        {
            ValidateSubject(subject, true);
            ValidateNotNull(options, "PullSubscribeOptions");
            return (IJetStreamPullSubscription) CreateSubscription(subject, null, null, false, null, options);
        }

        public IJetStreamPushAsyncSubscription PushSubscribeAsync(string subject, EventHandler<MsgHandlerEventArgs> handler, bool autoAck)
        {
            ValidateSubject(subject, true);
            ValidateNotNull(handler, "Handler");
            return (IJetStreamPushAsyncSubscription) CreateSubscription(subject, null, handler, autoAck, null, null);
        }

        public IJetStreamPushAsyncSubscription PushSubscribeAsync(string subject, string queue, EventHandler<MsgHandlerEventArgs> handler, bool autoAck)
        {
            ValidateSubject(subject, true);
            queue = EmptyAsNull(ValidateQueueName(queue, false));
            ValidateNotNull(handler, "Handler");
            return (IJetStreamPushAsyncSubscription) CreateSubscription(subject, queue, handler, autoAck, null, null);
        }

        public IJetStreamPushAsyncSubscription PushSubscribeAsync(string subject, EventHandler<MsgHandlerEventArgs> handler, bool autoAck, PushSubscribeOptions options)
        {
            ValidateSubject(subject, true);
            ValidateNotNull(handler, "Handler");
            return (IJetStreamPushAsyncSubscription) CreateSubscription(subject, null, handler, autoAck, options, null);
        }

        public IJetStreamPushAsyncSubscription PushSubscribeAsync(string subject, string queue, EventHandler<MsgHandlerEventArgs> handler, bool autoAck, PushSubscribeOptions options)
        {
            ValidateSubject(subject, true);
            queue = EmptyAsNull(ValidateQueueName(queue, false));
            ValidateNotNull(handler, "Handler");
            return (IJetStreamPushAsyncSubscription) CreateSubscription(subject, queue, handler, autoAck, options, null);
        }

        public IJetStreamPushSyncSubscription PushSubscribeSync(string subject)
        {
            ValidateSubject(subject, true);
            return (IJetStreamPushSyncSubscription) CreateSubscription(subject, null, null, false, null, null);
        }

        public IJetStreamPushSyncSubscription PushSubscribeSync(string subject, PushSubscribeOptions options)
        {
            ValidateSubject(subject, true);
            return (IJetStreamPushSyncSubscription) CreateSubscription(subject, null, null, false, options, null);
        }

        public IJetStreamPushSyncSubscription PushSubscribeSync(string subject, string queue)
        {
            ValidateSubject(subject, true);
            queue = EmptyAsNull(ValidateQueueName(queue, false));
            return (IJetStreamPushSyncSubscription) CreateSubscription(subject, queue, null, false, null, null);
        }

        public IJetStreamPushSyncSubscription PushSubscribeSync(string subject, string queue, PushSubscribeOptions options)
        {
            ValidateSubject(subject, true);
            queue = EmptyAsNull(ValidateQueueName(queue, false));
            return (IJetStreamPushSyncSubscription) CreateSubscription(subject, queue, null, false, options, null);
        }
    }
}
