﻿using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Ncqrs.Domain;
using Ncqrs.Eventing.Sourcing.Snapshotting;

namespace Ncqrs.Eventing.Sourcing
{
	public abstract class EventSource : IEventSource
	{
		private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

		[NonSerialized]
		private Guid _eventSourceId;

		/// <summary>
		/// Gets the globally unique identifier.
		/// </summary>
		/// <exception cref="InvalidOperationException">Thrown when setting this
		/// value when the version of this aggregate root is not 0 or this
		/// instance contains are any uncommitted events.</exception>
		public Guid EventSourceId
		{
			get { return _eventSourceId; }
			protected set
			{
				Contract.Requires<InvalidOperationException>(Version == 0);
				_eventSourceId = value;
			}
		}

		/// <summary>
		/// Gets the current version of the instance as it is known in the event store.
		/// </summary>
		/// <value>
		/// An <see cref="long"/> representing the current version of this aggregate root.
		/// </value>
		public long Version
		{
			get
			{
				return _currentVersion;
			}
		}
		[NonSerialized]
		private long _initialVersion;

		[NonSerialized]
		private long _currentVersion;

		/// <summary>
		/// Gets the initial version.
		/// <para>
		/// This represents the current version of this instance. When this instance was retrieved
		/// via history, it contains the version as it was at that time. For new instances this value is always 0.
		/// </para>
		/// 	<para>
		/// The version does not change until changes are accepted via the <see cref="AcceptChanges"/> method.
		/// </para>
		/// </summary>
		/// <value>The initial version.</value>
		public long InitialVersion
		{
			get { return _initialVersion; }
		}

		/// <summary>
		/// A list that contains all the event handlers.
		/// </summary>
		[NonSerialized]
		private readonly Dictionary<Guid, ISourcedEventHandler> _eventHandlers = new Dictionary<Guid, ISourcedEventHandler>();
		[NonSerialized]
		private readonly IUniqueIdentifierGenerator _idGenerator;

		/// <summary>
		/// Initializes a new instance of the <see cref="EventSource"/> class.
		/// </summary>
		protected EventSource()
		{
			_idGenerator = NcqrsEnvironment.Get<IUniqueIdentifierGenerator>();
			EventSourceId = _idGenerator.GenerateNewId();
		}

		protected EventSource(Guid eventSourceId)
			: this()
		{
			EventSourceId = eventSourceId;
		}

		public virtual void InitializeFromSnapshot(Snapshot snapshot)
		{
			Contract.Requires<ArgumentNullException>(snapshot != null, "The snapshot cannot be null.");
			Log.DebugFormat("Initializing event source {0} from snapshot (version {1}).", snapshot.EventSourceId, snapshot.Version);

			_eventSourceId = snapshot.EventSourceId;
			_initialVersion = _currentVersion = snapshot.Version;
		}

		/// <summary>
		/// Initializes from history.
		/// </summary>
		/// <param name="history">The history.</param>
		public virtual void InitializeFromHistory(CommittedEventStream history)
		{
			Contract.Requires<ArgumentNullException>(history != null, "The history cannot be null.");
			if (_initialVersion != Version)
			{
				throw new InvalidOperationException("Cannot apply history when instance has uncommitted changes.");
			}
			Log.DebugFormat("Initializing event source {0} from history.", history.SourceId);
			if (history.IsEmpty)
			{
				return;
			}

			_eventSourceId = history.SourceId;

			foreach (var historicalEvent in history)
			{
				Log.DebugFormat("Appying historic event {0} to event source {1}", historicalEvent.EventIdentifier,
								history.SourceId);
				ApplyEventFromHistory(historicalEvent);
			}

			Log.DebugFormat("Finished initializing event source {0} from history. Current event source version is {1}",
							history.SourceId, history.CurrentSourceVersion);
			_initialVersion = history.CurrentSourceVersion;
		}

		public event EventHandler<EventAppliedEventArgs> EventApplied;

		protected virtual void OnEventApplied(UncommittedEvent evnt)
		{
			if (EventApplied != null)
			{
				EventApplied(this, new EventAppliedEventArgs(evnt));
			}
		}

		internal protected void RegisterHandler(ISourcedEventHandler handler)
		{
			Contract.Requires<ArgumentNullException>(handler != null, "The handler cannot be null.");
			var key = GetHashSum(handler);
			_eventHandlers[key] = handler;
		}

