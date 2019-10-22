﻿using EventStore.ClientAPI;
using EventStore.ClientAPI.SystemData;
using EventStore.Core.Tests.ClientAPI.Helpers;
using EventStore.Core.Tests.Helpers;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services;

namespace EventStore.Core.Tests.ClientAPI {
	[TestFixture]
	[Category("LongRunning"), Category("ClientAPI")]
	public class catchup_filtered_subscription : SpecificationWithDirectory {
		private MiniNode _node;
		private IEventStoreConnection _conn;
		private List<EventData> _testEvents;
		private List<EventData> _testEventsAfter;
		private const int Timeout = 10000;

		[SetUp]
		public override void SetUp() {
			base.SetUp();
			_node = new MiniNode(PathName);
			_node.Start();

			_conn = BuildConnection(_node);
			_conn.ConnectAsync().Wait();
			_conn.SetStreamMetadataAsync("$all", -1,
				StreamMetadata.Build().SetReadRole(SystemRoles.All),
				new UserCredentials(SystemUsers.Admin, SystemUsers.DefaultAdminPassword)).Wait();

			_testEvents = Enumerable
				.Range(0, 10)
				.Select(x => TestEvent.NewTestEvent(x.ToString(), eventName: "AEvent"))
				.ToList();

			_testEvents.AddRange(
				Enumerable
					.Range(0, 10)
					.Select(x => TestEvent.NewTestEvent(x.ToString(), eventName: "BEvent")).ToList());

			_testEventsAfter = Enumerable
				.Range(0, 10)
				.Select(x => TestEvent.NewTestEvent(x.ToString(), eventName: "AEvent"))
				.ToList();

			_testEventsAfter.AddRange(
				Enumerable
					.Range(0, 10)
					.Select(x => TestEvent.NewTestEvent(x.ToString(), eventName: "BEvent")).ToList());

			_conn.AppendToStreamAsync("stream-a", ExpectedVersion.NoStream, _testEvents.EvenEvents()).Wait();
			_conn.AppendToStreamAsync("stream-b", ExpectedVersion.NoStream, _testEvents.OddEvents()).Wait();
		}

		protected virtual IEventStoreConnection BuildConnection(MiniNode node) {
			return TestConnection.Create(node.TcpEndPoint);
		}

		[Test]
		public void calls_checkpoint_delegate_during_catchup() {
			var filter = Filter.StreamId.Prefix("stream-a");
			var appeared = new CountdownEvent(5);
			var eventsSeen = 0;

			_conn.SubscribeToAllFilteredFrom(
				Position.Start,
				filter,
				CatchUpSubscriptionFilteredSettings.Default,
				(s, e) => {
					eventsSeen++;
					return Task.CompletedTask;
				},
				(s, p) => {
					appeared.Signal();
					return Task.CompletedTask;
				}, 2);

			if (!appeared.Wait(Timeout)) {
				Assert.Fail("Checkpoint appeared not called enough times within time limit.");
			}

			Assert.AreEqual(10, eventsSeen);
		}

		[Test]
		public void calls_checkpoint_during_live_processing_stage() {
			var filter = Filter.StreamId.Prefix("stream-a");
			var appeared = new CountdownEvent(6);
			var eventsSeen = 0;
			var isLive = false;

			_conn.SubscribeToAllFilteredFrom(
				Position.Start,
				filter,
				CatchUpSubscriptionFilteredSettings.Default,
				(s, e) => {
					eventsSeen++;
					return Task.CompletedTask;
				},
				(s, p) => {
					if (isLive) {
						appeared.Signal();
					}
					return Task.CompletedTask;
				}, 2, 
				s => {
					isLive = true;
					_conn.AppendToStreamAsync("stream-a", ExpectedVersion.Any, _testEventsAfter.EvenEvents()).Wait();
				});

			if (!appeared.Wait(Timeout)) {
				Assert.Fail("Checkpoint appeared not called enough times within time limit.");
			}

			Assert.AreEqual(20, eventsSeen);
		}

		[Test, Category("LongRunning")]
		public void only_return_events_with_a_given_stream_prefix() {
			var filter = Filter.StreamId.Prefix("stream-a");
			var foundEvents = new ConcurrentBag<ResolvedEvent>();
			var appeared = new CountdownEvent(20);

			Subscribe(filter, foundEvents, appeared);

			if (!appeared.Wait(Timeout)) {
				Assert.Fail("Appeared countdown event timed out.");
			}

			Assert.True(foundEvents.All(e => e.Event.EventStreamId == "stream-a"));
		}

		[Test, Category("LongRunning")]
		public void only_return_events_with_a_given_event_prefix() {
			var filter = Filter.EventType.Prefix("AE");
			var foundEvents = new ConcurrentBag<ResolvedEvent>();
			var appeared = new CountdownEvent(20);

			Subscribe(filter, foundEvents, appeared);

			if (!appeared.Wait(Timeout)) {
				Assert.Fail("Appeared countdown event timed out.");
			}

			Assert.True(foundEvents.All(e => e.Event.EventType == "AEvent"));
		}

		[Test, Category("LongRunning")]
		public void only_return_events_that_satisfy_a_given_stream_regex() {
			var filter = Filter.StreamId.Regex(new Regex(@"^.*eam-b.*$"));
			var foundEvents = new ConcurrentBag<ResolvedEvent>();
			var appeared = new CountdownEvent(20);

			Subscribe(filter, foundEvents, appeared);

			if (!appeared.Wait(Timeout)) {
				Assert.Fail("Appeared countdown event timed out.");
			}

			Assert.True(foundEvents.All(e => e.Event.EventStreamId == "stream-b"));
		}

		[Test, Category("LongRunning")]
		public void only_return_events_that_satisfy_a_given_event_regex() {
			var filter = Filter.EventType.Regex(new Regex(@"^.*BEv.*$"));
			var foundEvents = new ConcurrentBag<ResolvedEvent>();
			var appeared = new CountdownEvent(20);

			Subscribe(filter, foundEvents, appeared);

			if (!appeared.Wait(Timeout)) {
				Assert.Fail("Appeared countdown event timed out.");
			}

			Assert.True(foundEvents.All(e => e.Event.EventType == "BEvent"));
		}

		[Test, Category("LongRunning")]
		public void only_return_events_that_are_not_system_events() {
			var filter = Filter.ExcludeSystemEvents;
			var foundEvents = new ConcurrentBag<ResolvedEvent>();
			var appeared = new CountdownEvent(20);

			Subscribe(filter, foundEvents, appeared);

			if (!appeared.Wait(Timeout)) {
				Assert.Fail("Appeared countdown event timed out.");
			}

			Assert.True(foundEvents.All(e => !e.Event.EventType.StartsWith("$")));
		}

		private void Subscribe(Filter filter, ConcurrentBag<ResolvedEvent> foundEvents, CountdownEvent appeared) {
			_conn.SubscribeToAllFilteredFrom(
				Position.Start,
				filter,
				CatchUpSubscriptionFilteredSettings.Default,
				(s, e) => {
					foundEvents.Add(e);
					appeared.Signal();
					return Task.CompletedTask;
				},
				(s, p) => Task.CompletedTask, 5,
				s => {
					_conn.AppendToStreamAsync("stream-a", ExpectedVersion.Any, _testEventsAfter.EvenEvents()).Wait();
					_conn.AppendToStreamAsync("stream-b", ExpectedVersion.Any, _testEventsAfter.OddEvents()).Wait();
				});
		}

		[TearDown]
		public override void TearDown() {
			_conn.Close();
			_node.Shutdown();
			base.TearDown();
		}
	}
}
