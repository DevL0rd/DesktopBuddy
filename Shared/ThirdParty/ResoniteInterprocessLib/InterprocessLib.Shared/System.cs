using Renderite.Shared;
using System.Reflection;

using System;
using System.Collections.Generic;
using System.IO;

#nullable enable

namespace InterprocessLib;

internal class MessagingQueue : IDisposable, IMemoryPackerEntityPool
{
	internal class OwnerData
	{
		public readonly Dictionary<Type, Dictionary<string, object?>> TypedCallbacks = new();
		public TypeManager OutgoingTypeManager;
		public TypeManager IncomingTypeManager;
		public readonly string Id;
		public bool Initialized;
		public MessagingQueue Queue {get; private set;}
		public OwnerData(string id, MessagingQueue queue)
		{
			Id = id;
			Queue = queue;
			OutgoingTypeManager = new(Queue._pool, OnOutgoingTypeRegistered);
			IncomingTypeManager = new(Queue._pool, null);
		}
		public void OnOutgoingTypeRegistered(Type type)
		{
			var typeRegCommand = new TypeRegistrationCommand();
			typeRegCommand.Type = type;
			Queue.SendPackable(Id, "", typeRegCommand);
		}
	}
	public bool IsAuthority { get; }

	public string QueueName { get; }

	public long QueueCapacity { get; }

	public int ReceivedMessages => _primary.ReceivedMessages;

	public int SentMessages => _primary.SentMessages;

	public bool IsConnected { get; private set; }

	public bool IsDisposed { get; private set; }

	private MessagingManager _primary;