		protected virtual void HandleEvent(object evnt)
		{
			Contract.Requires<ArgumentNullException>(evnt != null, "The Event cannot be null.");
			Boolean handled = false;

			// Get a copy of the handlers because an event
			// handler can register a new handler. This will
			// cause the _eventHandlers list to be modified.
			// And modification while iterating it not allowed.
			var key = GetHashSum(evnt);
			ISourcedEventHandler handler = null;
			if (!_eventHandlers.TryGetValue(key, out handler))
			{
				//may be this is legacy entity event
				var sourcedEvent = evnt as IEntitySourcedEvent;
				if (sourcedEvent != null)
				{
					return;
				}
				throw new ApplicationException("Couldn't find handler");
			}

			Log.DebugFormat("Applying handler {0} of event source {1} to event {2}",
							handler, this, evnt);
			handled |= handler.HandleEvent(evnt);

			if (!handled)
				throw new EventNotHandledException(evnt);
		}

		protected Guid GetHashSum(object evnt)
		{
			var type = evnt.GetType().ToString();
			var sourcedEvent = evnt as IEntitySourcedEvent;
			if (sourcedEvent != null)
				return GetHashSum(type + sourcedEvent.EntityId.ToString());
			return GetHashSum(type + Guid.Empty.ToString());
		}

		protected Guid GetHashSum(ISourcedEventHandler handler)
		{
			return GetHashSum(handler.EventType.ToString() + handler.EntityId.ToString());
		}

		private Guid GetHashSum(string source)
		{
			var stringbytes = Encoding.UTF8.GetBytes(source);
			var hashedBytes = new SHA1CryptoServiceProvider().ComputeHash(stringbytes);
			Array.Resize(ref hashedBytes, 16);
			return new Guid(hashedBytes);
		}

		internal protected void ApplyEvent(object evnt)
		{
			Log.DebugFormat("Applying an event to event source {0}", evnt);
			var eventVersion = evnt.GetType().Assembly.GetName().Version;
			var eventSequence = GetNextSequence();
			//Legacy stuff...
			var sourcedEvent = evnt as ISourcedEvent;
			if (sourcedEvent != null)
			{
				sourcedEvent.ClaimEvent(EventSourceId, eventSequence);
			}
			var wrappedEvent = new UncommittedEvent(_idGenerator.GenerateNewId(), EventSourceId, eventSequence, _initialVersion, DateTime.UtcNow, evnt, eventVersion);


			Log.DebugFormat("Handling event {0} in event source {1}", wrappedEvent, this);
			HandleEvent(wrappedEvent.Payload);
			Log.DebugFormat("Notifying about application of an event {0} to event source {1}", wrappedEvent, this);
			OnEventApplied(wrappedEvent);
		}

		private long GetNextSequence()
		{

			// 628426 31 Feb 2011 - the following absolutely needed to ensure correct sequencing, as incorrect versions were being passed to event store
			// TODO: I don't think this should stay here
			if (_initialVersion > 0 && _currentVersion == 0)
			{
				_currentVersion = _initialVersion;
			}

			_currentVersion++;
			return _currentVersion;
		}

		private void ApplyEventFromHistory(CommittedEvent evnt)
		{
			ValidateHistoricalEvent(evnt);
			Log.DebugFormat("Handling historical event {0} in event source {1}", evnt, this);
			HandleEvent(evnt.Payload);
			_currentVersion++;
		}

		private void ValidateHistoricalEvent(CommittedEvent evnt)
		{
			if (evnt.EventSourceId != EventSourceId)
			{
				var message = String.Format("Cannot apply historical event from other event source.");
				throw new InvalidOperationException(message);
			}

			// TODO: Do we really really need this check? Why don't we trust IEventStore?

			if (evnt.EventSequence != Version + 1)
			{
				var message = String.Format("Cannot apply event with sequence {0}. Since the initial version of the " +
											"aggregate root is {1}. Only an event with sequence number {2} can be applied.",
											evnt.EventSequence, Version, Version + 1);
				throw new InvalidOperationException(message);
			}
		}

		public void AcceptChanges()
		{
			Log.DebugFormat("Accepting changes done to event source {0} up to version {1}", this, Version);
			_initialVersion = Version;
		}

		public override string ToString()
		{
			return string.Format("{0}[{1}]", GetType().FullName, EventSourceId.ToString("D"));
		}
	}
}