	private static readonly MethodInfo _handlePackableMethod = typeof(MessagingQueue).GetMethod(nameof(HandlePackable), BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMethodException(nameof(HandlePackable));

	private Action<string>? _onWarning;

	private Action<string>? _onDebug;

	private Action<Exception>? _onFailure;

	private readonly Dictionary<string, OwnerData> _ownerData = new();

	private static readonly Dictionary<string, MessagingQueue> _registeredQueues = new();

	private IMemoryPackerEntityPool _pool;

	public void RegisterOwner(string ownerId)
	{
		_ownerData.Add(ownerId, new(ownerId, this));
	}

	public bool HasOwner(string ownerName)
	{
		return _ownerData.ContainsKey(ownerName);
	}

	public OwnerData GetOwnerData(string ownerId)
	{
		return _ownerData[ownerId];
	}

	public void RegisterCallback<T>(string owner, string id, Action<T>? callback) where T : IMemoryPackable
	{
		if(!_ownerData[owner].TypedCallbacks.ContainsKey(typeof(T)))
			_ownerData[owner].TypedCallbacks.Add(typeof(T), new());

		_ownerData[owner].TypedCallbacks[typeof(T)][id] = callback;
	}

	public MessagingQueue(bool isAuthority, string queueName, long queueCapacity, IMemoryPackerEntityPool pool, Action<Exception>? failhandler = null, Action<string>? warnHandler = null, Action<string>? debugHandler = null)
	{
		if (queueName is null) throw new ArgumentNullException(nameof(queueName));
		if (pool is null) throw new ArgumentNullException(nameof(pool));

		IsAuthority = isAuthority;
		QueueName = queueName;
		QueueCapacity = queueCapacity;

		_onDebug = debugHandler;
		_onWarning = warnHandler;
		_onFailure = failhandler;

		_pool = pool;

		_primary = new MessagingManager(this);
		_primary.CommandHandler = CommandHandler;
		_primary.FailureHandler = (ex) =>
		{
			_onFailure?.Invoke(ex);
		};
		_primary.WarningHandler = (msg) =>
		{
			_onWarning?.Invoke(msg);
		};

		_registeredQueues.Add(QueueName, this);
	}

	public void Connect()
	{
		if (IsConnected) throw new InvalidOperationException("Already connected!");

		new WrapperCommand(); // Initialize WrapperCommand static constructor (very important)
		_primary.Connect(QueueName, IsAuthority, QueueCapacity);
		IsConnected = true;
	}

	public void Dispose()
	{
		_primary.Dispose();
		IsDisposed = true;
	}

	internal static MessagingQueue? TryGetRegisteredQueue(string queueName)
	{
		if (_registeredQueues.TryGetValue(queueName, out var queue)) return queue;
		return null;
	}

	private void HandlePackable<T>(string ownerId, string id, T obj) where T : class, IMemoryPackable, new()
	{
		if (_ownerData[ownerId].TypedCallbacks.TryGetValue(typeof(T), out var data) && data.ContainsKey(id))
		{
			var callback = (Action<T>?)data[id];
			callback?.Invoke(obj);
		}
		else
		{
			_onWarning?.Invoke($"{QueueName}:{ownerId} Packable of type {typeof(T)} with Id {id} is not registered to receive a callback!");
		}
	}

	private void CommandHandler(RendererCommand command, int messageSize)
	{
		if (command is WrapperCommand wrapperCommand)
		{
			var ownerData = _ownerData[wrapperCommand.Owner!];

			IMemoryPackable packable = wrapperCommand.Packable!;
			_onDebug?.Invoke($"{QueueName}:{ownerData.Id} Received {wrapperCommand.Id} {packable}");

			if (packable is QueueOwnerInitCommand)
			{
				if (ownerData.Initialized)
				{
					_onWarning?.Invoke($"{QueueName}:{ownerData.Id} Received additional QueueOwnerInitCommand! Registered types will be reset!");
					ownerData.OutgoingTypeManager = new(this, ownerData.OnOutgoingTypeRegistered);
					ownerData.IncomingTypeManager = new(this, null);
				}
				else
				{
					ownerData.Initialized = true;
				}
			}
			else if (!ownerData.Initialized)
			{
				throw new InvalidDataException("QueueOwnerInitCommand needs to be first!");
			}
			else if (packable is TypeRegistrationCommand typeRegCommand)
			{
				if (typeRegCommand.Type is not null)
				{
					_onDebug?.Invoke($"{QueueName}:{ownerData.Id} * Registering incoming type: {typeRegCommand.Type.Name}<{string.Join(",", (IEnumerable<Type>)typeRegCommand.Type.GenericTypeArguments)}>");
					_ownerData[ownerData.Id!].IncomingTypeManager.InvokeRegisterType(typeRegCommand.Type);
				}
				else
				{
					throw new InvalidDataException("Other process tried to register a type that could not be found in this process!");
				}
			}
			else
			{
				_handlePackableMethod.MakeGenericMethod(packable.GetType()).Invoke(this, [ownerData.Id, wrapperCommand.Id, packable]);
			}
		}
		else
		{
			_onWarning?.Invoke($"{QueueName} Received an unexpected RendererCommand! {command}");
		}
	}

	public void SendPackable<T>(string ownerId, string id, T packable) where T : class, IMemoryPackable, new()
	{
		if (packable is null) throw new ArgumentNullException(nameof(packable));
		if (ownerId is null) throw new ArgumentNullException(nameof(ownerId));
		if (id is null) throw new ArgumentNullException(nameof(id));

		_onDebug?.Invoke($"{QueueName}:{ownerId} Sending: {id} {packable}");

		var ownerData = _ownerData[ownerId];

		if (!ownerData.OutgoingTypeManager.IsTypeRegistered<T>())
		{
			_onDebug?.Invoke($"{QueueName}:{ownerId} * Registering outgoing type: {typeof(T).Name}<{string.Join(",", (IEnumerable<Type>)typeof(T).GenericTypeArguments)}>");
			ownerData.OutgoingTypeManager.RegisterType<T>();
		}

		var wrapper = _pool.Borrow<WrapperCommand>();
		wrapper.TypeIndex = ownerData.OutgoingTypeManager.GetTypeIndex(typeof(T));
		wrapper.Owner = ownerId;
		wrapper.Id = id;
		wrapper.Packable = packable;

		_primary.SendCommand(wrapper);

		_pool.Return(wrapper);
	}

	public void UnregisterOwner(string ownerId)
	{
		if (HasOwner(ownerId))
		{
			_ownerData.Remove(ownerId);
		}
		else
		{
			throw new InvalidOperationException("Cannot unregister an owner who is not registered!");
		}
		if (_ownerData.Count == 0)
		{
			Dispose();
		}
	}

    T IMemoryPackerEntityPool.Borrow<T>()
    {
        return _pool.Borrow<T>();
    }

    void IMemoryPackerEntityPool.Return<T>(T value)
    {
        _pool.Return(value);
    }
}
